import type { Scenario, ScenarioNode } from './types';
import { computeDispatchWaves, isDispatchNode, maxDispatchWave } from './waves';

/**
 * Deterministic layered-DAG layout for the landing scenario theater.
 *
 * The old player multiplied hand-authored `col * COL_STEP` / `row * ROW_STEP`
 * constants, which produced sparse, right-clustered graphs with big blank
 * quadrants. This helper instead derives a compact left-to-right layered layout
 * from the scenario's ROLES and dependency EDGES:
 *
 *  - Columns (x) come from the structural spine + dispatch waves:
 *      coordinator → 0, outcome_plan → 1, work_plan → 2,
 *      each specialist → 2 + its dependency wave, the review gate → last column.
 *    Specialists that run concurrently share a wave, so they land in the SAME
 *    column and stack vertically as an aligned "wave".
 *  - Rows (y) come from a Sugiyama-style barycenter relaxation: every node is
 *    pulled toward the average height of its neighbours, then each column is
 *    packed to a minimum vertical gap via isotonic (pool-adjacent-violators)
 *    regression that keeps the column centred on that barycenter. Chains such as
 *    design → build → tests therefore straighten into one horizontal track and
 *    the structural spine settles on the vertical centre line.
 *
 * The output is fully deterministic (fixed iteration count, no randomness) so it
 * can be asserted in unit tests, and the `row` field on a node is used only as a
 * stable ordering hint for which branch sits above which — never as a pixel
 * multiplier.
 */

/** Rendered width of a workflow node card (matches FIXED_NODE_W in dagLayout). */
export const LANDING_NODE_W = 184;
/** Rendered height of a workflow node card (header row). The model caption
 *  renders just below the card, which the row gap accounts for. */
export const LANDING_NODE_H = 60;
/** Horizontal gap between the right edge of one column and the left edge of the
 *  next. Kept tight so the (up to 7-column) spine stays compact. */
export const LANDING_COL_GAP = 58;
/** Minimum centre-to-centre vertical separation between two nodes in a column.
 *  Comfortably clears a node card + its model caption + breathing room. */
export const LANDING_ROW_GAP = 128;

const COL_STEP = LANDING_NODE_W + LANDING_COL_GAP;

/** Structural roles occupy fixed leading columns; everything else is derived. */
const STRUCTURAL_COLUMN: Record<string, number> = {
  coordinator: 0,
  outcome_plan: 1,
  work_plan: 2,
};

export interface NodePosition {
  x: number;
  y: number;
}

export interface GraphLayout {
  /** Top-left position of each node, keyed by node id (React Flow coordinates). */
  positions: Map<string, NodePosition>;
  /** Column index assigned to each node id. */
  columns: Map<string, number>;
  /** Bounding box of all node boxes. */
  width: number;
  height: number;
  nodeW: number;
  nodeH: number;
}

function isReviewNode(node: ScenarioNode): boolean {
  return Boolean(node.isReviewGate) || node.role === 'review';
}

/** Column index for a node, derived from its role or dependency wave. */
function columnForNode(
  node: ScenarioNode,
  waves: Map<string, number>,
  maxWave: number,
): number {
  const structural = STRUCTURAL_COLUMN[node.role];
  if (structural !== undefined) return structural;
  if (isReviewNode(node)) return 3 + maxWave;
  if (isDispatchNode(node)) return 2 + (waves.get(node.id) ?? 1);
  // Any other node type falls in just before review so edges stay forward.
  return 2 + maxWave;
}

/**
 * Isotonic (pool-adjacent-violators) packing.
 *
 * Given a column's nodes in top→bottom order and a desired centre for each,
 * returns final centres that are strictly non-decreasing with at least `gap`
 * separation while staying L2-closest to the desired positions. The column's
 * mean is preserved, so the packed column remains centred on its barycenter.
 */
function packColumn(desired: number[], gap: number): number[] {
  const n = desired.length;
  if (n <= 1) return desired.slice();

  // Remove the mandatory gap so the constraint becomes "non-decreasing".
  const z = desired.map((d, i) => d - i * gap);
  const blocks: { sum: number; count: number; mean: number }[] = [];
  for (let i = 0; i < n; i += 1) {
    let block = { sum: z[i], count: 1, mean: z[i] };
    while (blocks.length > 0 && blocks[blocks.length - 1].mean > block.mean) {
      const prev = blocks.pop()!;
      block = {
        sum: prev.sum + block.sum,
        count: prev.count + block.count,
        mean: (prev.sum + block.sum) / (prev.count + block.count),
      };
    }
    blocks.push(block);
  }

  const out: number[] = [];
  for (const block of blocks) {
    for (let k = 0; k < block.count; k += 1) out.push(block.mean);
  }
  return out.map((w, i) => w + i * gap);
}

const BARYCENTER_ITERATIONS = 16;

/**
 * Computes the compact layered layout for a scenario. Pure and deterministic.
 */
export function layoutScenarioGraph(scenario: Scenario): GraphLayout {
  const waves = computeDispatchWaves(scenario);
  const maxWave = maxDispatchWave(scenario);

  // --- Column assignment (x axis) ------------------------------------------
  const columns = new Map<string, number>();
  for (const node of scenario.nodes) {
    columns.set(node.id, columnForNode(node, waves, maxWave));
  }

  // Group node ids per column, ordered top→bottom by the authored `row` hint
  // (stable, tie-broken by declaration order) so parallel branches keep the
  // intended vertical arrangement.
  const declarationIndex = new Map<string, number>();
  scenario.nodes.forEach((node, index) => declarationIndex.set(node.id, index));
  const columnGroups = new Map<number, string[]>();
  for (const node of scenario.nodes) {
    const col = columns.get(node.id)!;
    const group = columnGroups.get(col) ?? [];
    group.push(node.id);
    columnGroups.set(col, group);
  }
  const rowHint = new Map<string, number>(scenario.nodes.map((n) => [n.id, n.row]));
  for (const group of columnGroups.values()) {
    group.sort((a, b) => {
      const ra = rowHint.get(a) ?? 0;
      const rb = rowHint.get(b) ?? 0;
      if (ra !== rb) return ra - rb;
      return (declarationIndex.get(a) ?? 0) - (declarationIndex.get(b) ?? 0);
    });
  }

  const sortedColumns = [...columnGroups.keys()].sort((a, b) => a - b);

  // Predecessor / successor adjacency (all node types).
  const preds = new Map<string, string[]>();
  const succs = new Map<string, string[]>();
  for (const node of scenario.nodes) {
    preds.set(node.id, []);
    succs.set(node.id, []);
  }
  for (const [, source, target] of scenario.edges) {
    if (succs.has(source)) succs.get(source)!.push(target);
    if (preds.has(target)) preds.get(target)!.push(source);
  }

  // --- Row assignment (y axis) via barycenter relaxation -------------------
  const y = new Map<string, number>();
  for (const group of columnGroups.values()) {
    const mid = (group.length - 1) / 2;
    group.forEach((id, i) => y.set(id, (i - mid) * LANDING_ROW_GAP));
  }

  const relax = (order: number[], neighbours: Map<string, string[]>) => {
    for (const col of order) {
      const group = columnGroups.get(col)!;
      const desired = group.map((id) => {
        const near = neighbours.get(id)!;
        if (near.length === 0) return y.get(id)!;
        const sum = near.reduce((acc, nId) => acc + (y.get(nId) ?? 0), 0);
        return sum / near.length;
      });
      const packed = packColumn(desired, LANDING_ROW_GAP);
      group.forEach((id, i) => y.set(id, packed[i]));
    }
  };

  const ascending = sortedColumns;
  const descending = [...sortedColumns].reverse();
  for (let iter = 0; iter < BARYCENTER_ITERATIONS; iter += 1) {
    relax(ascending, preds); // pull toward predecessors (left → right)
    relax(descending, succs); // pull toward successors (right → left)
  }

  // --- Normalise to a top-left origin --------------------------------------
  let minY = Infinity;
  let maxY = -Infinity;
  for (const value of y.values()) {
    minY = Math.min(minY, value);
    maxY = Math.max(maxY, value);
  }
  if (!Number.isFinite(minY)) {
    minY = 0;
    maxY = 0;
  }

  const positions = new Map<string, NodePosition>();
  for (const node of scenario.nodes) {
    const col = columns.get(node.id)!;
    const centreY = y.get(node.id)!;
    positions.set(node.id, {
      x: col * COL_STEP,
      y: centreY - minY,
    });
  }

  const maxCol = sortedColumns[sortedColumns.length - 1] ?? 0;
  const width = maxCol * COL_STEP + LANDING_NODE_W;
  const height = maxY - minY + LANDING_NODE_H;

  return {
    positions,
    columns,
    width,
    height,
    nodeW: LANDING_NODE_W,
    nodeH: LANDING_NODE_H,
  };
}
