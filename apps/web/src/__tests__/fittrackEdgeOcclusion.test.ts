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
  layoutDagStaircase,
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

function size(id: string) {
  return subtaskIds.has(id)
    ? { w: SUBTASK_NODE_W, h: SUBTASK_NODE_H }
    : { w: FIXED_NODE_W, h: FIXED_NODE_H };
}

function layout(rankdir: 'LR' | 'TB') {
  const nodes: Node[] = ids.map((id) => {
    const s = size(id);
    return { id, position: { x: 0, y: 0 }, data: {}, initialWidth: s.w, initialHeight: s.h } as Node;
  });
  const fwdEdges: Edge[] = rawEdges.map(([source, target], i) => ({ id: `e${i}`, source, target }));
  const hints = Object.fromEntries(ids.map((id) => [id, { width: size(id).w, height: size(id).h }]));
  const laid = layoutDagStaircase(nodes, fwdEdges, {
    rankSep: COORD_GRAPH_RANK_SEP,
    nodeSep: COORD_GRAPH_NODE_SEP,
    targetAspect: 1.35,
    minStepRanks: 3,
    rankdir,
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
  return { box };
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

describe('FitTrack run 41eb1aa4 graph — Skyler/Hank occlusion', () => {
  it('has NO phantom Skyler->Hank edge in the descriptor', () => {
    expect(rawEdges.some(([s, t]) => s === SKYLER && t === HANK)).toBe(false);
    expect(rawEdges.some(([s, t]) => s === HANK && t === SKYLER)).toBe(false);
  });

  it('LR (default): keeps sibling tasks in one band and RAI in the next band', () => {
    const { box } = layout('LR');
    const skyler = box(SKYLER);
    const hank = box(HANK);
    const rai = box(RAI);

    // Siblings share a rank column; the downstream target advances to a new column.
    expect(Math.abs(skyler.cx - hank.cx)).toBeLessThanOrEqual(SUBTASK_NODE_W / 2);
    expect(rai.x0).toBeGreaterThan(Math.max(skyler.x1, hank.x1));
    expect(skyler.cy).toBeLessThan(hank.cy);
  });

  it('LR: the real Skyler->RAI edge corridor is no longer occluded by Hank', () => {
    const { box } = layout('LR');
    expect(verticalCorridorBlocked(SKYLER, RAI, ids, box)).toBe(false);
    expect(verticalCorridorBlocked(HANK, RAI, ids, box)).toBe(false);
  });
});
