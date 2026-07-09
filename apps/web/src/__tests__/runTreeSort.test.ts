import { describe, it, expect } from 'vitest';
import { compareRunTreeSiblings, type RunTreeSiblingMeta } from '../pages/CoordinatorRunPage';

// Regression for Ahmed's report: "The run tree is all over the place, it is not
// sorted properly at all." Siblings must read chronologically by startedAt, and the
// GUID-nodeId case (no `subtask-<n>` match) must order by startedAt instead of the
// old effectively-random localeCompare on GUIDs.

function meta(overrides: Partial<RunTreeSiblingMeta> & { nodeId: string }): RunTreeSiblingMeta {
  return {
    label: overrides.nodeId,
    isSubtask: true,
    roleKey: 'subtask',
    x: 0,
    y: 0,
    ...overrides,
  };
}

const orderOf = (metas: RunTreeSiblingMeta[]) =>
  [...metas].sort(compareRunTreeSiblings).map((m) => m.nodeId);

describe('compareRunTreeSiblings — run tree chronological ordering', () => {
  it('orders subtask siblings by startedAt ascending even when input is out of order', () => {
    const nodes = [
      meta({ nodeId: 'subtask-3', startedAt: 3000 }),
      meta({ nodeId: 'subtask-1', startedAt: 1000 }),
      meta({ nodeId: 'subtask-2', startedAt: 2000 }),
    ];
    expect(orderOf(nodes)).toEqual(['subtask-1', 'subtask-2', 'subtask-3']);
  });

  it('orders GUID-style nodeIds (no subtask-<n> match) by startedAt, not random localeCompare', () => {
    // Chosen so alphabetical/localeCompare order (fff…, aaa…, mmm…) differs from
    // chronological order. Previously these fell back to localeCompare on the GUID and
    // rendered "all over the place"; now startedAt drives the order.
    const nodes = [
      meta({ nodeId: 'fff11111-0000-0000-0000-000000000000', startedAt: 100 }),
      meta({ nodeId: 'aaa99999-0000-0000-0000-000000000000', startedAt: 300 }),
      meta({ nodeId: 'mmm55555-0000-0000-0000-000000000000', startedAt: 200 }),
    ];
    expect(orderOf(nodes)).toEqual([
      'fff11111-0000-0000-0000-000000000000',
      'mmm55555-0000-0000-0000-000000000000',
      'aaa99999-0000-0000-0000-000000000000',
    ]);
  });

  it('places a sibling WITH startedAt before one WITHOUT (pending trails)', () => {
    const nodes = [
      meta({ nodeId: 'subtask-pending' }),
      meta({ nodeId: 'subtask-started', startedAt: 500 }),
    ];
    expect(orderOf(nodes)).toEqual(['subtask-started', 'subtask-pending']);
  });

  it('falls back to the deterministic subtask key when neither has startedAt', () => {
    const nodes = [
      meta({ nodeId: 'subtask-10' }),
      meta({ nodeId: 'subtask-2' }),
      meta({ nodeId: 'subtask-1' }),
    ];
    // numeric-aware subtaskSortKey → 1, 2, 10 (not lexical 1, 10, 2)
    expect(orderOf(nodes)).toEqual(['subtask-1', 'subtask-2', 'subtask-10']);
  });

  it('is stable/deterministic for equal startedAt (secondary key breaks the tie)', () => {
    const a = meta({ nodeId: 'subtask-2', startedAt: 1000 });
    const b = meta({ nodeId: 'subtask-1', startedAt: 1000 });
    // equal startedAt → secondary numeric subtask key orders 1 before 2, regardless of input order
    expect(orderOf([a, b])).toEqual(['subtask-1', 'subtask-2']);
    expect(orderOf([b, a])).toEqual(['subtask-1', 'subtask-2']);
  });
});
