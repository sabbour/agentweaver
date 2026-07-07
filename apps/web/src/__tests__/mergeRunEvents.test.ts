import { describe, expect, it } from 'vitest';
import type { RunStreamEvent } from '../api/sse';
import { mergeRunEvents } from '../timeline/mergeRunEvents';

function evt(type: RunStreamEvent['type'], sequence = 0, payload: Record<string, unknown> = {}): RunStreamEvent {
  return { type, sequence, payload };
}

describe('mergeRunEvents', () => {
  it('preserves repeated seq-0 review events from seed and live streams', () => {
    const merged = mergeRunEvents(
      [evt('coordinator.assembly_review_requested', 0, { gateId: 'first' })],
      [evt('coordinator.assembly_review_requested', 0, { gateId: 'second' })],
    );

    expect(merged).toHaveLength(2);
    expect(merged.map((e) => e.payload['gateId'])).toEqual(['first', 'second']);
  });

  it('merges a late REST seed with newer live events without duplicating positive sequences', () => {
    const merged = mergeRunEvents(
      [evt('coordinator.assembly_started', 1), evt('coordinator.assembly_review_requested', 2)],
      [evt('coordinator.assembly_review_requested', 2), evt('coordinator.assembly_review_approved', 3)],
      { sort: true },
    );

    expect(merged.map((e) => `${e.sequence}:${e.type}`)).toEqual([
      '1:coordinator.assembly_started',
      '2:coordinator.assembly_review_requested',
      '3:coordinator.assembly_review_approved',
    ]);
  });

  it('still dedupes true singleton seq-0 terminal events by type', () => {
    const merged = mergeRunEvents(
      [evt('run.completed', 0, { summary: 'seed' })],
      [evt('run.completed', 0, { summary: 'live duplicate' })],
    );

    expect(merged).toHaveLength(1);
    expect(merged[0].payload['summary']).toBe('seed');
  });
});
