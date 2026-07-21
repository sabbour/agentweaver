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
import { RoutedEdge, type Point, type RoutedEdgeData } from './edges';
import { neutral, radius } from './theme';
import type { GraphSpec } from './types';

const CARD_HEIGHT_2LINE = 92;
const CARD_HEIGHT_3LINE = 116;
const GROUP_PAD_SIDE = 24;
const GROUP_PAD_TOP = 44;
const GROUP_PAD_BOTTOM = 20;
const CANVAS_MARGIN = 60;

const nodeTypes = { card: CardNode, group: GroupNode };
const edgeTypes = { routed: RoutedEdge };

interface Box { x: number; y: number; width: number; height: number }

function unionBox(boxes: Box[]): Box {
  const x1 = Math.min(...boxes.map((b) => b.x));
  const y1 = Math.min(...boxes.map((b) => b.y));
  const x2 = Math.max(...boxes.map((b) => b.x + b.width));
  const y2 = Math.max(...boxes.map((b) => b.y + b.height));
  return { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
}

interface LabelBox {
  index: number;
  /** Approximate un-offset label center, in the same coordinate space as node positions. */
  cx: number;
  cy: number;
  width: number;
  height: number;
  /** Resolved offset applied on top of (cx, cy) -- and, at render time, on top of
   * whatever labelX/labelY the actual smoothstep path produces. */
  dx: number;
  dy: number;
}

/** Rough label footprint for an 11px/600-weight sans-serif string with the
 * edge label's own padding, used only to decide how far apart two labels
 * need to be pushed -- doesn't need to be pixel-exact. */
function estimateLabelSize(text: string): { width: number; height: number } {
  const CHAR_WIDTH = 6.4;
  const PADDING_X = 12;
  return { width: Math.max(28, text.length * CHAR_WIDTH + PADDING_X), height: 20 };
}

/**
 * Pushes apart any edge labels whose estimated bounding boxes overlap each
 * other, and additionally pushes any label off of a fixed obstacle (a node
 * card or group container) it starts overlapping -- otherwise a label
 * shoved sideways to dodge a sibling label can end up sliding underneath a
 * card, where the card's opaque background clips half its text.
 */
function resolveLabelCollisions(items: LabelBox[], obstacles: Box[]): void {
  const GAP = 4;
  const MAX_ITERATIONS = 80;
  for (let iter = 0; iter < MAX_ITERATIONS; iter += 1) {
    let moved = false;
    for (let i = 0; i < items.length; i += 1) {
      for (let j = i + 1; j < items.length; j += 1) {
        const a = items[i];
        const b = items[j];
        const ax = a.cx + a.dx;
        const ay = a.cy + a.dy;
        const bx = b.cx + b.dx;
        const by = b.cy + b.dy;
        const overlapX = (a.width + b.width) / 2 + GAP - Math.abs(ax - bx);
        const overlapY = (a.height + b.height) / 2 + GAP - Math.abs(ay - by);
        if (overlapX > 0 && overlapY > 0) {
          moved = true;
          if (overlapX < overlapY) {
            const push = overlapX / 2 + 0.5;
            if (ax <= bx) { a.dx -= push; b.dx += push; } else { a.dx += push; b.dx -= push; }
          } else {
            const push = overlapY / 2 + 0.5;
            if (ay <= by) { a.dy -= push; b.dy += push; } else { a.dy += push; b.dy -= push; }
          }
        }
      }
    }
    for (const a of items) {
      const ax = a.cx + a.dx;
      const ay = a.cy + a.dy;
      for (const obstacle of obstacles) {
        const ocx = obstacle.x + obstacle.width / 2;
        const ocy = obstacle.y + obstacle.height / 2;
        const overlapX = (a.width + obstacle.width) / 2 + GAP - Math.abs(ax - ocx);
        const overlapY = (a.height + obstacle.height) / 2 + GAP - Math.abs(ay - ocy);
        if (overlapX > 0 && overlapY > 0) {
          moved = true;
          // Obstacles are fixed -- move the label entirely. Prefer a
          // vertical nudge over a horizontal one: this is a TB dagre layout,
          // so there's always generous clearance above/below a card (the
          // ranksep gap between rows), but the horizontal gap between two
          // same-row sibling cards (nodesep) can be *narrower* than a label,
          // in which case picking "whichever overlap is smaller" ping-pongs
          // the label back and forth between two neighboring cards forever
          // without ever fully clearing either one.
          a.dy += ay <= ocy ? -overlapY - 0.5 : overlapY + 0.5;
        }
      }
    }
    if (!moved) break;
  }
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
  const g = new dagre.graphlib.Graph({ compound: true, multigraph: true });
  g.setGraph({ rankdir: spec.direction ?? 'TB', nodesep: 80, edgesep: 30, ranksep: 120, marginx: 20, marginy: 20 });
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
  // Give every edge a stable name (index) so parallel edges between the same
  // pair of nodes are kept distinct, and hand dagre each label's estimated
  // footprint so it *reserves* space for the label in the layout (positioning
  // it along the routed poly-line via edge.x/edge.y) instead of letting every
  // label default to the shared path midpoint.
  spec.edges.forEach((e, i) => {
    const edgeLabel: Record<string, unknown> = {};
    if (e.label) {
      const { width, height } = estimateLabelSize(e.label);
      edgeLabel.width = width;
      edgeLabel.height = height;
      edgeLabel.labelpos = 'c';
    }
    g.setEdge(e.from, e.to, edgeLabel, `e${i}`);
  });
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

  // Pull dagre's routed poly-line and reserved label anchor for every edge.
  // dagre routes each edge through per-rank waypoints chosen to avoid the node
  // cards, and -- because labelled edges were given a width/height above --
  // reserves a non-overlapping slot for each label along that route (edge.x/
  // edge.y). Seeding the collision pass from those reserved anchors (rather
  // than a shared path midpoint) means labels start already separated; the
  // pass then only has to break any residual overlap and keep labels off the
  // opaque card backgrounds.
  const routedPoints: Point[][] = [];
  const labelAnchors: (Point | undefined)[] = [];
  const labelBoxes: LabelBox[] = [];
  spec.edges.forEach((e, i) => {
    const ge = g.edge(e.from, e.to, `e${i}`) as { points?: Point[]; x?: number; y?: number } | undefined;
    const pts: Point[] = (ge?.points ?? []).map((p) => ({ x: p.x, y: p.y }));
    routedPoints.push(pts);
    if (e.label && ge && typeof ge.x === 'number' && typeof ge.y === 'number') {
      const anchor = { x: ge.x, y: ge.y };
      labelAnchors.push(anchor);
      const { width, height } = estimateLabelSize(e.label);
      labelBoxes.push({ index: i, cx: anchor.x, cy: anchor.y, width, height, dx: 0, dy: 0 });
    } else {
      labelAnchors.push(undefined);
    }
  });
  // Cards obscure a label with a solid background (unlike the translucent
  // group containers, which labels are expected to sit on top of), so only
  // node cards are treated as fixed obstacles here -- group boxes are not.
  resolveLabelCollisions(labelBoxes, [...leafBoxes.values()]);
  const labelOffsetByIndex = new Map<number, { dx: number; dy: number }>(
    labelBoxes.map((l) => [l.index, { dx: l.dx, dy: l.dy }]),
  );

  const allBoxes = [...leafBoxes.values(), ...groupBoxes.values()];
  const bbox = unionBox(allBoxes);
  const canvasWidth = bbox.width + CANVAS_MARGIN * 2;
  const canvasHeight = bbox.height + CANVAS_MARGIN * 2;

  // Shift everything so the union bbox starts at (CANVAS_MARGIN, CANVAS_MARGIN).
  // Edge poly-lines and label anchors come out of dagre in the same
  // pre-shift coordinate space as the nodes, so they get the same translation.
  const dx = CANVAS_MARGIN - bbox.x;
  const dy = CANVAS_MARGIN - bbox.y;
  for (const n of nodes) {
    n.position = { x: n.position.x + dx, y: n.position.y + dy };
  }

  const edges: Edge[] = spec.edges.map((e, i) => {
    const shiftedPoints = routedPoints[i].map((p) => ({ x: p.x + dx, y: p.y + dy }));
    const anchor = labelAnchors[i];
    const labelPos = anchor ? { x: anchor.x + dx, y: anchor.y + dy } : undefined;
    return {
      id: `e${i}-${e.from}-${e.to}`,
      source: e.from,
      target: e.to,
      label: e.label,
      type: 'routed',
      data: {
        points: shiftedPoints,
        labelPos,
        labelOffset: labelOffsetByIndex.get(i) ?? { dx: 0, dy: 0 },
      } satisfies RoutedEdgeData,
      style: {
        stroke: neutral.foreground4,
        strokeWidth: 1.5,
        strokeDasharray: e.dashed ? '5 4' : undefined,
      },
      markerEnd: e.undirected ? undefined : { type: MarkerType.ArrowClosed, color: neutral.foreground4, width: 16, height: 16 },
    };
  });

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
        edgeTypes={edgeTypes}
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
