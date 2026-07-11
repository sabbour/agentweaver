import Dagre from 'dagre';
import type { Edge, Node } from '@xyflow/react';
export const NODE_W = 200;
export const NODE_H = 145;
export const DAG_NODE_SEP = 96;

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

export interface NodeSizeHint {
  width: number;
  height: number;
}

export interface ConnectorPoint {
  x: number;
  y: number;
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
    width: NODE_TYPE_W[key] ?? NODE_W,
    height: RENDERED_NODE_TYPE_H[key] ?? RENDERED_DEFAULT_NODE_H,
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

  const LANE_GAP = 72; // gap between successive ranks (columns for LR, rows for TB)
  const CROSS_GAP = 40; // gap between stacked cards within a rank
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
