import dagre from 'dagre';
import type { DiagramSource } from './diagramTypes';

export const FONT_SIZE = 14;
export const LINE_HEIGHT = 18;
export const NODE_PAD_X = 16;
export const NODE_PAD_Y = 10;
export const GROUP_LABEL_HEIGHT = 26;
export const GROUP_PAD = 14;

export interface LaidOutBox {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface LaidOutNode extends LaidOutBox {
  label: string;
  variant: string;
}

export interface LaidOutGroup extends LaidOutBox {
  label: string;
}

export interface LaidOutEdge {
  source: string;
  target: string;
  label?: string;
  style?: 'solid' | 'dashed' | 'plain';
  points: { x: number; y: number }[];
}

export interface DiagramLayout {
  width: number;
  height: number;
  nodes: LaidOutNode[];
  groups: LaidOutGroup[];
  edges: LaidOutEdge[];
}

/** Measures each line of a node's label with the real (browser) font metrics. */
function measureLabel(ctx: CanvasRenderingContext2D, label: string, fontWeight = '400') {
  ctx.font = `${fontWeight} ${FONT_SIZE}px Segoe UI, system-ui, -apple-system, sans-serif`;
  const lines = label.split('\n');
  const maxWidth = Math.max(...lines.map((line) => ctx.measureText(line).width));
  return { lines, maxWidth };
}

/**
 * Lays out a diagram with `dagre`'s compound-graph support: groups are plain
 * dagre nodes with no fixed size, so dagre computes a bounding box that
 * encloses their children automatically (nested groups included).
 */
export function layoutDiagram(source: DiagramSource): DiagramLayout {
  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d')!;

  const g = new dagre.graphlib.Graph({ compound: true });
  g.setGraph({
    rankdir: source.direction,
    nodesep: 44,
    ranksep: 70,
    marginx: 20,
    marginy: 20,
  });
  g.setDefaultEdgeLabel(() => ({}));

  const groups = source.groups ?? [];
  const nodeMeta = new Map<string, { label: string; variant: string; lines: string[] }>();

  for (const group of groups) {
    g.setNode(group.id, { label: group.label });
  }
  for (const node of source.nodes) {
    const { lines, maxWidth } = measureLabel(ctx, node.label, node.variant === 'core' || node.variant === 'workerStyle' ? '600' : '400');
    const width = Math.ceil(maxWidth) + NODE_PAD_X * 2;
    const height = lines.length * LINE_HEIGHT + NODE_PAD_Y * 2;
    g.setNode(node.id, { width, height });
    nodeMeta.set(node.id, { label: node.label, variant: node.variant, lines });
  }
  for (const group of groups) {
    if (group.parent) g.setParent(group.id, group.parent);
  }
  for (const node of source.nodes) {
    if (node.parent) g.setParent(node.id, node.parent);
  }
  for (const edge of source.edges) {
    g.setEdge(edge.source, edge.target, { label: edge.label, style: edge.style ?? 'solid' });
  }

  dagre.layout(g);

  const laidOutNodes: LaidOutNode[] = source.nodes.map((node) => {
    const box = g.node(node.id);
    return {
      id: node.id,
      x: box.x - box.width / 2,
      y: box.y - box.height / 2,
      width: box.width,
      height: box.height,
      label: node.label,
      variant: node.variant,
    };
  });

  // Groups: dagre gives the tight bbox around children; inflate for a visible
  // border + a label header band above the children (drawn, not laid out, so
  // it never perturbs sibling spacing).
  const laidOutGroups: LaidOutGroup[] = groups.map((group) => {
    const box = g.node(group.id);
    return {
      id: group.id,
      x: box.x - box.width / 2 - GROUP_PAD,
      y: box.y - box.height / 2 - GROUP_PAD - GROUP_LABEL_HEIGHT,
      width: box.width + GROUP_PAD * 2,
      height: box.height + GROUP_PAD * 2 + GROUP_LABEL_HEIGHT,
      label: group.label,
    };
  });

  const laidOutEdges: LaidOutEdge[] = source.edges.map((edge) => {
    const dagreEdge = g.edge(edge.source, edge.target);
    return {
      source: edge.source,
      target: edge.target,
      label: edge.label,
      style: edge.style ?? 'solid',
      points: dagreEdge.points,
    };
  });

  const graphInfo = g.graph();
  // Outer bounds must also cover inflated group boxes (their header band can
  // extend above the raw graph bbox that dagre reports).
  const minX = Math.min(0, ...laidOutGroups.map((box) => box.x));
  const minY = Math.min(0, ...laidOutGroups.map((box) => box.y));
  const maxX = Math.max(graphInfo.width ?? 0, ...laidOutGroups.map((box) => box.x + box.width));
  const maxY = Math.max(graphInfo.height ?? 0, ...laidOutGroups.map((box) => box.y + box.height));

  const offsetX = -minX + 16;
  const offsetY = -minY + 16;
  const shift = (box: LaidOutBox) => {
    box.x += offsetX;
    box.y += offsetY;
  };
  laidOutNodes.forEach(shift);
  laidOutGroups.forEach(shift);
  laidOutEdges.forEach((edge) => {
    edge.points = edge.points.map((point) => ({ x: point.x + offsetX, y: point.y + offsetY }));
  });

  return {
    width: maxX - minX + 32,
    height: maxY - minY + 32,
    nodes: laidOutNodes,
    groups: laidOutGroups,
    edges: laidOutEdges,
  };
}
