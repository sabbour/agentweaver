import Dagre from 'dagre';
import type { Edge, Node } from '@xyflow/react';
export const NODE_W = 200;
export const NODE_H = 145;
export const DAG_NODE_SEP = 96;

// Compact "pill" node dimensions for the coordinator topology DAG. Node heights are DIFFERENTIATED
// so dagre packs a tight, data-driven graph without wasted space:
//   • SUBTASK (agent task) nodes are TALL — avatar + 2-line title + "Name (Role)" line + AI credits,
//     with the model-name caption rendered just BELOW the card. SUBTASK_CARD_H is the card box;
//     SUBTASK_NODE_H additionally reserves room for that caption (dagre hint).
//   • ALL other nodes (Coordinator, Outcome plan, Work plan, RAI, Review, Merge, Scribe) use the
//     SMALL card — icon + one-line title (+ short sub-label), no avatar/credits on the face
//     (FIXED_CARD_H / FIXED_NODE_H). ANY compact node that HAS a model additionally renders a
//     model-name caption BELOW the card, so its dagre hint reserves the extra caption room
//     (FIXED_NODE_WITH_CAPTION_H) — driven purely off the node's model, not its role.
//   • The Human Review gate GROWS to fit on-face action buttons while it is awaiting a decision;
//     REVIEW_EXPANDED_NODE_H is its dagre hint in that state so the staircase reserves the room.
// Node WIDTHS are differentiated too: SUBTASK nodes are WIDE (SUBTASK_NODE_W) to fit the 2-line
// title + "Name (Role)" + AI credits; the compact gate/system/coordinator nodes are NARROWER
// (FIXED_NODE_W) since they only carry an icon + title (+ model caption). Both widths are fed to
// dagre as per-node hints so columns stay cleanly aligned.
export const SUBTASK_NODE_W = 250;
export const FIXED_NODE_W = 184;
// Back-compat alias: existing imports refer to the wide (subtask) pill width.
export const COMPACT_NODE_W = SUBTASK_NODE_W;
export const SUBTASK_CARD_H = 88;
export const SUBTASK_NODE_H = 112;
export const FIXED_CARD_H = 48;
export const FIXED_NODE_H = 56;
// A compact node that also shows a model caption below the card (Coordinator / RAI): small card +
// the same caption reserve the subtask node uses (SUBTASK_NODE_H − SUBTASK_CARD_H = 24px).
export const FIXED_NODE_WITH_CAPTION_H = FIXED_CARD_H + (SUBTASK_NODE_H - SUBTASK_CARD_H);
export const REVIEW_EXPANDED_NODE_H = 96;
// Back-compat aliases: existing imports refer to the subtask (tall) pill dimensions.
export const COMPACT_CARD_H = SUBTASK_CARD_H;
export const COMPACT_NODE_H = SUBTASK_NODE_H;

// Workflow definitions need more visual mass than the compact runtime stages. The viewer and editor
// share this footprint so layout hints, edge routing, and the rendered cards stay aligned.
export const WORKFLOW_DEFINITION_NODE_W = 240;
export const WORKFLOW_PILL_NODE_W = WORKFLOW_DEFINITION_NODE_W;
export const WORKFLOW_PILL_NODE_H: Record<string, number> = {
  agent: 80,
  subtask: 80,
  gate: 76,
  action: 80,
  terminal: 68,
};
export const WORKFLOW_PILL_DEFAULT_NODE_H = 80;
export const WORKFLOW_EDITOR_ACTIONS_HEIGHT = 44;
export const WORKFLOW_FIT_VIEW_OPTIONS = { padding: 0.1, maxZoom: 1.8 } as const;
export const WORKFLOW_LONG_LINEAR_MIN_RANKS = 5;

// Back-compat export retained for consumers that used the former staircase
// tuning constant. Banded-lane wrapping now derives its width from aspect.
export const STAIR_RUN = 2;

// Per-node-type layout dimensions. Keep in sync with WorkflowGraphPanel card widths.
export const NODE_TYPE_W: Record<string, number> = {
  agent:    220,
  subtask:  220,
  gate:     180,
  action:   170,
  terminal: 150,
};
export const NODE_TYPE_H: Record<string, number> = {
  agent:    160,
  subtask:  180,
  gate:     130,
  action:   130,
  terminal: 110,
};

// Conservative rendered-card height hints for dagre. These include headers, metadata,
// timers/cost chips, and one or more action buttons so tall cards don't overlap.
export const RENDERED_NODE_TYPE_H: Record<string, number> = {
  agent:    240,
  subtask:  244,
  gate:     190,
  action:   210,
  terminal: 150,
};

export const RENDERED_DEFAULT_NODE_H = 220;
export const RENDERED_TOPOLOGY_NODE_H = 260;

export interface LayoutOpts {
  rankdir?: 'LR' | 'TB';
  rankSep?: number;
  nodeSep?: number;
  crossAlign?: 'start' | 'center';
}

export interface BalancedGridLayoutOpts extends LayoutOpts {
  viewportWidth?: number;
  viewportHeight?: number;
  minColumns?: number;
  maxColumns?: number;
}

export interface StaircaseLayoutOpts extends LayoutOpts {
  /** Desired width/height of the graph bounding box (matches the panel's aspect). */
  targetAspect?: number;
  /** Only cascade (step) when the rank count exceeds this; short chains stay a straight line. */
  minStepRanks?: number;
  /** Explicit cross-axis offset added per rank. When omitted it's derived from targetAspect. */
  stepOffset?: number;
}

export interface NodeSizeHint {
  width: number;
  height: number;
}

export interface ConnectorPoint {
  x: number;
  y: number;
}

/**
 * Computes the axis-aligned bounding box (width/height) that encloses a laid-out
 * node set, honoring each node's size hint (falling back to the default node box).
 * Used to compare LR vs TB staircase footprints when auto-picking the orientation
 * that best fills the topology panel.
 */
export function layoutBBox(
  nodes: { id: string; position: { x: number; y: number } }[],
  nodeSizeHints?: Record<string, NodeSizeHint>,
): { w: number; h: number } {
  if (nodes.length === 0) return { w: 0, h: 0 };
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const n of nodes) {
    const hint = nodeSizeHints?.[n.id];
    const w = hint?.width ?? NODE_W;
    const h = hint?.height ?? NODE_H;
    minX = Math.min(minX, n.position.x);
    minY = Math.min(minY, n.position.y);
    maxX = Math.max(maxX, n.position.x + w);
    maxY = Math.max(maxY, n.position.y + h);
  }
  return { w: Math.max(1, maxX - minX), h: Math.max(1, maxY - minY) };
}

export interface SteppedConnectorRoute {
  path: string;
  labelX: number;
  labelY: number;
  points: ConnectorPoint[];
}

function coord(value: number): string {
  return `${Math.round(value * 100) / 100}`;
}

function pointCommand(point: ConnectorPoint): string {
  return `${coord(point.x)},${coord(point.y)}`;
}

function dedupePoints(points: ConnectorPoint[]): ConnectorPoint[] {
  return points.filter((point, index) => {
    const prev = points[index - 1];
    return !prev || Math.abs(prev.x - point.x) > 0.01 || Math.abs(prev.y - point.y) > 0.01;
  });
}

export function roundedOrthogonalPath(points: ConnectorPoint[], radius = 8): string {
  const clean = dedupePoints(points);
  if (clean.length === 0) return '';
  if (clean.length === 1) return `M ${pointCommand(clean[0])}`;

  const commands = [`M ${pointCommand(clean[0])}`];
  for (let i = 1; i < clean.length - 1; i += 1) {
    const prev = clean[i - 1];
    const cur = clean[i];
    const next = clean[i + 1];
    const inDx = Math.sign(cur.x - prev.x);
    const inDy = Math.sign(cur.y - prev.y);
    const outDx = Math.sign(next.x - cur.x);
    const outDy = Math.sign(next.y - cur.y);
    const prevDist = Math.hypot(cur.x - prev.x, cur.y - prev.y);
    const nextDist = Math.hypot(next.x - cur.x, next.y - cur.y);
    const straight = (inDx === outDx && inDy === outDy) || (inDx === -outDx && inDy === -outDy);
    if (radius <= 0 || straight || prevDist < 2 || nextDist < 2) {
      commands.push(`L ${pointCommand(cur)}`);
      continue;
    }
    const r = Math.min(radius, prevDist / 2, nextDist / 2);
    const before = { x: cur.x - inDx * r, y: cur.y - inDy * r };
    const after = { x: cur.x + outDx * r, y: cur.y + outDy * r };
    commands.push(`L ${pointCommand(before)}`);
    commands.push(`Q ${pointCommand(cur)} ${pointCommand(after)}`);
  }
  commands.push(`L ${pointCommand(clean[clean.length - 1])}`);
  return commands.join(' ');
}

export function buildSteppedConnectorRoute(input: {
  sourceX: number;
  sourceY: number;
  targetX: number;
  targetY: number;
  orientation?: 'auto' | 'horizontal' | 'vertical';
  laneOffset?: number;
}): SteppedConnectorRoute {
  const { sourceX, sourceY, targetX, targetY, orientation = 'auto', laneOffset = 0 } = input;
  const vertical = orientation === 'vertical'
    || (orientation === 'auto' && Math.abs(targetY - sourceY) >= Math.abs(targetX - sourceX));
  const points = vertical
    ? [
        { x: sourceX, y: sourceY },
        { x: sourceX, y: (sourceY + targetY) / 2 + laneOffset },
        { x: targetX, y: (sourceY + targetY) / 2 + laneOffset },
        { x: targetX, y: targetY },
      ]
    : [
        { x: sourceX, y: sourceY },
        { x: (sourceX + targetX) / 2 + laneOffset, y: sourceY },
        { x: (sourceX + targetX) / 2 + laneOffset, y: targetY },
        { x: targetX, y: targetY },
      ];
  const clean = dedupePoints(points);
  return {
    path: roundedOrthogonalPath(clean),
    labelX: (sourceX + targetX) / 2 + (vertical ? 0 : laneOffset),
    labelY: (sourceY + targetY) / 2 + (vertical ? laneOffset : 0),
    points: clean,
  };
}

export function workflowNodeSizeHint(
  nodeType?: string | null,
  opts: { withEditorActions?: boolean } = {},
): NodeSizeHint {
  const key = nodeType ?? '';
  return {
    width: WORKFLOW_PILL_NODE_W,
    height: (WORKFLOW_PILL_NODE_H[key] ?? WORKFLOW_PILL_DEFAULT_NODE_H)
      + (opts.withEditorActions ? WORKFLOW_EDITOR_ACTIONS_HEIGHT : 0),
  };
}

export interface WorkflowLayoutAnalysis {
  rankCount: number;
  hasBranching: boolean;
  hasParallelRank: boolean;
  isLongLinear: boolean;
}

export type WorkflowDefinitionLayoutMode = 'columns' | 'staircase';

export interface WorkflowDefinitionLayoutResult {
  nodes: Node[];
  mode: WorkflowDefinitionLayoutMode;
  bbox: { w: number; h: number };
  analysis: WorkflowLayoutAnalysis;
}

interface BandedSection {
  nodeIds: string[];
  cols: number;
  rows: number;
}

interface BandedLayoutOptions {
  rankdir: 'LR' | 'TB';
  rankGap: number;
  nodeGap: number;
  snakeMinRanks: number;
  targetAspect: number;
}

const BANDED_MARGIN = 24;
const BANDED_LANE_STEP = 34;
const BANDED_LABEL_CHAR_W = 7;
const BANDED_SNAKE_MIN_RANKS = 3;

function edgeLabelSize(edge: Edge): NodeSizeHint {
  if (typeof edge.label !== 'string' && typeof edge.label !== 'number') {
    return { width: 0, height: 0 };
  }
  const text = String(edge.label).trim();
  if (!text) return { width: 0, height: 0 };
  return {
    width: text.length * BANDED_LABEL_CHAR_W + 20,
    height: 26,
  };
}

/**
 * Stable longest-path ranks. DFS back edges are ignored for ranking so loops
 * remain visible without pushing their target below its own descendants.
 */
function rankBandedNodes(nodes: Node[], edges: Edge[]): Map<string, number> {
  const index = new Map(nodes.map((node, i) => [node.id, i]));
  const ids = new Set(index.keys());
  const outgoing = new Map(nodes.map((node) => [node.id, [] as string[]]));
  for (const edge of edges) {
    if (!ids.has(edge.source) || !ids.has(edge.target) || edge.source === edge.target) continue;
    outgoing.get(edge.source)!.push(edge.target);
  }
  for (const targets of outgoing.values()) {
    targets.sort((a, b) => (index.get(a) ?? 0) - (index.get(b) ?? 0));
  }

  const backEdges = new Set<string>();
  const state = new Map<string, 0 | 1 | 2>();
  const visit = (id: string) => {
    state.set(id, 1);
    for (const target of outgoing.get(id) ?? []) {
      const targetState = state.get(target) ?? 0;
      if (targetState === 1) backEdges.add(`${id}\0${target}`);
      else if (targetState === 0) visit(target);
    }
    state.set(id, 2);
  };
  for (const node of nodes) {
    if ((state.get(node.id) ?? 0) === 0) visit(node.id);
  }

  const predecessors = new Map(nodes.map((node) => [node.id, [] as string[]]));
  for (const edge of edges) {
    if (!ids.has(edge.source) || !ids.has(edge.target)) continue;
    if (!backEdges.has(`${edge.source}\0${edge.target}`)) {
      predecessors.get(edge.target)!.push(edge.source);
    }
  }

  const ranks = new Map<string, number>();
  const rankOf = (id: string): number => {
    const known = ranks.get(id);
    if (known !== undefined) return known;
    ranks.set(id, 0);
    const parents = predecessors.get(id) ?? [];
    const rank = parents.length === 0 ? 0 : Math.max(...parents.map(rankOf)) + 1;
    ranks.set(id, rank);
    return rank;
  };
  for (const node of nodes) rankOf(node.id);
  return ranks;
}

function bandedLayers(nodes: Node[], edges: Edge[], ranks: Map<string, number>): string[][] {
  const index = new Map(nodes.map((node, i) => [node.id, i]));
  const ids = new Set(index.keys());
  const rankValues = [...new Set(nodes.map((node) => ranks.get(node.id) ?? 0))].sort((a, b) => a - b);
  const layerIndex = new Map(rankValues.map((rank, i) => [rank, i]));
  const layers = rankValues.map(() => [] as string[]);
  for (const node of nodes) layers[layerIndex.get(ranks.get(node.id) ?? 0)!].push(node.id);

  const predecessors = new Map(nodes.map((node) => [node.id, [] as string[]]));
  for (const edge of edges) {
    if (!ids.has(edge.source) || !ids.has(edge.target)) continue;
    if ((ranks.get(edge.source) ?? 0) < (ranks.get(edge.target) ?? 0)) {
      predecessors.get(edge.target)!.push(edge.source);
    }
  }

  const slots = new Map<string, number>();
  layers[0]?.forEach((id, i) => slots.set(id, i));
  for (let layer = 1; layer < layers.length; layer += 1) {
    layers[layer] = layers[layer]
      .map((id, original) => {
        const parents = (predecessors.get(id) ?? []).filter((parent) => slots.has(parent));
        const barycenter = parents.length === 0
          ? original
          : parents.reduce((sum, parent) => sum + slots.get(parent)!, 0) / parents.length;
        return { id, original, barycenter };
      })
      .sort((a, b) =>
        a.barycenter - b.barycenter
        || a.original - b.original
        || (index.get(a.id) ?? 0) - (index.get(b.id) ?? 0))
      .map(({ id }) => id);
    layers[layer].forEach((id, i) => slots.set(id, i));
  }
  return layers;
}

function snakeColumns(
  count: number,
  sizes: Map<string, NodeSizeHint>,
  ids: string[],
  nodeGap: number,
  targetAspect: number,
  rankdir: 'LR' | 'TB',
): number {
  let best = 1;
  let bestScore = Infinity;
  const typicalWidth = Math.max(1, median(ids.map((id) => sizes.get(id)!.width)));
  const typicalHeight = Math.max(1, median(ids.map((id) => sizes.get(id)!.height)));
  const maxPrimarySlots = Math.min(count, 6, Math.max(1, count - 1));
  for (let cols = 1; cols <= maxPrimarySlots; cols += 1) {
    const rows = Math.ceil(count / cols);
    const width = (rankdir === 'LR' ? cols : rows) * typicalWidth
      + Math.max(0, (rankdir === 'LR' ? cols : rows) - 1) * nodeGap;
    const height = (rankdir === 'LR' ? rows : cols) * typicalHeight
      + Math.max(0, (rankdir === 'LR' ? rows : cols) - 1) * nodeGap;
    const desiredAspect = rankdir === 'LR'
      ? targetAspect
      : typicalHeight / typicalWidth / targetAspect;
    const score = Math.abs(Math.log(width / height / desiredAspect));
    if (score < bestScore) {
      bestScore = score;
      best = cols;
    }
  }
  return best;
}

function serpentineIds(ids: string[], cols: number): string[] {
  const ordered: string[] = [];
  for (let row = 0; row * cols < ids.length; row += 1) {
    const slice = ids.slice(row * cols, (row + 1) * cols);
    ordered.push(...(row % 2 === 1 ? slice.reverse() : slice));
  }
  return ordered;
}

function layoutBandedLane(
  nodes: Node[],
  edges: Edge[],
  options: BandedLayoutOptions,
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  if (nodes.length === 0) return nodes;

  const sizes = new Map<string, NodeSizeHint>();
  for (const node of nodes) {
    const hint = nodeSizeHints?.[node.id];
    sizes.set(node.id, {
      width: hint?.width ?? NODE_W,
      height: hint?.height ?? NODE_H,
    });
  }

  const ranks = rankBandedNodes(nodes, edges);
  const layers = bandedLayers(nodes, edges, ranks);
  const linked = new Set(edges.map((edge) => `${edge.source}\0${edge.target}`));
  const aspect = Number.isFinite(options.targetAspect) && options.targetAspect > 0
    ? options.targetAspect
    : 1.4;
  const snakeMin = Math.max(2, Math.floor(options.snakeMinRanks));
  const sections: BandedSection[] = [];

  let layer = 0;
  while (layer < layers.length) {
    let end = layer;
    while (
      layers[end]?.length === 1
      && layers[end + 1]?.length === 1
      && linked.has(`${layers[end][0]}\0${layers[end + 1][0]}`)
    ) {
      end += 1;
    }
    const runLength = end - layer + 1;
    if (runLength >= snakeMin) {
      const ids = layers.slice(layer, end + 1).map((rank) => rank[0]);
      const cols = snakeColumns(ids.length, sizes, ids, options.nodeGap, aspect, options.rankdir);
      sections.push({
        nodeIds: serpentineIds(ids, cols),
        cols,
        rows: Math.ceil(ids.length / cols),
      });
      layer = end + 1;
      continue;
    }

    sections.push({
      nodeIds: layers[layer],
      cols: 1,
      rows: layers[layer].length,
    });
    layer += 1;
  }

  const sectionByNode = new Map<string, number>();
  sections.forEach((section, sectionIndex) => {
    section.nodeIds.forEach((id) => sectionByNode.set(id, sectionIndex));
  });

  const boundaryEdges = Array.from({ length: Math.max(0, sections.length - 1) }, () => [] as Edge[]);
  for (const edge of edges) {
    const sourceSection = sectionByNode.get(edge.source);
    const targetSection = sectionByNode.get(edge.target);
    if (sourceSection === undefined || targetSection === undefined || sourceSection === targetSection) continue;
    const lo = Math.min(sourceSection, targetSection);
    const hi = Math.max(sourceSection, targetSection);
    for (let boundary = lo; boundary < hi; boundary += 1) boundaryEdges[boundary].push(edge);
  }
  const boundaryGap = boundaryEdges.map((crossing) => {
    const labelExtent = crossing.reduce((max, edge) => {
      const label = edgeLabelSize(edge);
      return Math.max(max, options.rankdir === 'LR' ? label.width : label.height);
    }, 0);
    return Math.max(
      options.rankGap,
      options.rankGap + Math.max(0, crossing.length - 1) * BANDED_LANE_STEP,
      labelExtent + 24 + Math.max(0, crossing.length - 1) * BANDED_LANE_STEP,
    );
  });

  const sectionGeometry = sections.map((section, sectionIndex) => {
    const internalLabels = edges
      .filter((edge) =>
        sectionByNode.get(edge.source) === sectionByNode.get(edge.target)
        && sectionByNode.get(edge.source) === sectionIndex)
      .map(edgeLabelSize);
    const primaryGap = Math.max(
      options.nodeGap,
      ...internalLabels.map((label) =>
        (options.rankdir === 'LR' ? label.width : label.height) + 24),
    );
    const crossGap = Math.max(
      options.nodeGap,
      ...internalLabels.map((label) =>
        (options.rankdir === 'LR' ? label.height : label.width) + 24),
    );
    const primaryExtents = Array.from({ length: section.cols }, () => 0);
    const crossExtents = Array.from({ length: section.rows }, () => 0);
    section.nodeIds.forEach((id, i) => {
      const crossSlot = Math.floor(i / section.cols);
      const rowLength = Math.min(section.cols, section.nodeIds.length - crossSlot * section.cols);
      const primarySlot = i % section.cols
        + (crossSlot % 2 === 1 ? section.cols - rowLength : 0);
      const size = sizes.get(id)!;
      const primarySize = options.rankdir === 'LR' ? size.width : size.height;
      const crossSize = options.rankdir === 'LR' ? size.height : size.width;
      primaryExtents[primarySlot] = Math.max(primaryExtents[primarySlot], primarySize);
      crossExtents[crossSlot] = Math.max(crossExtents[crossSlot], crossSize);
    });
    const primaryStarts: number[] = [];
    const crossStarts: number[] = [];
    primaryExtents.reduce((cursor, extent, i) => {
      primaryStarts[i] = cursor;
      return cursor + extent + primaryGap;
    }, 0);
    crossExtents.reduce((cursor, extent, i) => {
      crossStarts[i] = cursor;
      return cursor + extent + crossGap;
    }, 0);
    const primaryExtent = primaryExtents.reduce((sum, extent) => sum + extent, 0)
      + Math.max(0, section.cols - 1) * primaryGap;
    const crossExtent = crossExtents.reduce((sum, extent) => sum + extent, 0)
      + Math.max(0, section.rows - 1) * crossGap;
    return { primaryExtents, crossExtents, primaryStarts, crossStarts, primaryExtent, crossExtent };
  });
  const maxCrossExtent = Math.max(...sectionGeometry.map((geometry) => geometry.crossExtent));

  const positions = new Map<string, { x: number; y: number }>();
  let primaryCursor = BANDED_MARGIN;
  for (let sectionIndex = 0; sectionIndex < sections.length; sectionIndex += 1) {
    const section = sections[sectionIndex];
    const geometry = sectionGeometry[sectionIndex];
    const crossOrigin = BANDED_MARGIN + (maxCrossExtent - geometry.crossExtent) / 2;
    section.nodeIds.forEach((id, i) => {
      const crossSlot = Math.floor(i / section.cols);
      const rowLength = Math.min(section.cols, section.nodeIds.length - crossSlot * section.cols);
      const primarySlot = i % section.cols
        + (crossSlot % 2 === 1 ? section.cols - rowLength : 0);
      const size = sizes.get(id)!;
      const primarySize = options.rankdir === 'LR' ? size.width : size.height;
      const crossSize = options.rankdir === 'LR' ? size.height : size.width;
      const primary = primaryCursor
        + geometry.primaryStarts[primarySlot]
        + (geometry.primaryExtents[primarySlot] - primarySize) / 2;
      const cross = crossOrigin
        + geometry.crossStarts[crossSlot]
        + (geometry.crossExtents[crossSlot] - crossSize) / 2;
      positions.set(id, options.rankdir === 'LR'
        ? { x: primary, y: cross }
        : { x: cross, y: primary });
    });

    primaryCursor += geometry.primaryExtent + (boundaryGap[sectionIndex] ?? 0);
  }

  return nodes.map((node) => {
    const size = sizes.get(node.id)!;
    return {
      ...node,
      position: positions.get(node.id) ?? node.position,
      initialWidth: size.width,
      initialHeight: size.height,
    };
  });
}

export function analyzeWorkflowLayout(nodes: Node[], edges: Edge[]): WorkflowLayoutAnalysis {
  if (nodes.length === 0) {
    return { rankCount: 0, hasBranching: false, hasParallelRank: false, isLongLinear: false };
  }

  const nodeIds = new Set(nodes.map((node) => node.id));
  const originalIndex = new Map(nodes.map((node, index) => [node.id, index]));
  const outgoing = new Map(nodes.map((node) => [node.id, [] as string[]]));
  const incoming = new Map(nodes.map((node) => [node.id, [] as string[]]));
  const indegree = new Map(nodes.map((node) => [node.id, 0]));

  for (const edge of edges) {
    if (!nodeIds.has(edge.source) || !nodeIds.has(edge.target)) continue;
    outgoing.get(edge.source)!.push(edge.target);
    incoming.get(edge.target)!.push(edge.source);
    indegree.set(edge.target, (indegree.get(edge.target) ?? 0) + 1);
  }
  for (const targets of outgoing.values()) {
    targets.sort((a, b) => (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0));
  }

  const queue = nodes
    .filter((node) => (indegree.get(node.id) ?? 0) === 0)
    .map((node) => node.id);
  const depth = new Map(queue.map((id) => [id, 0]));
  const visited = new Set<string>();

  while (queue.length > 0) {
    const id = queue.shift()!;
    visited.add(id);
    const baseDepth = depth.get(id) ?? 0;
    for (const target of outgoing.get(id) ?? []) {
      depth.set(target, Math.max(depth.get(target) ?? 0, baseDepth + 1));
      indegree.set(target, (indegree.get(target) ?? 0) - 1);
      if ((indegree.get(target) ?? 0) === 0) {
        queue.push(target);
        queue.sort((a, b) => (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0));
      }
    }
  }

  // Keep malformed/cyclic definitions deterministic and visible; validation handles the error itself.
  for (const node of nodes) {
    if (visited.has(node.id)) continue;
    const parentDepth = (incoming.get(node.id) ?? []).reduce(
      (max, parent) => Math.max(max, depth.get(parent) ?? 0),
      0,
    );
    depth.set(node.id, parentDepth + 1);
  }

  const rankCounts = new Map<number, number>();
  for (const node of nodes) {
    const rank = depth.get(node.id) ?? 0;
    rankCounts.set(rank, (rankCounts.get(rank) ?? 0) + 1);
  }

  const rankCount = Math.max(0, ...depth.values()) + 1;
  const hasParallelRank = [...rankCounts.values()].some((count) => count > 1);
  const hasBranching = hasParallelRank
    || [...outgoing.values()].some((targets) => targets.length > 1)
    || [...incoming.values()].some((sources) => sources.length > 1);
  const isLongLinear = !hasBranching
    && rankCount >= WORKFLOW_LONG_LINEAR_MIN_RANKS
    && rankCount === nodes.length;

  return { rankCount, hasBranching, hasParallelRank, isLongLinear };
}

export function layoutWorkflowDefinitionNodes(
  nodes: Node[],
  edges: Edge[],
  nodeSizeHints?: Record<string, NodeSizeHint>,
): WorkflowDefinitionLayoutResult {
  const analysis = analyzeWorkflowLayout(nodes, edges);
  const mode: WorkflowDefinitionLayoutMode = analysis.isLongLinear ? 'staircase' : 'columns';
  const laidOut = layoutBandedLane(
    nodes,
    edges,
    {
      rankdir: 'LR',
      rankGap: mode === 'staircase' ? 64 : 72,
      nodeGap: mode === 'staircase' ? 40 : 48,
      snakeMinRanks: BANDED_SNAKE_MIN_RANKS,
      targetAspect: 1.35,
    },
    nodeSizeHints,
  );

  return {
    nodes: laidOut,
    mode,
    bbox: layoutBBox(laidOut, nodeSizeHints),
    analysis,
  };
}

export function workflowDefinitionViewportHeight(bbox: { w: number; h: number }): number {
  if (bbox.w === 0 || bbox.h === 0) return 320;
  return Math.min(520, Math.max(300, Math.ceil(bbox.h + 80)));
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

function median(values: number[]): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

/**
 * Balanced row/column layout for the Coordinator topology inspector.
 *
 * Unlike `layoutDagColumns`, this is intentionally not a strict LR or TB layout:
 * it computes DAG depths for stable ordering, then packs ranks into an adaptive
 * row-major grid. Fan-out ranks occupy clean left-to-right rows, while the
 * single-node assembly tail after fan-in continues across columns instead of
 * becoming a tall one-node-per-row stack.
 */
export function layoutDagBalancedGrid(
  nodes: Node[],
  edges: Edge[],
  opts: BalancedGridLayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  if (nodes.length === 0) return nodes;

  const nodeIds = new Set(nodes.map((n) => n.id));
  const originalIndex = new Map(nodes.map((n, index) => [n.id, index]));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  const indegree = new Map<string, number>();
  for (const n of nodes) {
    outgoing.set(n.id, []);
    incoming.set(n.id, []);
    indegree.set(n.id, 0);
  }

  for (const e of edges) {
    if (!nodeIds.has(e.source) || !nodeIds.has(e.target)) continue;
    outgoing.get(e.source)!.push(e.target);
    incoming.get(e.target)!.push(e.source);
    indegree.set(e.target, (indegree.get(e.target) ?? 0) + 1);
  }
  for (const list of outgoing.values()) {
    list.sort((a, b) => (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0));
  }

  const queue = nodes
    .filter((n) => (indegree.get(n.id) ?? 0) === 0)
    .map((n) => n.id)
    .sort((a, b) => (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0));
  const topo: string[] = [];
  const depth = new Map<string, number>();
  for (const id of queue) depth.set(id, 0);

  while (queue.length > 0) {
    const id = queue.shift()!;
    topo.push(id);
    const baseDepth = depth.get(id) ?? 0;
    for (const target of outgoing.get(id) ?? []) {
      depth.set(target, Math.max(depth.get(target) ?? 0, baseDepth + 1));
      indegree.set(target, (indegree.get(target) ?? 0) - 1);
      if ((indegree.get(target) ?? 0) === 0) {
        queue.push(target);
        queue.sort((a, b) => (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0));
      }
    }
  }

  // Defensive cycle/partial-descriptor fallback: keep every node visible and stable.
  for (const n of nodes) {
    if (topo.includes(n.id)) continue;
    const parentDepth = (incoming.get(n.id) ?? []).reduce(
      (max, parent) => Math.max(max, depth.get(parent) ?? 0),
      0,
    );
    depth.set(n.id, parentDepth + 1);
    topo.push(n.id);
  }

  const topoIndex = new Map(topo.map((id, index) => [id, index]));
  const sizes = new Map<string, NodeSizeHint>();
  for (const n of nodes) {
    const hint = nodeSizeHints?.[n.id];
    sizes.set(n.id, { width: hint?.width ?? NODE_W, height: hint?.height ?? NODE_H });
  }

  const typicalWidth = Math.max(
    1,
    median(nodes.map((n) => sizes.get(n.id)!.width)),
  );
  const typicalHeight = Math.max(
    1,
    median(nodes.map((n) => sizes.get(n.id)!.height)),
  );
  const maxNodeWidth = Math.max(
    1,
    ...nodes.map((n) => sizes.get(n.id)!.width),
  );
  const colGap = opts.nodeSep ?? 56;
  const rowGap = opts.rankSep ?? 56;
  const margin = 24;
  const minColumns = opts.minColumns ?? 1;
  const maxColumns = opts.maxColumns ?? 5;
  const usableWidth = Math.max(0, (opts.viewportWidth ?? 0) - margin * 2);
  const widthSafeColumns = opts.viewportWidth
    ? Math.max(1, Math.floor((usableWidth + colGap) / (maxNodeWidth + colGap)))
    : maxColumns;
  const maxAllowedColumns = Math.max(1, Math.min(maxColumns, widthSafeColumns));
  let columns = opts.viewportWidth
    ? Math.floor((usableWidth + colGap) / (typicalWidth + colGap))
    : 3;
  columns = clamp(columns || 1, Math.min(minColumns, maxAllowedColumns), maxAllowedColumns);

  if (opts.viewportHeight && nodes.length > columns) {
    const estimatedRows = () => Math.ceil(nodes.length / columns);
    while (
      columns < maxAllowedColumns &&
      estimatedRows() * typicalHeight + Math.max(0, estimatedRows() - 1) * rowGap + margin * 2 > opts.viewportHeight
    ) {
      columns += 1;
    }
  }

  const ranks = new Map<number, string[]>();
  for (const id of topo) {
    const d = depth.get(id) ?? 0;
    if (!ranks.has(d)) ranks.set(d, []);
    ranks.get(d)!.push(id);
  }

  const cellById = new Map<string, { row: number; col: number }>();
  const occupied = new Set<string>();
  let cursorRow = 0;
  let cursorCol = 0;

  const keyFor = (row: number, col: number) => `${row}:${col}`;
  const bumpCursor = () => {
    cursorCol += 1;
    if (cursorCol >= columns) {
      cursorCol = 0;
      cursorRow += 1;
    }
    while (occupied.has(keyFor(cursorRow, cursorCol))) {
      cursorCol += 1;
      if (cursorCol >= columns) {
        cursorCol = 0;
        cursorRow += 1;
      }
    }
  };
  const place = (id: string, row: number, col: number) => {
    let r = row;
    let c = clamp(col, 0, columns - 1);
    while (occupied.has(keyFor(r, c))) {
      c += 1;
      if (c >= columns) {
        c = 0;
        r += 1;
      }
    }
    occupied.add(keyFor(r, c));
    cellById.set(id, { row: r, col: c });
    if (r > cursorRow || (r === cursorRow && c >= cursorCol)) {
      cursorRow = r;
      cursorCol = c;
      bumpCursor();
    }
  };
  const placeNext = (id: string) => place(id, cursorRow, cursorCol);

  const sortedRankKeys = [...ranks.keys()].sort((a, b) => a - b);
  for (const rank of sortedRankKeys) {
    const ids = ranks.get(rank)!.sort((a, b) => {
      const ai = topoIndex.get(a) ?? 0;
      const bi = topoIndex.get(b) ?? 0;
      return ai - bi || (originalIndex.get(a) ?? 0) - (originalIndex.get(b) ?? 0);
    });

    if (ids.length > 1 && cursorCol !== 0) {
      cursorRow += 1;
      cursorCol = 0;
    }

    for (const id of ids) {
      const parentCells = (incoming.get(id) ?? [])
        .map((parent) => cellById.get(parent))
        .filter((cell): cell is { row: number; col: number } => Boolean(cell));
      if (ids.length === 1 && parentCells.length > 1) {
        const parentCols = parentCells.map((cell) => cell.col);
        const maxParentRow = parentCells.reduce((max, cell) => Math.max(max, cell.row), 0);
        let row = Math.max(cursorRow, maxParentRow + 1);
        const col = clamp(Math.round(median(parentCols)), 0, columns - 1);
        if (row === cursorRow && col < cursorCol) row += 1;
        place(id, row, col);
      } else {
        placeNext(id);
      }
    }
  }

  const colWidths = Array.from({ length: columns }, () => 0);
  let rowCount = 0;
  for (const [id, cell] of cellById) {
    const size = sizes.get(id)!;
    colWidths[cell.col] = Math.max(colWidths[cell.col], size.width);
    rowCount = Math.max(rowCount, cell.row + 1);
  }
  const rowHeights = Array.from({ length: rowCount }, () => 0);
  for (const [id, cell] of cellById) {
    const size = sizes.get(id)!;
    rowHeights[cell.row] = Math.max(rowHeights[cell.row], size.height);
  }

  const colX: number[] = [];
  let x = margin;
  for (let col = 0; col < columns; col += 1) {
    colX[col] = x;
    x += colWidths[col] + colGap;
  }
  const rowY: number[] = [];
  let y = margin;
  for (let row = 0; row < rowCount; row += 1) {
    rowY[row] = y;
    y += rowHeights[row] + rowGap;
  }

  return nodes.map((n) => {
    const size = sizes.get(n.id)!;
    const cell = cellById.get(n.id);
    if (!cell) return n;
    return {
      ...n,
      position: {
        x: Math.round(colX[cell.col] + (colWidths[cell.col] - size.width) / 2),
        y: Math.round(rowY[cell.row] + (rowHeights[cell.row] - size.height) / 2),
      },
      initialWidth: size.width,
      initialHeight: size.height,
    };
  });
}

/**
 * Rank-aligned DAG layout. Runs dagre to determine rank (depth) assignments,
 * then snaps every node in the same rank to an exact virtual lane so cards line
 * up in clean rows/columns. Supports both LR (vertical columns, left→right) and
 * TB (horizontal rows, top→bottom) rank directions.
 *
 * Use this for the coordinator run page where the aligned "grid" look is
 * required. Other surfaces can keep using layoutDag directly.
 */
export function layoutDagColumns(
  nodes: Node[],
  edges: Edge[],
  opts: LayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  if (nodes.length === 0) return nodes;

  const rankdir = opts.rankdir ?? 'LR';
  const g = new Dagre.graphlib.Graph();
  g.setGraph({
    rankdir,
    ranksep: opts.rankSep ?? 80,
    nodesep: opts.nodeSep ?? 40,
    marginx: 24,
    marginy: 24,
  });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    const hint = nodeSizeHints?.[n.id];
    g.setNode(n.id, { width: hint?.width ?? NODE_W, height: hint?.height ?? NODE_H });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }

  Dagre.layout(g);

  const isVertical = rankdir === 'TB';

  // Group nodes by dagre-assigned rank. In LR mode all nodes at the same depth
  // share the same X value; in TB mode they share the same Y value. Round to
  // absorb floating-point noise.
  const byRank = new Map<number, string[]>();
  for (const n of nodes) {
    const node = g.node(n.id);
    const key = Math.round(isVertical ? node.y : node.x);
    if (!byRank.has(key)) byRank.set(key, []);
    byRank.get(key)!.push(n.id);
  }

  const LANE_GAP = opts.rankSep ?? 72; // gap between successive ranks (columns for LR, rows for TB)
  const CROSS_GAP = opts.nodeSep ?? 40; // gap between stacked cards within a rank
  const MARGIN = 24;

  const posMap = new Map<string, { x: number; y: number }>();
  const sortedRankKeys = [...byRank.keys()].sort((a, b) => a - b);

  if (isVertical) {
    // Compute each rank's total row width so we can centre every row on a
    // shared vertical axis. A single-node spine rank then lands centred over a
    // multi-node fan-out row, and fan-out rows spread symmetrically.
    const rowWidthOf = (nodeIds: string[]): number =>
      nodeIds.reduce((sum, id) => sum + (nodeSizeHints?.[id]?.width ?? NODE_W), 0) +
      Math.max(0, nodeIds.length - 1) * CROSS_GAP;

    const maxRowWidth = sortedRankKeys.reduce(
      (max, key) => Math.max(max, rowWidthOf(byRank.get(key)!)),
      0,
    );
    const centerX = MARGIN + maxRowWidth / 2;

    let laneStart = MARGIN;
    for (const rankKey of sortedRankKeys) {
      const nodeIds = byRank.get(rankKey)!;
      nodeIds.sort((a, b) => g.node(a).x - g.node(b).x);

      const rowH = nodeIds.reduce((max, id) => Math.max(max, nodeSizeHints?.[id]?.height ?? NODE_H), 0);
      let crossX = Math.round(centerX - rowWidthOf(nodeIds) / 2);
      for (const id of nodeIds) {
        const hint = nodeSizeHints?.[id];
        const h = hint?.height ?? NODE_H;
        const w = hint?.width ?? NODE_W;
        posMap.set(id, { x: crossX, y: laneStart + (rowH - h) / 2 });
        crossX += w + CROSS_GAP;
      }
      laneStart += rowH + LANE_GAP;
    }
  } else {
    const colHeightOf = (nodeIds: string[]): number =>
      nodeIds.reduce((sum, id) => sum + (nodeSizeHints?.[id]?.height ?? NODE_H), 0) +
      Math.max(0, nodeIds.length - 1) * CROSS_GAP;
    const maxColHeight = sortedRankKeys.reduce(
      (max, key) => Math.max(max, colHeightOf(byRank.get(key)!)),
      0,
    );

    let laneStart = MARGIN;
    for (const rankKey of sortedRankKeys) {
      const nodeIds = byRank.get(rankKey)!;
      // Preserve dagre's cross-axis ordering within the rank.
      nodeIds.sort((a, b) => g.node(a).y - g.node(b).y);

      // Rank lanes run left→right; cards stack top→bottom within each column.
      const colW = nodeIds.reduce((max, id) => Math.max(max, nodeSizeHints?.[id]?.width ?? NODE_W), 0);
      let crossY = opts.crossAlign === 'center'
        ? MARGIN + (maxColHeight - colHeightOf(nodeIds)) / 2
        : MARGIN;
      for (const id of nodeIds) {
        const hint = nodeSizeHints?.[id];
        const h = hint?.height ?? NODE_H;
        const w = hint?.width ?? NODE_W;
        // Centre narrower cards within the column width.
        posMap.set(id, { x: laneStart + (colW - w) / 2, y: crossY });
        crossY += h + CROSS_GAP;
      }
      laneStart += colW + LANE_GAP;
    }
  }

  // Guard against any negative coordinates by shifting everything so the graph
  // starts at MARGIN on the cross axis.
  if (isVertical && posMap.size > 0) {
    let minX = Infinity;
    for (const pos of posMap.values()) minX = Math.min(minX, pos.x);
    if (minX < MARGIN) {
      const shift = MARGIN - minX;
      for (const pos of posMap.values()) pos.x += shift;
    }
  }

  return nodes.map((n) => {
    const hint = nodeSizeHints?.[n.id];
    return {
      ...n,
      position: posMap.get(n.id) ?? n.position,
      initialWidth: hint?.width ?? NODE_W,
      initialHeight: hint?.height ?? NODE_H,
    };
  });
}

/**
 * Deterministic banded-lane layout for runtime and landing DAGs.
 *
 * Long one-node rank runs fold into a serpentine grid; branching ranks remain
 * fixed bands ordered by stable longest-path rank and predecessor barycenter.
 * Inter-band spacing reserves orthogonal routing gutters, including additional
 * lanes for parallel crossings and room for rendered edge labels.
 */
export function layoutDagStaircase(
  nodes: Node[],
  edges: Edge[],
  opts: StaircaseLayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  const rawStep = opts.stepOffset;
  const extraGap = rawStep != null && Number.isFinite(rawStep) ? Math.max(0, rawStep) : 0;
  return layoutBandedLane(
    nodes,
    edges,
    {
      rankdir: opts.rankdir ?? 'LR',
      rankGap: Math.max(opts.rankSep ?? 72, extraGap),
      nodeGap: opts.nodeSep ?? 40,
      snakeMinRanks: opts.minStepRanks ?? BANDED_SNAKE_MIN_RANKS,
      targetAspect: opts.targetAspect ?? 1.4,
    },
    nodeSizeHints,
  );
}

/**
 * Runs dagre auto-layout on the given nodes and edges.
 * Returns a new nodes array with computed positions.
 * Pass only forward (non-loopback) edges so dagre doesn't try to route cycles.
 * Optionally provide per-node size overrides via `nodeSizeHints`.
 */
export function layoutDag(
  nodes: Node[],
  edges: Edge[],
  opts: LayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  const g = new Dagre.graphlib.Graph();
  g.setGraph({
    rankdir: opts.rankdir ?? 'LR',
    ranksep: opts.rankSep ?? 80,
    nodesep: opts.nodeSep ?? 40,
    marginx: 24,
    marginy: 24,
  });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    const hint = nodeSizeHints?.[n.id];
    g.setNode(n.id, { width: hint?.width ?? NODE_W, height: hint?.height ?? NODE_H });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }

  Dagre.layout(g);

  return nodes.map((n) => {
    const pos = g.node(n.id);
    const hint = nodeSizeHints?.[n.id];
    const w = hint?.width ?? NODE_W;
    const h = hint?.height ?? NODE_H;
    return { ...n, position: { x: pos.x - w / 2, y: pos.y - h / 2 }, initialWidth: w, initialHeight: h };
  });
}

/**
 * Rendered footprint of a laid-out grid node: measured size wins, then the layout
 * helper's `initialWidth`/`initialHeight` hint, then the compact-pill default.
 */
export function graphNodeSize(node: Node): { width: number; height: number } {
  return {
    width: node.measured?.width ?? node.initialWidth ?? COMPACT_NODE_W,
    height: node.measured?.height ?? node.initialHeight ?? COMPACT_NODE_H,
  };
}

/**
 * Stepped-edge routing for a grid/staircase layout.
 *
 * Chooses the source/target handle (of the eight GRID handles rendered by
 * WorkflowNode) and a `flowDirection` for every `spine` / `loopback` edge so the
 * connector leaves and enters on the correct side and bows AROUND any node that
 * sits in its straight corridor. Non-spine/loopback edges pass through untouched.
 * Shared by the Coordinator run graph and the landing scenario demo so both use
 * the exact same production routing (never a reimplementation).
 */
export function routeGridEdges(edges: Edge[], nodes: Node[]): Edge[] {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const center = (node: Node) => {
    const size = graphNodeSize(node);
    return {
      x: node.position.x + size.width / 2,
      y: node.position.y + size.height / 2,
    };
  };
  const laneOffsets = new Map<string, number>();
  const gutterGroups = new Map<string, Array<{ edge: Edge; cross: number }>>();
  const loopbackSides = new Map<string, 'left' | 'right' | 'top' | 'bottom'>();
  const loopbackGroups = new Map<string, Array<{ edge: Edge; span: number }>>();
  for (const edge of edges) {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) continue;
    const sourceCenter = center(source);
    const targetCenter = center(target);
    if (edge.type === 'loopback') {
      const horizontal = Math.abs(targetCenter.x - sourceCenter.x)
        >= Math.abs(targetCenter.y - sourceCenter.y);
      const peerCenters = nodes
        .filter((peer) => peer.id !== edge.source && peer.id !== edge.target)
        .map((peer) => center(peer));
      let side: 'left' | 'right' | 'top' | 'bottom';
      if (horizontal) {
        const above = peerCenters.filter((peer) =>
          peer.y < Math.min(sourceCenter.y, targetCenter.y)).length;
        const below = peerCenters.filter((peer) =>
          peer.y > Math.max(sourceCenter.y, targetCenter.y)).length;
        side = above <= below ? 'top' : 'bottom';
      } else {
        const left = peerCenters.filter((peer) =>
          peer.x < Math.min(sourceCenter.x, targetCenter.x)).length;
        const right = peerCenters.filter((peer) =>
          peer.x > Math.max(sourceCenter.x, targetCenter.x)).length;
        side = left <= right ? 'left' : 'right';
      }
      loopbackSides.set(edge.id, side);
      const key = `loopback:${side}`;
      if (!loopbackGroups.has(key)) loopbackGroups.set(key, []);
      loopbackGroups.get(key)!.push({
        edge,
        span: horizontal
          ? Math.abs(targetCenter.x - sourceCenter.x)
          : Math.abs(targetCenter.y - sourceCenter.y),
      });
      continue;
    }
    if (edge.type !== 'spine') continue;
    const horizontal = Math.abs(targetCenter.x - sourceCenter.x)
      >= Math.abs(targetCenter.y - sourceCenter.y);
    const midpoint = horizontal
      ? (sourceCenter.x + targetCenter.x) / 2
      : (sourceCenter.y + targetCenter.y) / 2;
    const cross = horizontal
      ? (sourceCenter.y + targetCenter.y) / 2
      : (sourceCenter.x + targetCenter.x) / 2;
    const key = `${horizontal ? 'h' : 'v'}:${Math.round(midpoint / 4)}`;
    if (!gutterGroups.has(key)) gutterGroups.set(key, []);
    gutterGroups.get(key)!.push({ edge, cross });
  }
  for (const group of gutterGroups.values()) {
    group.sort((a, b) => a.cross - b.cross || a.edge.id.localeCompare(b.edge.id));
    group.forEach(({ edge }, index) => {
      laneOffsets.set(edge.id, (index - (group.length - 1) / 2) * BANDED_LANE_STEP);
    });
  }
  for (const group of loopbackGroups.values()) {
    group.sort((a, b) => a.span - b.span || a.edge.id.localeCompare(b.edge.id));
    group.forEach(({ edge }, index) => {
      laneOffsets.set(edge.id, index * BANDED_LANE_STEP);
    });
  }

  return edges.map((edge) => {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) return edge;
    const sourceCenter = center(source);
    const targetCenter = center(target);
    if (edge.type === 'loopback') {
      const side = loopbackSides.get(edge.id) ?? 'top';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: {
          ...(edge.data ?? {}),
          returnSide: side,
          returnLaneOffset: laneOffsets.get(edge.id) ?? 0,
        },
      };
    }
    if (edge.type !== 'spine') return edge;
    // Pick the dominant axis so the connector leaves/enters on the correct side in BOTH the
    // horizontal (LR) and vertical (TB) layouts. Horizontal-dominant → left/right handles;
    // vertical-dominant → top/bottom handles.
    const dx = targetCenter.x - sourceCenter.x;
    const dy = targetCenter.y - sourceCenter.y;

    // A spine edge that skips over a rank (e.g. an upper sibling → a shared fan-in target two rows
    // below) is normally drawn as a straight bottom→top (or right→left) segment. When another,
    // UNRELATED node happens to sit in that straight corridor — as when same-rank siblings are
    // stacked in one column above their common downstream target — the segment is drawn directly
    // through that intermediate card, making a real edge look like a dependency on the occluded
    // node. Detect that occlusion and route the edge out to a perpendicular side handle so React
    // Flow bows it AROUND the stack instead of through it. Non-occluded edges keep their handles.
    const corridorObstacles = (axis: 'vertical' | 'horizontal') => {
      const result: Array<{ cx: number; cy: number }> = [];
      for (const peer of nodes) {
        if (peer.id === edge.source || peer.id === edge.target) continue;
        const size = graphNodeSize(peer);
        const x0 = peer.position.x;
        const x1 = peer.position.x + size.width;
        const y0 = peer.position.y;
        const y1 = peer.position.y + size.height;
        if (axis === 'vertical') {
          const loY = Math.min(sourceCenter.y, targetCenter.y);
          const hiY = Math.max(sourceCenter.y, targetCenter.y);
          const corridorX = (sourceCenter.x + targetCenter.x) / 2;
          const peerCy = (y0 + y1) / 2;
          if (corridorX >= x0 && corridorX <= x1 && peerCy > loY && peerCy < hiY) {
            result.push({ cx: (x0 + x1) / 2, cy: peerCy });
          }
        } else {
          const loX = Math.min(sourceCenter.x, targetCenter.x);
          const hiX = Math.max(sourceCenter.x, targetCenter.x);
          const corridorY = (sourceCenter.y + targetCenter.y) / 2;
          const peerCx = (x0 + x1) / 2;
          if (corridorY >= y0 && corridorY <= y1 && peerCx > loX && peerCx < hiX) {
            result.push({ cx: peerCx, cy: (y0 + y1) / 2 });
          }
        }
      }
      return result;
    };

    if (Math.abs(dx) >= Math.abs(dy)) {
      const forward = dx >= 0;
      // Horizontal-dominant edge blocked by a node in the horizontal corridor → bow vertically.
      const blockers = corridorObstacles('horizontal');
      if (blockers.length > 0) {
        const corridorY = (sourceCenter.y + targetCenter.y) / 2;
        const above = blockers.filter((b) => b.cy < corridorY).length;
        const below = blockers.length - above;
        const side = below <= above ? 'bottom' : 'top';
        return {
          ...edge,
          sourceHandle: `source-${side}`,
          targetHandle: `target-${side}`,
          data: {
            ...(edge.data ?? {}),
            flowDirection: 'horizontal',
            reroutedAround: side,
            gutterLaneOffset: laneOffsets.get(edge.id) ?? 0,
          },
        };
      }
      return {
        ...edge,
        sourceHandle: forward ? 'source-right' : 'source-left',
        targetHandle: forward ? 'target-left' : 'target-right',
        data: {
          ...(edge.data ?? {}),
          flowDirection: 'horizontal',
          gutterLaneOffset: laneOffsets.get(edge.id) ?? 0,
        },
      };
    }
    const down = dy >= 0;
    // Vertical-dominant edge blocked by a node in the vertical corridor → bow horizontally.
    const blockers = corridorObstacles('vertical');
    if (blockers.length > 0) {
      const corridorX = (sourceCenter.x + targetCenter.x) / 2;
      const left = blockers.filter((b) => b.cx < corridorX).length;
      const right = blockers.length - left;
      const side = right <= left ? 'right' : 'left';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: {
          ...(edge.data ?? {}),
          flowDirection: 'vertical',
          reroutedAround: side,
          gutterLaneOffset: laneOffsets.get(edge.id) ?? 0,
        },
      };
    }
    return {
      ...edge,
      sourceHandle: down ? 'source-bottom' : 'source-top',
      targetHandle: down ? 'target-top' : 'target-bottom',
      data: {
        ...(edge.data ?? {}),
        flowDirection: 'vertical',
        gutterLaneOffset: laneOffsets.get(edge.id) ?? 0,
      },
    };
  });
}
