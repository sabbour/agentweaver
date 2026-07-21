import dagre from 'dagre';
import { useEffect, useMemo, useState } from 'react';
import {
  Background,
  MarkerType,
  ReactFlow,
  type Edge,
  type Node,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { CardNode, GroupNode, CARD_WIDTH } from './nodes';
import { neutral, radius } from './theme';
import type { GraphSpec } from './types';

const CARD_HEIGHT_2LINE = 92;
const CARD_HEIGHT_3LINE = 116;
const GROUP_PAD_SIDE = 24;
const GROUP_PAD_TOP = 44;
const GROUP_PAD_BOTTOM = 20;
const CANVAS_MARGIN = 60;

const nodeTypes = { card: CardNode, group: GroupNode };

interface Box { x: number; y: number; width: number; height: number }

function unionBox(boxes: Box[]): Box {
  const x1 = Math.min(...boxes.map((b) => b.x));
  const y1 = Math.min(...boxes.map((b) => b.y));
  const x2 = Math.max(...boxes.map((b) => b.x + b.width));
  const y2 = Math.max(...boxes.map((b) => b.y + b.height));
  return { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
}

/**
 * Lays out a graph-spec using dagre's native compound-graph clustering
 * (`setParent`), so grouped and ungrouped nodes are ranked/positioned in one
 * holistic pass -- dagre itself reserves each cluster's rectangle and keeps
 * unrelated (ungrouped) nodes from being placed on top of it. An earlier
 * version of this function did a flat (non-compound) layout and derived group
 * boxes as a bbox post-pass; that produced visually-broken output where
 * ungrouped nodes (e.g. a standalone "PostgreSQL" card) could land inside
 * another group's bounding box purely by rank/coordinate coincidence, making
 * it look like they belonged to a group they weren't tagged with. Compound
 * dagre avoids that class of bug structurally instead of by more padding.
 */
function layout(spec: GraphSpec): { nodes: Node[]; edges: Edge[]; canvasWidth: number; canvasHeight: number } {
  const g = new dagre.graphlib.Graph({ compound: true });
  g.setGraph({ rankdir: spec.direction ?? 'TB', nodesep: 60, ranksep: 110, marginx: 20, marginy: 20 });
  g.setDefaultEdgeLabel(() => ({}));

  const groups = spec.groups ?? [];

  for (const grp of groups) {
    g.setNode(grp.id, {});
    if (grp.parent) g.setParent(grp.id, grp.parent);
  }
  for (const n of spec.nodes) {
    const height = n.meta ? CARD_HEIGHT_3LINE : CARD_HEIGHT_2LINE;
    g.setNode(n.id, { width: CARD_WIDTH, height });
    if (n.group) g.setParent(n.id, n.group);
  }
  for (const e of spec.edges) {
    g.setEdge(e.from, e.to);
  }
  dagre.layout(g);

  const leafBoxes = new Map<string, Box>();
  for (const n of spec.nodes) {
    const gn = g.node(n.id);
    const height = n.meta ? CARD_HEIGHT_3LINE : CARD_HEIGHT_2LINE;
    leafBoxes.set(n.id, { x: gn.x - CARD_WIDTH / 2, y: gn.y - height / 2, width: CARD_WIDTH, height });
  }

  // dagre computes each cluster (parent) node's own x/y/width/height post-layout,
  // encompassing its descendants -- add our own visual padding on top of that.
  const groupBoxes = new Map<string, Box>();
  for (const grp of groups) {
    const gn = g.node(grp.id);
    if (!gn || !gn.width || !gn.height) continue;
    const inner: Box = { x: gn.x - gn.width / 2, y: gn.y - gn.height / 2, width: gn.width, height: gn.height };
    groupBoxes.set(grp.id, {
      x: inner.x - GROUP_PAD_SIDE,
      y: inner.y - GROUP_PAD_TOP,
      width: inner.width + GROUP_PAD_SIDE * 2,
      height: inner.height + GROUP_PAD_TOP + GROUP_PAD_BOTTOM,
    });
  }

  const nodes: Node[] = [];
  // Shallowest tier first so outer group backgrounds paint before nested ones.
  const groupsByDepthAsc = [...groups].sort((a, b) => a.tier - b.tier);
  for (const grp of groupsByDepthAsc) {
    const box = groupBoxes.get(grp.id);
    if (!box) continue;
    nodes.push({
      id: `group:${grp.id}`,
      type: 'group',
      position: { x: box.x, y: box.y },
      style: { width: box.width, height: box.height },
      data: { label: grp.label, tier: grp.tier },
      draggable: false,
      selectable: false,
      // Negative so groups always render behind default-z-index (0) edges as
      // well as cards -- otherwise a cluster's opaque background paints over
      // edges that pass through it (deeper tiers still sit above shallower
      // ones, preserving nested-group paint order).
      zIndex: -100 + grp.tier,
    });
  }
  for (const n of spec.nodes) {
    const box = leafBoxes.get(n.id)!;
    nodes.push({
      id: n.id,
      type: 'card',
      position: { x: box.x, y: box.y },
      data: n as unknown as Record<string, unknown>,
      draggable: false,
      selectable: false,
      zIndex: 10,
    });
  }

  const edges: Edge[] = spec.edges.map((e, i) => ({
    id: `e${i}-${e.from}-${e.to}`,
    source: e.from,
    target: e.to,
    label: e.label,
    type: 'smoothstep',
    style: {
      stroke: neutral.foreground4,
      strokeWidth: 1.5,
      strokeDasharray: e.dashed ? '5 4' : undefined,
    },
    labelStyle: { fill: neutral.foreground3, fontSize: 11, fontWeight: 600 },
    labelBgStyle: { fill: neutral.background1, fillOpacity: 0.9 },
    labelBgPadding: [4, 2],
    labelBgBorderRadius: 4,
    markerEnd: e.undirected ? undefined : { type: MarkerType.ArrowClosed, color: neutral.foreground4, width: 16, height: 16 },
  }));

  const allBoxes = [...leafBoxes.values(), ...groupBoxes.values()];
  const bbox = unionBox(allBoxes);
  const canvasWidth = bbox.width + CANVAS_MARGIN * 2;
  const canvasHeight = bbox.height + CANVAS_MARGIN * 2;

  // Shift everything so the union bbox starts at (CANVAS_MARGIN, CANVAS_MARGIN).
  const dx = CANVAS_MARGIN - bbox.x;
  const dy = CANVAS_MARGIN - bbox.y;
  for (const n of nodes) {
    n.position = { x: n.position.x + dx, y: n.position.y + dy };
  }

  return { nodes, edges, canvasWidth, canvasHeight };
}

export interface DiagramCanvasProps {
  spec: GraphSpec;
  onReady?: () => void;
}

export function DiagramCanvas({ spec, onReady }: DiagramCanvasProps) {
  const { nodes, edges, canvasWidth, canvasHeight } = useMemo(() => layout(spec), [spec]);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (ready) onReady?.();
  }, [ready, onReady]);

  return (
    <div
      id="diagram-root"
      data-diagram-ready={ready ? 'true' : 'false'}
      style={{
        width: canvasWidth,
        height: canvasHeight,
        borderRadius: radius.container,
        border: `1px solid ${neutral.stroke2}`,
        backgroundColor: neutral.background1,
        overflow: 'hidden',
      }}
    >
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        fitView
        fitViewOptions={{ padding: 0.04 }}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        panOnDrag={false}
        zoomOnScroll={false}
        zoomOnPinch={false}
        zoomOnDoubleClick={false}
        proOptions={{ hideAttribution: true }}
        onInit={() => {
          // Let fitView's layout pass settle before signalling Playwright.
          requestAnimationFrame(() => requestAnimationFrame(() => setReady(true)));
        }}
      >
        <Background color={neutral.stroke2} gap={24} size={1} />
      </ReactFlow>
    </div>
  );
}
