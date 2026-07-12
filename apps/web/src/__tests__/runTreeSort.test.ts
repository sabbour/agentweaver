import { describe, it, expect } from 'vitest';
import { compareRunTreeSiblings, type RunTreeSiblingMeta } from '../pages/CoordinatorRunPage';

// Regression for Ahmed's report: "The run tree is all over the place, it is not sorted
// properly at all." The run tree order is now DECOUPLED from wall-clock timestamps and from
// graph layout. Siblings read in canonical pipeline-stage order
// (Outcome plan → Work plan → subtasks → RAI → Build & Test → Human Review → Merge → Scribe),
// with the descriptor emission `order` and numeric subtask key as deterministic tiebreakers.
// startedAt is intentionally NOT a sort key — sorting by it caused the tree to jump around as
// stages started at different times.

function meta(overrides: Partial<RunTreeSiblingMeta> & { nodeId: string }): RunTreeSiblingMeta {
  return {
    label: overrides.nodeId,
    isSubtask: true,
    roleKey: 'subtask',
    order: 0,
    x: 0,
    y: 0,
    ...overrides,
  };
}

const orderOf = (metas: RunTreeSiblingMeta[]) =>
  [...metas].sort(compareRunTreeSiblings).map((m) => m.nodeId);

describe('compareRunTreeSiblings — canonical pipeline-stage ordering', () => {
  it('orders siblings by canonical pipeline stage regardless of input/arrival order', () => {
    // Deliberately shuffled input (arrival/descriptor order does NOT match stage order).
    const nodes = [
      meta({ nodeId: 'scribe', label: 'Scribe', roleKey: 'scribe', isSubtask: false, order: 1 }),
      meta({ nodeId: 'work-plan', label: 'Work plan', roleKey: 'work_plan', isSubtask: false, order: 6 }),
      meta({ nodeId: 'rai', label: 'RAI', roleKey: 'rai', isSubtask: false, order: 2 }),
      meta({ nodeId: 'outcome-plan', label: 'Outcome plan', roleKey: 'outcome_plan', isSubtask: false, order: 9 }),
      meta({ nodeId: 'plan:subtask-1', label: 'Subtask 1', roleKey: 'subtask', isSubtask: true, order: 7 }),
      meta({ nodeId: 'merge', label: 'Merge', roleKey: 'merge', isSubtask: false, order: 0 }),
      meta({ nodeId: 'build-test', label: 'Build & Test', roleKey: 'build_test', isSubtask: false, order: 3 }),
      meta({ nodeId: 'review', label: 'Review', roleKey: 'review', isSubtask: false, order: 4 }),
    ];
    expect(orderOf(nodes)).toEqual([
      'outcome-plan',
      'work-plan',
      'plan:subtask-1',
      'rai',
      'build-test',
      'review',
      'merge',
      'scribe',
    ]);
  });

  it('places Outcome plan before Work plan (canonical planning order)', () => {
    const nodes = [
      meta({ nodeId: 'work-plan', roleKey: 'work_plan', isSubtask: false, order: 1 }),
      meta({ nodeId: 'outcome-plan', roleKey: 'outcome_plan', isSubtask: false, order: 2 }),
    ];
    // Even though 'work-plan' has the lower descriptor order, stage rank puts Outcome first.
    expect(orderOf(nodes)).toEqual(['outcome-plan', 'work-plan']);
  });

  it('orders subtask siblings by numeric subtask key (not lexical), independent of startedAt', () => {
    const nodes = [
      // startedAt is reversed vs the desired order to prove it is ignored.
      meta({ nodeId: 'plan:subtask-10', startedAt: 100 }),
      meta({ nodeId: 'plan:subtask-2', startedAt: 200 }),
      meta({ nodeId: 'plan:subtask-1', startedAt: 300 }),
    ];
    expect(orderOf(nodes)).toEqual(['plan:subtask-1', 'plan:subtask-2', 'plan:subtask-10']);
  });

  it('is fully decoupled from startedAt — timestamps never change the order', () => {
    // A pending (no startedAt) subtask-1 must still sort before a started subtask-2.
    const nodes = [
      meta({ nodeId: 'plan:subtask-2', startedAt: 500 }),
      meta({ nodeId: 'plan:subtask-1' }),
    ];
    expect(orderOf(nodes)).toEqual(['plan:subtask-1', 'plan:subtask-2']);
  });

  it('uses descriptor emission `order` to break ties among same-rank/same-key nodes', () => {
    // Same numeric subtask key → subtaskSortKey ties → descriptor `order` decides, deterministically.
    const a = meta({ nodeId: 'a:subtask-1', order: 5 });
    const b = meta({ nodeId: 'b:subtask-1', order: 1 });
    expect(orderOf([a, b])).toEqual(['b:subtask-1', 'a:subtask-1']);
    expect(orderOf([b, a])).toEqual(['b:subtask-1', 'a:subtask-1']);
  });
});
