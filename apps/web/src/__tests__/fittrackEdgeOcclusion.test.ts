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
  layoutDagBalancedGrid,
  layoutDagStaircase,
  routeGridEdges,
  SUBTASK_NODE_W,
  SUBTASK_NODE_H,
  FIXED_NODE_W,
  FIXED_NODE_H,
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
  rect: ReturnType<ReturnType<typeof layout>['box']>,
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
