import { describe, expect, it } from 'vitest';
import { isRunFinished, runState } from './runState';

describe('runState', () => {
  it('maps owner execution states to the work-state words', () => {
    expect(runState('blocked')).toEqual({ label: 'Needs input', color: 'warning' });
    expect(runState('idle')).toEqual({ label: 'Open', color: 'informative' });
    expect(runState('active')).toEqual({ label: 'Working', color: 'brand' });
    expect(runState('completed')).toEqual({ label: 'Done', color: 'success' });
    expect(runState('failed')).toEqual({ label: 'Failed', color: 'danger' });
    expect(runState('indeterminate')).toEqual({ label: 'Outcome unknown', color: 'severe' });
    expect(runState('cancelled')).toEqual({ label: 'Cancelled', color: 'subtle' });
  });

  it('shows missing or unknown states as Unavailable, never as Done', () => {
    // Matching is exact and case-sensitive, so upper-case values are unknown too.
    for (const value of [null, undefined, '', 'CANCELLED', 'COMPLETED'])
      expect(runState(value)).toEqual({ label: 'Unavailable', color: 'subtle' });
  });

  it('keeps every label distinct', () => {
    const states = ['blocked', 'idle', 'active', 'completed', 'failed', 'cancelled', 'indeterminate', null];
    const labels = states.map((state) => runState(state).label);
    expect(new Set(labels).size).toBe(states.length);
  });
});

describe('isRunFinished', () => {
  it('is true only for completed, failed, cancelled, and indeterminate', () => {
    expect(['completed', 'failed', 'cancelled', 'indeterminate'].every(isRunFinished)).toBe(true);
    for (const value of ['blocked', 'idle', 'active', 'other', null, undefined])
      expect(isRunFinished(value)).toBe(false);
  });
});
