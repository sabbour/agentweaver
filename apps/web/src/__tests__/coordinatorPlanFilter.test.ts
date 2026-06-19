import { describe, it, expect } from 'vitest';
import { isSerializedWorkPlan, stripSerializedWorkPlanMessages } from '../timeline/coordinatorPlanFilter';
import type { TimelineItem } from '../timeline/types';

describe('isSerializedWorkPlan', () => {
  it('recognizes a raw work-plan JSON array (title + scope on every element)', () => {
    const plan = JSON.stringify([
      { title: 'A', scope: 'do x', role: 'r', depends_on: [] },
      { title: 'B', scope: 'do y', depends_on: [1] },
    ]);
    expect(isSerializedWorkPlan(plan)).toBe(true);
  });

  it('recognizes a plan wrapped in prose / code fences (tolerant extraction)', () => {
    const plan = 'Here is the plan:\n```json\n[{"title":"A","scope":"x"}]\n```';
    expect(isSerializedWorkPlan(plan)).toBe(true);
  });

  it('does not match ordinary assistant prose', () => {
    expect(isSerializedWorkPlan('Decomposing the outcome into subtasks.')).toBe(false);
  });

  it('does not match an unrelated JSON array (no title/scope shape)', () => {
    expect(isSerializedWorkPlan('[1, 2, 3]')).toBe(false);
    expect(isSerializedWorkPlan('[{"foo":"bar"}]')).toBe(false);
  });

  it('handles empty / non-array content safely', () => {
    expect(isSerializedWorkPlan('')).toBe(false);
    expect(isSerializedWorkPlan('{}')).toBe(false);
    expect(isSerializedWorkPlan('not json [oops')).toBe(false);
  });
});

describe('stripSerializedWorkPlanMessages', () => {
  it('drops a turn group whose only step is the serialized plan', () => {
    const items: TimelineItem[] = [
      {
        kind: 'turn-group',
        turnId: 't1',
        turnIndex: 1,
        active: false,
        steps: [
          { kind: 'agent-message', messageId: 'm1', content: '[{"title":"A","scope":"x"}]', streaming: false },
        ],
      },
    ];
    expect(stripSerializedWorkPlanMessages(items)).toHaveLength(0);
  });

  it('keeps ordinary prose messages and non-plan steps', () => {
    const items: TimelineItem[] = [
      {
        kind: 'turn-group',
        turnId: 't1',
        turnIndex: 1,
        active: false,
        steps: [
          { kind: 'agent-message', messageId: 'm1', content: 'Planning now.', streaming: false },
          { kind: 'agent-message', messageId: 'm2', content: '[{"title":"A","scope":"x"}]', streaming: false },
        ],
      },
    ];
    const out = stripSerializedWorkPlanMessages(items);
    expect(out).toHaveLength(1);
    const group = out[0] as Extract<TimelineItem, { kind: 'turn-group' }>;
    expect(group.steps).toHaveLength(1);
    expect((group.steps[0] as { content: string }).content).toBe('Planning now.');
  });

  it('leaves non-turn-group items untouched', () => {
    const items: TimelineItem[] = [
      { kind: 'lifecycle', event: { sequence: 1, type: 'coordinator.work_plan', payload: { subtasks: [{}, {}] } } },
    ];
    expect(stripSerializedWorkPlanMessages(items)).toEqual(items);
  });
});
