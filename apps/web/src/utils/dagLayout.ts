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

export interface NodeSizeHint {
  width: number;
  height: number;
}

export function workflowNodeSizeHint(nodeType?: string | null): NodeSizeHint {
  const key = nodeType ?? '';
  return {
    width: NODE_TYPE_W[key] ?? NODE_W,
    height: RENDERED_NODE_TYPE_H[key] ?? RENDERED_DEFAULT_NODE_H,
  };
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
