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

// Compact workflow-definition / visual-editor nodes all render through WorkflowNode's short pill
// face, regardless of semantic node_type. Keep these hints close to the actual rendered pills so
// fitView and routed edges size to the visible cards rather than oversized virtual boxes.
export const WORKFLOW_PILL_NODE_W = FIXED_NODE_W;
export const WORKFLOW_PILL_NODE_H: Record<string, number> = {
  agent: 92,
  subtask: 84,
  gate: 72,
  action: 92,
  terminal: 64,
};
export const WORKFLOW_PILL_DEFAULT_NODE_H = 84;
export const WORKFLOW_FIT_VIEW_OPTIONS = { padding: 0.12, maxZoom: 1.35 } as const;

// Stair tread length: how many nodes advance along the SAME row (LR) / column (TB) before the stair
// steps down/right to the next tread. A value of 2 keeps chunky, clearly-horizontal treads (instead of
// stepping after every single node → a fine 45° diagonal) while still packing into a square-ish box.
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
}): SteppedConnectorRoute {
  const { sourceX, sourceY, targetX, targetY, orientation = 'auto' } = input;
  const vertical = orientation === 'vertical'
    || (orientation === 'auto' && Math.abs(targetY - sourceY) >= Math.abs(targetX - sourceX));
  const points = vertical
    ? [
        { x: sourceX, y: sourceY },
        { x: sourceX, y: (sourceY + targetY) / 2 },
        { x: targetX, y: (sourceY + targetY) / 2 },
        { x: targetX, y: targetY },
      ]
    : [
        { x: sourceX, y: sourceY },
        { x: (sourceX + targetX) / 2, y: sourceY },
        { x: (sourceX + targetX) / 2, y: targetY },
        { x: targetX, y: targetY },
      ];
  const clean = dedupePoints(points);
  return {
    path: roundedOrthogonalPath(clean),
    labelX: (sourceX + targetX) / 2,
    labelY: (sourceY + targetY) / 2,
    points: clean,
  };
}

export function workflowNodeSizeHint(nodeType?: string | null): NodeSizeHint {
  const key = nodeType ?? '';
  return {
    width: WORKFLOW_PILL_NODE_W,
    height: WORKFLOW_PILL_NODE_H[key] ?? WORKFLOW_PILL_DEFAULT_NODE_H,
  };
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
    let laneStart = MARGIN;
    for (const rankKey of sortedRankKeys) {
      const nodeIds = byRank.get(rankKey)!;
      // Preserve dagre's cross-axis ordering within the rank.
      nodeIds.sort((a, b) => g.node(a).y - g.node(b).y);

      // Rank lanes run left→right; cards stack top→bottom within each column.
      const colW = nodeIds.reduce((max, id) => Math.max(max, nodeSizeHints?.[id]?.width ?? NODE_W), 0);
      let crossY = MARGIN;
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
 * Staircase (stepped / monotonic diagonal) DAG layout for long, mostly-linear runs.
 *
 * A pure LR row (Coordinator → Outcome → Work plan → subagents → RAI → Review → Merge → Scribe)
 * overflows width while leaving the panel's height empty; once fit-to-view scales that wide-and-short
 * shape down, the nodes become unreadably small. This layout keeps dagre's data-driven rank + ordering
 * (so true parallel branches still fan out within a rank) but walks the ranks as an ALTERNATING
 * orthogonal stair so the sequence uses BOTH dimensions and packs into a square-ish box.
 *
 * Rather than a uniform 45° diagonal (which leaves two large empty triangles), each step advances only
 * ONE axis, alternating: odd steps advance the primary axis, even steps advance the cross axis. The flow
 * is monotonic — both axes only ever increase, so it always progresses one way (never wraps/reverses):
 *   • `rankdir: 'LR'` — right, down, right, down… (primary axis = X): the spine steps right, then down,
 *     then right, then down, descending to the right in a compact staircase.
 *   • `rankdir: 'TB'` — down, right, down, right… (primary axis = Y): mirrored, descending down-right.
 *
 * Every column hosts at most two ranks (a right-arrival and the following down-departure), separated by
 * a cross advance sized to clear the previous rank's full sibling stack, so ranks can never collide.
 */
export function layoutDagStaircase(
  nodes: Node[],
  edges: Edge[],
  opts: StaircaseLayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  if (nodes.length === 0) return nodes;

  const rankdir = opts.rankdir ?? 'LR';
  const horizontal = rankdir !== 'TB'; // LR ⇒ primary axis is X (spine runs left→right)

  // 1. Dagre determines the rank (depth) of every node and the cross-axis ordering within a rank.
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

  const primaryOf = (id: string) => (horizontal ? g.node(id).x : g.node(id).y);
  const crossOf = (id: string) => (horizontal ? g.node(id).y : g.node(id).x);

  // Node size projected onto the primary axis (advances rank-to-rank) and the cross axis (stacks
  // parallel nodes within a rank).
  const primarySize = (id: string) => {
    const hint = nodeSizeHints?.[id];
    return horizontal ? (hint?.width ?? NODE_W) : (hint?.height ?? NODE_H);
  };
  const crossSize = (id: string) => {
    const hint = nodeSizeHints?.[id];
    return horizontal ? (hint?.height ?? NODE_H) : (hint?.width ?? NODE_W);
  };

  // Stable emission index (the descriptor's declared node order) drives a deterministic tie-break so
  // repeated layouts (e.g. Tidy) never reshuffle siblings that share a rank/cross coordinate.
  const emissionIndex = new Map<string, number>();
  nodes.forEach((n, i) => emissionIndex.set(n.id, i));

  // 2. Group nodes by dagre rank; order within a rank by cross coordinate, then by emission index so
  //    identical input always yields identical ordering (independent of Array.sort stability).
  const byRank = new Map<number, string[]>();
  for (const n of nodes) {
    const key = Math.round(primaryOf(n.id));
    if (!byRank.has(key)) byRank.set(key, []);
    byRank.get(key)!.push(n.id);
  }
  const rankKeys = [...byRank.keys()].sort((a, b) => a - b);
  for (const key of rankKeys) {
    byRank.get(key)!.sort((a, b) => {
      const dc = crossOf(a) - crossOf(b);
      if (Math.abs(dc) > 0.5) return dc;
      return (emissionIndex.get(a) ?? 0) - (emissionIndex.get(b) ?? 0);
    });
  }
  const ranks = rankKeys.map((key) => byRank.get(key)!);
  const R = ranks.length;

  const laneGap = opts.rankSep ?? 72;   // primary-axis gap between successive ranks
  const crossGap = opts.nodeSep ?? 40;  // cross-axis gap between stacked parallel nodes
  const MARGIN = 24;

  // Primary-axis pitch is computed PER adjacent-rank transition so compact→compact steps do not inherit
  // the spacing required by a tall/wide subtask elsewhere in the graph. Each transition still reserves
  // enough room for the larger of the two participating ranks plus the configured lane gap.
  const rankPrimaryExtent = ranks.map((ids) => ids.reduce((max, id) => Math.max(max, primarySize(id)), 0));

  const rankCrossExtent = (ids: string[]) =>
    ids.reduce((sum, id) => sum + crossSize(id), 0) + Math.max(0, ids.length - 1) * crossGap;
  const maxRankCross = ranks.reduce((m, ids) => Math.max(m, rankCrossExtent(ids)), 0);

  // Normalize public options so a caller can never poison coordinates: targetAspect must be positive
  // & finite, and an explicit stepOffset is coerced to a finite value and clamped to [0, ceil].
  const rawAspect = opts.targetAspect ?? 1.4;
  const targetAspect = Number.isFinite(rawAspect) && rawAspect > 0 ? rawAspect : 1.4;
  void targetAspect; // retained for API compatibility; the orthogonal stair is self-distributing
  const minStepRanks = opts.minStepRanks ?? 3;
  const stepCeil = maxRankCross + crossGap; // don't out-run a full rank stack per step

  // 3. Optional explicit cross-advance floor. Short chains stay a straight line (all right steps).
  const straight = R <= minStepRanks;
  const explicitStep =
    opts.stepOffset != null
      ? Math.round(
          Math.min(stepCeil, Math.max(0, Number.isFinite(opts.stepOffset) ? opts.stepOffset : 0)),
        )
      : 0;

  // 4. Alternating orthogonal stair with chunky treads. Advance the primary axis (→ right) for a run of
  //    STAIR_RUN successive ranks (one tread), then take a single cross-axis step (↓ down) to the next
  //    tread, and repeat. This holds 2+ nodes per row instead of stepping after every node, while both
  //    axes still accumulate monotonically (never decrease) and parallel siblings fan out on the cross
  //    axis at their shared rank. A down step clears the previous rank's full sibling stack, and because
  //    the down step keeps the primary column, each column hosts at most a right-arrival + a down-
  //    departure (separated by that clearance), so ranks can never collide.
  const run = Math.max(1, STAIR_RUN);
  const positions = new Map<string, { x: number; y: number }>();
  let primaryPos = MARGIN;
  let crossPos = MARGIN;
  for (let rankIndex = 0; rankIndex < R; rankIndex += 1) {
    if (rankIndex > 0) {
      // Step down only when starting a new tread (every `run` ranks); otherwise advance along the tread.
      const advancePrimary = straight || rankIndex % run !== 0;
      if (advancePrimary) {
        primaryPos += Math.max(rankPrimaryExtent[rankIndex - 1], rankPrimaryExtent[rankIndex]) + laneGap;
      } else {
        const prevExtent = rankCrossExtent(ranks[rankIndex - 1]);
        crossPos += Math.max(prevExtent + crossGap, explicitStep);
      }
    }
    const ids = ranks[rankIndex];
    let cross = crossPos;
    for (const id of ids) {
      const x = horizontal ? primaryPos : cross;
      const y = horizontal ? cross : primaryPos;
      positions.set(id, { x: Math.round(x), y: Math.round(y) });
      cross += crossSize(id) + crossGap;
    }
  }

  return nodes.map((n) => {
    const hint = nodeSizeHints?.[n.id];
    return {
      ...n,
      position: positions.get(n.id) ?? n.position,
      initialWidth: hint?.width ?? NODE_W,
      initialHeight: hint?.height ?? NODE_H,
    };
  });
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
  return edges.map((edge) => {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) return edge;
    const sourceCenter = center(source);
    const targetCenter = center(target);
    if (edge.type === 'loopback') {
      const rowPeers = (node: Node, nodeCenter: { x: number; y: number }) =>
        nodes
          .filter((peer) => peer.id !== node.id)
          .map((peer) => center(peer))
          .filter((peerCenter) => Math.abs(peerCenter.y - nodeCenter.y) <= 1);
      const rightCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x > sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x > targetCenter.x),
      ].length;
      const leftCrossings = [
        ...rowPeers(source, sourceCenter).filter((peer) => peer.x < sourceCenter.x),
        ...rowPeers(target, targetCenter).filter((peer) => peer.x < targetCenter.x),
      ].length;
      const side = leftCrossings < rightCrossings ? 'left' : 'right';
      return {
        ...edge,
        sourceHandle: `source-${side}`,
        targetHandle: `target-${side}`,
        data: { ...(edge.data ?? {}), returnSide: side },
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
          data: { ...(edge.data ?? {}), flowDirection: 'horizontal', reroutedAround: side },
        };
      }
      return {
        ...edge,
        sourceHandle: forward ? 'source-right' : 'source-left',
        targetHandle: forward ? 'target-left' : 'target-right',
        data: { ...(edge.data ?? {}), flowDirection: 'horizontal' },
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
        data: { ...(edge.data ?? {}), flowDirection: 'vertical', reroutedAround: side },
      };
    }
    return {
      ...edge,
      sourceHandle: down ? 'source-bottom' : 'source-top',
      targetHandle: down ? 'target-top' : 'target-bottom',
      data: { ...(edge.data ?? {}), flowDirection: 'vertical' },
    };
  });
}
