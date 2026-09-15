// Regression proof for the "Skyler -> Hank" arrow the user reported on run 41eb1aa4.
//
// Backend truth (verified separately against SubtaskDependencies + coordinator.graph seq 99):
//   the ONLY edges into/near Hank are Jesse->Hank and Hank->RAI. There is NO Skyler->Hank edge.
//   Skyler and Hank are SIBLINGS at the same dependency rank (both depend on the design task via
//   Jesse), and both fan into the RAI assembly gate.
//
// The banded-lane layout keeps the siblings in one rank band and advances the
// shared downstream target into the next band. This prevents the real
// Skyler->RAI edge from crossing Hank's card.
import { describe, it, expect } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import {
  buildSteppedConnectorRoute,
  connectorDirectionMarkers,
  graphNodeSize,
  layoutDagBalancedGrid,
  layoutDagStaircase,
  routeGridEdges,
  SUBTASK_NODE_W,
  SUBTASK_NODE_H,
  FIXED_NODE_W,
  FIXED_NODE_H,
  TOPOLOGY_CONNECTOR_CARD_CLEARANCE,
  TOPOLOGY_CONNECTOR_DIRECTION_MARKER_SPACING,
  TOPOLOGY_CONNECTOR_LANE_GAP,
} from '../utils/dagLayout';

const COORD_GRAPH_RANK_SEP = 40;
const COORD_GRAPH_NODE_SEP = 20;

const COORDINATOR = 'coordinator';
const WALT = 'plan:subtask-359';
const JESSE = 'plan:subtask-360';
const SKYLER = 'plan:subtask-361';
const HANK = 'plan:subtask-362';
const RAI = 'planned:assembly-rai';
const RUBBERDUCK = 'planned:assembly-rubberduck';
const BUILD = 'planned:assembly-build-test';
const REVIEW = 'planned:assembly-review';
const MERGE = 'planned:assembly-merge';
const SCRIBE = 'planned:assembly-scribe';

const subtaskIds = new Set([WALT, JESSE, SKYLER, HANK]);
const ids = [COORDINATOR, WALT, JESSE, SKYLER, HANK, RAI, RUBBERDUCK, BUILD, REVIEW, MERGE, SCRIBE];

// The exact forward edge set from the coordinator.graph descriptor for this run.
const rawEdges: Array<[string, string]> = [
  [COORDINATOR, WALT],
  [WALT, JESSE],
  [WALT, SKYLER],
  [JESSE, SKYLER],
  [JESSE, HANK],
  [SKYLER, RAI],
  [HANK, RAI],
  [RAI, RUBBERDUCK],
  [RUBBERDUCK, BUILD],
  [BUILD, REVIEW],
  [REVIEW, MERGE],
  [MERGE, SCRIBE],
];

const engines = [
  { name: 'legacy staircase', id: 'legacy-staircase' },
  { name: 'balanced grid', id: 'balanced-grid' },
] as const;

function size(id: string) {
  return subtaskIds.has(id)
    ? { w: SUBTASK_NODE_W, h: SUBTASK_NODE_H }
    : { w: FIXED_NODE_W, h: FIXED_NODE_H };
}

function layout(engine: (typeof engines)[number]['id']) {
  const nodes: Node[] = ids.map((id) => {
    const s = size(id);
    return { id, position: { x: 0, y: 0 }, data: {}, initialWidth: s.w, initialHeight: s.h } as Node;
  });
  const fwdEdges: Edge[] = rawEdges.map(([source, target], i) => ({ id: `e${i}`, source, target, type: 'spine' }));
  const hints = Object.fromEntries(ids.map((id) => [id, { width: size(id).w, height: size(id).h }]));
  const laid = engine === 'legacy-staircase'
    ? layoutDagStaircase(nodes, fwdEdges, {
      rankSep: COORD_GRAPH_RANK_SEP,
      nodeSep: COORD_GRAPH_NODE_SEP,
      targetAspect: 1.35,
      minStepRanks: 3,
      rankdir: 'LR',
    }, hints)
    : layoutDagBalancedGrid(nodes, fwdEdges, {
      rankSep: COORD_GRAPH_RANK_SEP,
      nodeSep: COORD_GRAPH_NODE_SEP,
      minColumns: 1,
      maxColumns: 4,
    }, hints);
  const byId = new Map(laid.map((n) => [n.id, n]));
  const box = (id: string) => {
    const n = byId.get(id)!;
    const s = size(id);
    return {
      x0: n.position.x, y0: n.position.y, x1: n.position.x + s.w, y1: n.position.y + s.h,
      cx: n.position.x + s.w / 2, cy: n.position.y + s.h / 2,
    };
  };
  return { box, nodes: laid, edges: fwdEdges };
}

// Mirrors the corridor-occlusion predicate in routeGridEdges (CoordinatorRunPage.tsx): does any node
// other than src/tgt sit in the straight vertical corridor between the two node centers?
function verticalCorridorBlocked(
  src: string, tgt: string, others: string[], box: (id: string) => ReturnType<ReturnType<typeof layout>['box']>,
) {
  const s = box(src);
  const t = box(tgt);
  const loY = Math.min(s.cy, t.cy);
  const hiY = Math.max(s.cy, t.cy);
  const corridorX = (s.cx + t.cx) / 2;
  return others.some((id) => {
    if (id === src || id === tgt) return false;
    const p = box(id);
    return corridorX >= p.x0 && corridorX <= p.x1 && p.cy > loY && p.cy < hiY;
  });
}

function handlePoint(node: Node, handle: string | null | undefined) {
  const rect = {
    x0: node.position.x,
    y0: node.position.y,
    x1: node.position.x + size(node.id).w,
    y1: node.position.y + size(node.id).h,
  };
  const side = handle?.split('-').at(-1);
  if (side === 'left') return { x: rect.x0, y: (rect.y0 + rect.y1) / 2 };
  if (side === 'right') return { x: rect.x1, y: (rect.y0 + rect.y1) / 2 };
  if (side === 'top') return { x: (rect.x0 + rect.x1) / 2, y: rect.y0 };
  if (side === 'bottom') return { x: (rect.x0 + rect.x1) / 2, y: rect.y1 };
  return { x: (rect.x0 + rect.x1) / 2, y: (rect.y0 + rect.y1) / 2 };
}

function segmentCrossesRect(
  from: { x: number; y: number },
  to: { x: number; y: number },
  rect: { x0: number; y0: number; x1: number; y1: number },
) {
  if (Math.abs(from.x - to.x) < 0.5) {
    const x = from.x;
    if (x <= rect.x0 || x >= rect.x1) return false;
    return Math.max(from.y, to.y) > rect.y0 && Math.min(from.y, to.y) < rect.y1;
  }
  if (Math.abs(from.y - to.y) < 0.5) {
    const y = from.y;
    if (y <= rect.y0 || y >= rect.y1) return false;
    return Math.max(from.x, to.x) > rect.x0 && Math.min(from.x, to.x) < rect.x1;
  }
  return false;
}

describe('FitTrack run 41eb1aa4 graph — Skyler/Hank occlusion', () => {
  it('has NO phantom Skyler->Hank edge in the descriptor', () => {
    expect(rawEdges.some(([s, t]) => s === SKYLER && t === HANK)).toBe(false);
    expect(rawEdges.some(([s, t]) => s === HANK && t === SKYLER)).toBe(false);
  });

  it.each(engines)('$name: keeps sibling tasks in one band and RAI in the next band', ({ id }) => {
    const { box } = layout(id);
    const skyler = box(SKYLER);
    const hank = box(HANK);
    const rai = box(RAI);

    // Siblings share one rank band; the downstream target advances to a new band.
    const sharedRankBand = Math.abs(skyler.cx - hank.cx) <= SUBTASK_NODE_W / 2 ||
      Math.abs(skyler.cy - hank.cy) <= SUBTASK_NODE_H / 2;
    const raiAdvanced = rai.x0 > Math.max(skyler.x1, hank.x1) ||
      rai.y0 > Math.max(skyler.y1, hank.y1);

    expect(sharedRankBand).toBe(true);
    expect(raiAdvanced).toBe(true);
  });

  it.each(engines)('$name: the real Skyler->RAI edge corridor is no longer occluded by Hank', ({ id }) => {
    const { box } = layout(id);
    expect(verticalCorridorBlocked(SKYLER, RAI, ids, box)).toBe(false);
    expect(verticalCorridorBlocked(HANK, RAI, ids, box)).toBe(false);
  });

  it.each(engines)('$name: routed edges do not cross unrelated cards', ({ id }) => {
    const { nodes, edges, box } = layout(id);
    const byId = new Map(nodes.map((node) => [node.id, node]));
    const routed = routeGridEdges(edges, nodes);

    for (const edge of routed) {
      const data = edge.data as {
        flowDirection?: 'horizontal' | 'vertical';
        gutterLaneOffset?: number;
        routePoints?: Array<{ x: number; y: number }>;
      } | undefined;
      const source = handlePoint(byId.get(edge.source)!, edge.sourceHandle);
      const target = handlePoint(byId.get(edge.target)!, edge.targetHandle);
      const points = data?.routePoints && data.routePoints.length >= 2
        ? data.routePoints
        : buildSteppedConnectorRoute({
          sourceX: source.x,
          sourceY: source.y,
          targetX: target.x,
          targetY: target.y,
          orientation: data?.flowDirection,
          laneOffset: data?.gutterLaneOffset,
        }).points;

      for (let index = 0; index < points.length - 1; index += 1) {
        for (const nodeId of ids) {
          if (nodeId === edge.source || nodeId === edge.target) continue;
          expect(
            segmentCrossesRect(points[index], points[index + 1], box(nodeId)),
            `${id}:${edge.id} crosses ${nodeId}`,
          ).toBe(false);
        }
      }
    }
  });
});

const WANDERLY_COORDINATOR = 'wanderly-coordinator';
const WANDERLY_OUTCOME = 'wanderly-outcome-plan';
const WANDERLY_WORK = 'wanderly-work-plan';
const WANDERLY_SPEC = 'wanderly-define-product-spec';
const WANDERLY_BACKEND = 'wanderly-build-backend-api';
const WANDERLY_FRONTEND = 'wanderly-build-frontend-ui';
const WANDERLY_PREVIEW = 'wanderly-start-stack-preview-url';
const WANDERLY_RAI = 'wanderly-rai-check';
const WANDERLY_RUBBERDUCK = 'wanderly-rubberduck-review';
const WANDERLY_BUILD = 'wanderly-build-test';
const WANDERLY_REVIEW = 'wanderly-review-gate';
const WANDERLY_MERGE = 'wanderly-merge';
const WANDERLY_SCRIBE = 'wanderly-scribe';

const wanderlySubtasks = new Set([
  WANDERLY_SPEC,
  WANDERLY_BACKEND,
  WANDERLY_FRONTEND,
  WANDERLY_PREVIEW,
]);

const wanderlyIds = [
  WANDERLY_COORDINATOR,
  WANDERLY_OUTCOME,
  WANDERLY_WORK,
  WANDERLY_SPEC,
  WANDERLY_BACKEND,
  WANDERLY_FRONTEND,
  WANDERLY_PREVIEW,
  WANDERLY_RAI,
  WANDERLY_RUBBERDUCK,
  WANDERLY_BUILD,
  WANDERLY_REVIEW,
  WANDERLY_MERGE,
  WANDERLY_SCRIBE,
];

const wanderlyRawEdges: Array<[string, string]> = [
  [WANDERLY_COORDINATOR, WANDERLY_OUTCOME],
  [WANDERLY_OUTCOME, WANDERLY_WORK],
  [WANDERLY_WORK, WANDERLY_SPEC],
  [WANDERLY_WORK, WANDERLY_BACKEND],
  [WANDERLY_WORK, WANDERLY_FRONTEND],
  [WANDERLY_SPEC, WANDERLY_BACKEND],
  [WANDERLY_BACKEND, WANDERLY_FRONTEND],
  [WANDERLY_BACKEND, WANDERLY_PREVIEW],
  [WANDERLY_FRONTEND, WANDERLY_PREVIEW],
  [WANDERLY_PREVIEW, WANDERLY_RAI],
  [WANDERLY_RAI, WANDERLY_RUBBERDUCK],
  [WANDERLY_RUBBERDUCK, WANDERLY_BUILD],
  [WANDERLY_BUILD, WANDERLY_REVIEW],
  [WANDERLY_REVIEW, WANDERLY_MERGE],
  [WANDERLY_MERGE, WANDERLY_SCRIBE],
];

function wanderlySize(id: string) {
  return wanderlySubtasks.has(id)
    ? { w: SUBTASK_NODE_W, h: SUBTASK_NODE_H }
    : { w: FIXED_NODE_W, h: FIXED_NODE_H };
}

function layoutWanderly(engine: (typeof engines)[number]['id']) {
  const nodes: Node[] = wanderlyIds.map((id) => {
    const s = wanderlySize(id);
    return { id, position: { x: 0, y: 0 }, data: {}, initialWidth: s.w, initialHeight: s.h } as Node;
  });
  const fwdEdges: Edge[] = wanderlyRawEdges.map(([source, target], i) => ({
    id: `wanderly-e${i}-${source}-${target}`,
    source,
    target,
    type: 'spine',
  }));
  const hints = Object.fromEntries(wanderlyIds.map((id) => [
    id,
    { width: wanderlySize(id).w, height: wanderlySize(id).h },
  ]));
  const laid = engine === 'legacy-staircase'
    ? layoutDagStaircase(nodes, fwdEdges, {
      rankSep: COORD_GRAPH_RANK_SEP,
      nodeSep: COORD_GRAPH_NODE_SEP,
      targetAspect: 1.35,
      minStepRanks: 3,
      rankdir: 'LR',
    }, hints)
    : layoutDagBalancedGrid(nodes, fwdEdges, {
      rankSep: COORD_GRAPH_RANK_SEP,
      nodeSep: COORD_GRAPH_NODE_SEP,
      minColumns: 1,
      maxColumns: 4,
    }, hints);
  return { nodes: laid, edges: fwdEdges };
}

function graphRect(node: Node) {
  const size = graphNodeSize(node);
  return {
    x0: node.position.x,
    y0: node.position.y,
    x1: node.position.x + size.width,
    y1: node.position.y + size.height,
  };
}

function handlePointByGraphSize(node: Node, handle: string | null | undefined) {
  const rect = graphRect(node);
  const side = handle?.split('-').at(-1);
  if (side === 'left') return { x: rect.x0, y: (rect.y0 + rect.y1) / 2 };
  if (side === 'right') return { x: rect.x1, y: (rect.y0 + rect.y1) / 2 };
  if (side === 'top') return { x: (rect.x0 + rect.x1) / 2, y: rect.y0 };
  if (side === 'bottom') return { x: (rect.x0 + rect.x1) / 2, y: rect.y1 };
  return { x: (rect.x0 + rect.x1) / 2, y: (rect.y0 + rect.y1) / 2 };
}

function routedPoints(edge: Edge, nodes: Map<string, Node>) {
  const data = edge.data as {
    flowDirection?: 'horizontal' | 'vertical';
    gutterLaneOffset?: number;
    routePoints?: Array<{ x: number; y: number }>;
  } | undefined;
  if (data?.routePoints && data.routePoints.length >= 2) return data.routePoints;
  const source = handlePointByGraphSize(nodes.get(edge.source)!, edge.sourceHandle);
  const target = handlePointByGraphSize(nodes.get(edge.target)!, edge.targetHandle);
  return buildSteppedConnectorRoute({
    sourceX: source.x,
    sourceY: source.y,
    targetX: target.x,
    targetY: target.y,
    orientation: data?.flowDirection,
    laneOffset: data?.gutterLaneOffset,
  }).points;
}

function intervalGap(a0: number, a1: number, b0: number, b1: number): number {
  if (a1 < b0) return b0 - a1;
  if (b1 < a0) return a0 - b1;
  return 0;
}

function pointDistanceToRect(point: { x: number; y: number }, rect: ReturnType<typeof graphRect>): number {
  const dx = point.x < rect.x0 ? rect.x0 - point.x : point.x > rect.x1 ? point.x - rect.x1 : 0;
  const dy = point.y < rect.y0 ? rect.y0 - point.y : point.y > rect.y1 ? point.y - rect.y1 : 0;
  return Math.hypot(dx, dy);
}

function segmentDistanceToRect(
  from: { x: number; y: number },
  to: { x: number; y: number },
  rect: ReturnType<typeof graphRect>,
): number {
  if (segmentCrossesRect(from, to, rect)) return 0;
  if (Math.abs(from.x - to.x) < 0.5) {
    const y0 = Math.min(from.y, to.y);
    const y1 = Math.max(from.y, to.y);
    if (from.x >= rect.x0 && from.x <= rect.x1) return intervalGap(y0, y1, rect.y0, rect.y1);
    if (intervalGap(y0, y1, rect.y0, rect.y1) === 0) {
      return Math.min(Math.abs(from.x - rect.x0), Math.abs(from.x - rect.x1));
    }
  }
  if (Math.abs(from.y - to.y) < 0.5) {
    const x0 = Math.min(from.x, to.x);
    const x1 = Math.max(from.x, to.x);
    if (from.y >= rect.y0 && from.y <= rect.y1) return intervalGap(x0, x1, rect.x0, rect.x1);
    if (intervalGap(x0, x1, rect.x0, rect.x1) === 0) {
      return Math.min(Math.abs(from.y - rect.y0), Math.abs(from.y - rect.y1));
    }
  }
  return Math.min(
    pointDistanceToRect(from, rect),
    pointDistanceToRect(to, rect),
  );
}

function routeLength(points: Array<{ x: number; y: number }>): number {
  return points.slice(1).reduce((sum, point, index) => {
    const prev = points[index];
    return sum + Math.hypot(point.x - prev.x, point.y - prev.y);
  }, 0);
}

function routedSegments(edge: Edge, points: Array<{ x: number; y: number }>) {
  return points.slice(1).flatMap((point, index) => {
    const from = points[index];
    if (Math.abs(from.x - point.x) < 0.5 && Math.abs(from.y - point.y) > 0.5) {
      return [{
        edge,
        orientation: 'vertical' as const,
        constant: from.x,
        start: Math.min(from.y, point.y),
        end: Math.max(from.y, point.y),
      }];
    }
    if (Math.abs(from.y - point.y) < 0.5 && Math.abs(from.x - point.x) > 0.5) {
      return [{
        edge,
        orientation: 'horizontal' as const,
        constant: from.y,
        start: Math.min(from.x, point.x),
        end: Math.max(from.x, point.x),
      }];
    }
    return [];
  });
}

describe('Wanderly dense topology connector readability', () => {
  it.each(engines)('$name: keeps connectors clear of cards they do not terminate at', ({ id }) => {
    const { nodes, edges } = layoutWanderly(id);
    const byId = new Map(nodes.map((node) => [node.id, node]));
    const routed = routeGridEdges(edges, nodes);

    for (const edge of routed) {
      const points = routedPoints(edge, byId);
      for (let index = 0; index < points.length - 1; index += 1) {
        for (const node of nodes) {
          if (node.id === edge.source || node.id === edge.target) continue;
          const distance = segmentDistanceToRect(points[index], points[index + 1], graphRect(node));
          expect(
            distance,
            `${id}:${edge.id} passes ${distance}px from ${node.id}`,
          ).toBeGreaterThanOrEqual(TOPOLOGY_CONNECTOR_CARD_CLEARANCE - 0.5);
        }
      }
    }
  });

  it.each(engines)('$name: puts shared corridors in separate visible lanes', ({ id }) => {
    const { nodes, edges } = layoutWanderly(id);
    const byId = new Map(nodes.map((node) => [node.id, node]));
    const routed = routeGridEdges(edges, nodes);
    const segments = routed.flatMap((edge) => routedSegments(edge, routedPoints(edge, byId)));

    for (let leftIndex = 0; leftIndex < segments.length; leftIndex += 1) {
      for (let rightIndex = leftIndex + 1; rightIndex < segments.length; rightIndex += 1) {
        const left = segments[leftIndex];
        const right = segments[rightIndex];
        if (left.edge.id === right.edge.id || left.orientation !== right.orientation) continue;
        if (left.edge.target === right.edge.source || right.edge.target === left.edge.source) continue;
        if (Math.abs(left.constant - right.constant) > 0.5) continue;
        const overlap = Math.min(left.end, right.end) - Math.max(left.start, right.start);
        const samePort = left.edge.source === right.edge.source || left.edge.target === right.edge.target;
        expect(
          overlap,
          `${id}:${left.edge.id} and ${right.edge.id} share a ${left.orientation} lane`,
        ).toBeLessThanOrEqual(samePort ? TOPOLOGY_CONNECTOR_LANE_GAP : TOPOLOGY_CONNECTOR_CARD_CLEARANCE);
      }
    }
  });

  it.each(engines)('$name: gives every connector direction markers at default zoom', ({ id }) => {
    const { nodes, edges } = layoutWanderly(id);
    const byId = new Map(nodes.map((node) => [node.id, node]));
    const routed = routeGridEdges(edges, nodes);

    for (const edge of routed) {
      const points = routedPoints(edge, byId);
      const expectedMarkerCount = Math.max(1, Math.ceil(routeLength(points) / TOPOLOGY_CONNECTOR_DIRECTION_MARKER_SPACING));
      expect(
        connectorDirectionMarkers(points).length,
        `${id}:${edge.id} lacks in-path direction markers`,
      ).toBe(expectedMarkerCount);
    }
  });
});
