import Dagre from 'dagre';
import type { Edge, Node } from '@xyflow/react';

export const NODE_W = 200;
export const NODE_H = 108;

export interface LayoutOpts {
  rankdir?: 'LR' | 'TB';
  rankSep?: number;
  nodeSep?: number;
}

/**
 * Runs dagre auto-layout on the given nodes and edges.
 * Returns a new nodes array with computed positions.
 * Pass only forward (non-loopback) edges so dagre doesn't try to route cycles.
 */
export function layoutDag(nodes: Node[], edges: Edge[], opts: LayoutOpts = {}): Node[] {
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
    g.setNode(n.id, { width: NODE_W, height: NODE_H });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }

  Dagre.layout(g);

  return nodes.map((n) => {
    const pos = g.node(n.id);
    return { ...n, position: { x: pos.x - NODE_W / 2, y: pos.y - NODE_H / 2 } };
  });
}
