import { layoutDagBalancedGrid, layoutDagColumns, layoutDagStaircase, NODE_H, NODE_W } from '../utils/dagLayout';
import { describe, expect, it } from 'vitest';
import type { NodeSizeHint } from '../utils/dagLayout';
import type { Edge, Node } from '@xyflow/react';
function makeNode(id: string): Node {
  return { id, position: { x: 0, y: 0 }, data: {} };
}

function centerX(node: Node, width = NODE_W): number {
  return node.position.x + width / 2;
}

describe('layoutDagColumns TB centering', () => {
  it('centres a single-node spine rank over a multi-node fan-out rank on a shared axis', () => {
    // coordinator → {a, b, c} fan-out
    const nodes: Node[] = [
      makeNode('coordinator'),
      makeNode('a'),
      makeNode('b'),
      makeNode('c'),
    ];
    const edges: Edge[] = [
      { id: 'e1', source: 'coordinator', target: 'a' },
      { id: 'e2', source: 'coordinator', target: 'b' },
      { id: 'e3', source: 'coordinator', target: 'c' },
    ];

    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    const byId = new Map(laid.map((n) => [n.id, n]));

    const coord = byId.get('coordinator')!;
    const a = byId.get('a')!;
    const c = byId.get('c')!;

    // The fan-out row's centre is the midpoint between its first and last cards.
    const fanCenter = (centerX(a) + centerX(c)) / 2;
    // The single spine node must sit on the same centre axis.
    expect(Math.abs(centerX(coord) - fanCenter)).toBeLessThanOrEqual(1);
  });

  it('keeps a linear spine (coordinator→outcome→work) vertically aligned', () => {
    const nodes: Node[] = [
      makeNode('coordinator'),
      makeNode('outcome'),
      makeNode('work'),
    ];
    const edges: Edge[] = [
      { id: 'e1', source: 'coordinator', target: 'outcome' },
      { id: 'e2', source: 'outcome', target: 'work' },
    ];

    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    const xs = laid.map((n) => centerX(n));
    // All three single-node ranks share one centre X.
    expect(Math.max(...xs) - Math.min(...xs)).toBeLessThanOrEqual(1);
  });

  it('never produces negative coordinates', () => {
    const nodes: Node[] = [makeNode('root'), makeNode('a'), makeNode('b')];
    const edges: Edge[] = [
      { id: 'e1', source: 'root', target: 'a' },
      { id: 'e2', source: 'root', target: 'b' },
    ];
    const laid = layoutDagColumns(nodes, edges, { rankdir: 'TB' });
    for (const n of laid) {
      expect(n.position.x).toBeGreaterThanOrEqual(0);
      expect(n.position.y).toBeGreaterThanOrEqual(0);
    }
  });
});

function makeEdge(source: string, target: string): Edge {
  return { id: `${source}-${target}`, source, target };
}

function centerY(node: Node, height = NODE_H): number {
  return node.position.y + height / 2;
}

function rounded(value: number): number {
  return Math.round(value);
}

function coordinatorWithSubtasks(count: number): { nodes: Node[]; edges: Edge[]; hints: Record<string, NodeSizeHint> } {
  const subtaskIds = Array.from({ length: count }, (_, index) => `subtask-${index + 1}`);
  const nodeIds = ['coordinator', 'outcome', 'work', ...subtaskIds, 'rai', 'review', 'merge', 'scribe'];
  const nodes = nodeIds.map(makeNode);
  const edges = [
    makeEdge('coordinator', 'outcome'),
    makeEdge('outcome', 'work'),
    ...subtaskIds.map((id) => makeEdge('work', id)),
    ...subtaskIds.map((id) => makeEdge(id, 'rai')),
    makeEdge('rai', 'review'),
    makeEdge('review', 'merge'),
    makeEdge('merge', 'scribe'),
  ];
  const hints = Object.fromEntries(
    nodeIds.map((id) => [id, { width: id.startsWith('subtask') ? 220 : 180, height: id.startsWith('subtask') ? 180 : 130 }]),
  );
  return { nodes, edges, hints };
}

describe('layoutDagBalancedGrid', () => {
  it('wraps 5+ subtasks into multiple rows and columns', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));
    const subtasks = ['subtask-1', 'subtask-2', 'subtask-3', 'subtask-4', 'subtask-5'].map((id) => byId.get(id)!);
    const subtaskRows = new Set(subtasks.map((node) => rounded(centerY(node, hints[node.id].height))));
    const subtaskCols = new Set(subtasks.map((node) => rounded(centerX(node, hints[node.id].width))));

    expect(subtaskRows.size).toBeGreaterThan(1);
    expect(subtaskCols.size).toBeGreaterThan(1);
    expect(subtaskCols.size).toBeLessThanOrEqual(3);
  });

  it('reduces to one column for narrow inspector widths', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 360, rankSep: 48, nodeSep: 56 }, hints);
    const centers = laid.map((node) => rounded(centerX(node, hints[node.id].width)));

    expect(Math.max(...centers) - Math.min(...centers)).toBeLessThanOrEqual(1);
  });

  it('caps height-driven balancing to columns that fit the inspector width', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);
    const viewportWidth = 700;
    const viewportHeight = 600;
    const nodeSep = 56;
    const margin = 24;
    const maxNodeWidth = Math.max(...Object.values(hints).map((hint) => hint.width));
    const widthSafeColumns = Math.max(1, Math.floor((viewportWidth - margin * 2 + nodeSep) / (maxNodeWidth + nodeSep)));

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth, viewportHeight, rankSep: 48, nodeSep, maxColumns: 5 }, hints);
    const occupiedColumns = new Set(laid.map((node) => rounded(centerX(node, hints[node.id].width))));
    const contentRight = Math.max(...laid.map((node) => node.position.x + hints[node.id].width));

    expect(widthSafeColumns).toBe(2);
    expect(occupiedColumns.size).toBeLessThanOrEqual(widthSafeColumns);
    expect(contentRight + margin).toBeLessThanOrEqual(viewportWidth);
  });

  it('packs the assembly tail across columns after fan-in instead of one node per row', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);

    const laid = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));
    const tail = ['rai', 'review', 'merge', 'scribe'].map((id) => byId.get(id)!);
    const tailRows = new Set(tail.map((node) => rounded(centerY(node, hints[node.id].height))));

    expect(tailRows.size).toBeLessThan(tail.length);
    expect(rounded(centerY(byId.get('rai')!, hints.rai.height))).toBe(rounded(centerY(byId.get('review')!, hints.review.height)));
    expect(rounded(centerY(byId.get('merge')!, hints.merge.height))).toBe(rounded(centerY(byId.get('scribe')!, hints.scribe.height)));
  });

  it('keeps stable positions with no node overlap', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(5);

    const first = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);
    const second = layoutDagBalancedGrid(nodes, edges, { viewportWidth: 900, rankSep: 48, nodeSep: 56 }, hints);

    expect(second.map((node) => [node.id, node.position])).toEqual(first.map((node) => [node.id, node.position]));
    for (let i = 0; i < first.length; i += 1) {
      for (let j = i + 1; j < first.length; j += 1) {
        const a = first[i];
        const b = first[j];
        const ah = hints[a.id];
        const bh = hints[b.id];
        const separated =
          a.position.x + ah.width <= b.position.x ||
          b.position.x + bh.width <= a.position.x ||
          a.position.y + ah.height <= b.position.y ||
          b.position.y + bh.height <= a.position.y;
        expect(separated).toBe(true);
      }
    }
  });
});

describe('layoutDagStaircase', () => {
  const SEP = { rankSep: 64, nodeSep: 28 };

  const bbox = (laid: Node[], hints: Record<string, NodeSizeHint>) => {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const n of laid) {
      const h = hints[n.id];
      minX = Math.min(minX, n.position.x);
      minY = Math.min(minY, n.position.y);
      maxX = Math.max(maxX, n.position.x + h.width);
      maxY = Math.max(maxY, n.position.y + h.height);
    }
    return { width: maxX - minX, height: maxY - minY };
  };

  const SPINE = ['coordinator', 'outcome', 'work', 'rai', 'review', 'merge', 'scribe'];

  it('is deterministic — repeated layout of identical input yields identical coordinates (Tidy)', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const snap = (laid: Node[]) => laid.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y }));
    const a = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, targetAspect: 1.35, minStepRanks: 3 }, hints);
    const b = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, targetAspect: 1.35, minStepRanks: 3 }, hints);
    const c = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, targetAspect: 1.35, minStepRanks: 3 }, hints);
    expect(snap(b)).toEqual(snap(a));
    expect(snap(c)).toEqual(snap(a));
  });

  it('preserves the input (descriptor emission) order in the returned nodes, independent of layout', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    for (const rankdir of ['LR', 'TB'] as const) {
      const laid = layoutDagStaircase(nodes, edges, { rankdir, ...SEP, minStepRanks: 3 }, hints);
      // The run tree derives its order from this array; layout must never reorder it.
      expect(laid.map((n) => n.id)).toEqual(nodes.map((n) => n.id));
    }
  });

  it('yields a fixed dependency spine order for a fixed fixture', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, minStepRanks: 3 }, hints);
    // Sorting the spine nodes by primary axis (X in LR) must reproduce the real dependency order.
    const spineOrder = laid
      .filter((n) => SPINE.includes(n.id))
      .slice()
      .sort((a, b) => a.position.x - b.position.x)
      .map((n) => n.id);
    expect(spineOrder).toEqual(SPINE);
  });

  it('holds 2+ nodes per stair tread (chunky staircase, not a 1-node-per-step diagonal)', () => {
    // 3 subtasks ⇒ 8 ranks. With STAIR_RUN=2 the spine forms 2-node treads that share a row before
    // stepping down, so the number of distinct rows is well below the number of spine nodes.
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, minStepRanks: 3 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));

    const rows = SPINE.map((id) => rounded(byId.get(id)!.position.y));
    const distinctRows = new Set(rows).size;
    // A pure 1-per-step diagonal would put every spine node on its own row (distinctRows === length).
    expect(distinctRows).toBeLessThan(SPINE.length);
    expect(distinctRows).toBeGreaterThan(1); // still uses height (multiple treads)

    // The first tread groups the first two ranks on the same row (advancing right, no step yet).
    expect(rounded(byId.get('coordinator')!.position.y)).toBe(rounded(byId.get('outcome')!.position.y));
    expect(byId.get('outcome')!.position.x).toBeGreaterThan(byId.get('coordinator')!.position.x);
  });

  it('cascades a long linear spine as an alternating orthogonal stair, using both dimensions (LR)', () => {
    // 3 subtasks ⇒ 8 ranks (coordinator, outcome, work, [subtasks], rai, review, merge, scribe).
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, targetAspect: 1.35, minStepRanks: 3 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));

    const xs = SPINE.map((id) => byId.get(id)!.position.x);
    const ys = SPINE.map((id) => byId.get(id)!.position.y);
    // Monotonic non-decreasing on BOTH axes, and every step advances at least one axis (forward
    // progress, never reversing). The alternating stair steps right OR down each step — not both.
    for (let i = 1; i < SPINE.length; i += 1) {
      expect(xs[i]).toBeGreaterThanOrEqual(xs[i - 1]);
      expect(ys[i]).toBeGreaterThanOrEqual(ys[i - 1]);
      expect(xs[i] > xs[i - 1] || ys[i] > ys[i - 1]).toBe(true);
    }
    // Both dimensions are actually used (distinct columns AND distinct rows along the spine).
    expect(new Set(xs).size).toBeGreaterThan(1);
    expect(new Set(ys).size).toBeGreaterThan(1);

    // It must actually use height (many node rows tall, not a flat line).
    const { height } = bbox(laid, hints);
    expect(height).toBeGreaterThan(3 * 130);
  });

  it('keeps true parallel branches fanned out within their rank', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, minStepRanks: 3 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));
    const subs = ['subtask-1', 'subtask-2', 'subtask-3'].map((id) => byId.get(id)!);

    // Parallel subtasks share one column (primary X) but spread on the cross axis (distinct Y).
    const xs = new Set(subs.map((n) => rounded(n.position.x)));
    const ys = new Set(subs.map((n) => rounded(n.position.y)));
    expect(xs.size).toBe(1);
    expect(ys.size).toBe(3);
  });

  it('leaves a short chain as a straight line (no stepping)', () => {
    const nodes: Node[] = [makeNode('coordinator'), makeNode('outcome'), makeNode('work')];
    const edges: Edge[] = [makeEdge('coordinator', 'outcome'), makeEdge('outcome', 'work')];
    const hints = Object.fromEntries(nodes.map((n) => [n.id, { width: 250, height: 80 }]));

    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, minStepRanks: 3 }, hints);
    // All three single-node ranks share one row (no cascade for a short chain).
    const ys = new Set(laid.map((n) => rounded(n.position.y)));
    expect(ys.size).toBe(1);
  });

  it('transposes for the vertical (TB) orientation — an alternating stair down then right', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const laid = layoutDagStaircase(nodes, edges, { rankdir: 'TB', ...SEP, targetAspect: 1.35, minStepRanks: 3 }, hints);
    const byId = new Map(laid.map((n) => [n.id, n]));

    // In TB the primary axis is vertical (Y advances down) and the step spreads across X (right); the
    // alternation moves down OR right each step, so assert non-decreasing + forward progress on both.
    const xs = SPINE.map((id) => byId.get(id)!.position.x);
    const ys = SPINE.map((id) => byId.get(id)!.position.y);
    for (let i = 1; i < SPINE.length; i += 1) {
      expect(xs[i]).toBeGreaterThanOrEqual(xs[i - 1]);
      expect(ys[i]).toBeGreaterThanOrEqual(ys[i - 1]);
      expect(xs[i] > xs[i - 1] || ys[i] > ys[i - 1]).toBe(true);
    }
    expect(new Set(xs).size).toBeGreaterThan(1);
    expect(new Set(ys).size).toBeGreaterThan(1);
  });

  it('never produces negative coordinates', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(4);
    for (const rankdir of ['LR', 'TB'] as const) {
      const laid = layoutDagStaircase(nodes, edges, { rankdir, ...SEP, minStepRanks: 3 }, hints);
      for (const n of laid) {
        expect(n.position.x).toBeGreaterThanOrEqual(0);
        expect(n.position.y).toBeGreaterThanOrEqual(0);
      }
    }
  });

  it('never emits NaN/Infinity/negative coords even for poisoned options', () => {
    const { nodes, edges, hints } = coordinatorWithSubtasks(3);
    const poisons = [
      { targetAspect: 0 },
      { targetAspect: -3 },
      { targetAspect: Number.NaN },
      { targetAspect: Number.POSITIVE_INFINITY },
      { stepOffset: Number.NaN },
      { stepOffset: -500 },
      { stepOffset: Number.POSITIVE_INFINITY },
    ];
    for (const extra of poisons) {
      const laid = layoutDagStaircase(nodes, edges, { rankdir: 'LR', ...SEP, minStepRanks: 3, ...extra }, hints);
      for (const n of laid) {
        expect(Number.isFinite(n.position.x)).toBe(true);
        expect(Number.isFinite(n.position.y)).toBe(true);
        expect(n.position.x).toBeGreaterThanOrEqual(0);
        expect(n.position.y).toBeGreaterThanOrEqual(0);
      }
    }
  });
});
