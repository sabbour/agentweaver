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
 * Column-aligned DAG layout. Runs dagre to determine rank (depth) assignments,
 * then snaps every node in the same rank to an exact virtual column X so cards
 * line up in clean vertical columns. Within each column nodes are stacked
 * top-to-bottom with uniform spacing, preserving dagre's vertical ordering.
 *
 * Use this for the coordinator run page where the "virtual column grid" look
 * is required. Other surfaces can keep using layoutDag directly.
 */
export function layoutDagColumns(
  nodes: Node[],
  edges: Edge[],
  opts: LayoutOpts = {},
  nodeSizeHints?: Record<string, NodeSizeHint>,
): Node[] {
  if (nodes.length === 0) return nodes;

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

  // Group nodes by dagre-assigned rank. In LR mode all nodes at the same depth
  // share the same X value from dagre. Round to absorb floating-point noise.
  const byRank = new Map<number, string[]>();
  for (const n of nodes) {
    const key = Math.round(g.node(n.id).x);
    if (!byRank.has(key)) byRank.set(key, []);
    byRank.get(key)!.push(n.id);
  }

  const COL_GAP = 72; // horizontal gap between column edges
  const ROW_GAP = 40; // vertical gap between stacked cards
  const MARGIN = 24;

  // Walk columns left → right, assign fixed X positions.
  const posMap = new Map<string, { x: number; y: number }>();
  let colX = MARGIN;
  for (const rankKey of [...byRank.keys()].sort((a, b) => a - b)) {
    const nodeIds = byRank.get(rankKey)!;
    // Preserve dagre's vertical ordering within the column.
    nodeIds.sort((a, b) => g.node(a).y - g.node(b).y);

    // Column width = widest card in this column.
    const colW = nodeIds.reduce((max, id) => {
      return Math.max(max, nodeSizeHints?.[id]?.width ?? NODE_W);
    }, 0);

    let rowY = MARGIN;
    for (const id of nodeIds) {
      const hint = nodeSizeHints?.[id];
      const h = hint?.height ?? NODE_H;
      const w = hint?.width ?? NODE_W;
      // Centre narrower cards within the column width.
      posMap.set(id, { x: colX + (colW - w) / 2, y: rowY });
      rowY += h + ROW_GAP;
    }

    colX += colW + COL_GAP;
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
