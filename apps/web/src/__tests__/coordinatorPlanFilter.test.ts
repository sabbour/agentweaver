import {
  isOutcomeSpecMessagePrefix,
  isSerializedWorkPlan,
} from '../timeline/coordinatorPlanFilter';
import { describe, expect, it } from 'vitest';
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

describe('isOutcomeSpecMessagePrefix', () => {
  it('recognizes incomplete canonical outcome-spec JSON without matching ordinary text', () => {
    expect(isOutcomeSpecMessagePrefix('{"desired_out')).toBe(true);
    expect(isOutcomeSpecMessagePrefix('  { "desiredOutcome":')).toBe(true);
    expect(isOutcomeSpecMessagePrefix('Drafting an outcome plan now.')).toBe(false);
    expect(isOutcomeSpecMessagePrefix('{"description":"An ordinary JSON response"}')).toBe(false);
  });
});
