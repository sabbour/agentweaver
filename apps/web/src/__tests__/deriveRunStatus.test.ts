import { describe, it, expect } from 'vitest';
import { deriveRunStatusFromEvents } from '../timeline/deriveRunStatus';
import type { RunStreamEvent } from '../api/sse';

function evt(type: RunStreamEvent['type'], seq = 0): RunStreamEvent {
  return { sequence: seq, type, payload: {} };
}

describe('deriveRunStatusFromEvents', () => {
  it('returns in_progress when stream is live and no lifecycle events have arrived', () => {
    expect(deriveRunStatusFromEvents([], true)).toBe('in_progress');
  });

  it('returns completed when stream is done and no lifecycle events have arrived', () => {
    expect(deriveRunStatusFromEvents([], false)).toBe('completed');
  });

  it('returns awaiting_review when the last lifecycle event is review.requested', () => {
    const events = [evt('agent.turn.start'), evt('review.requested', 1)];
    expect(deriveRunStatusFromEvents(events, false)).toBe('awaiting_review');
  });

  it('returns in_progress when revision.started follows review.requested', () => {
    const events = [
      evt('review.requested', 1),
      evt('review.changes_requested', 2),
      evt('revision.started', 3),
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('in_progress');
  });

  it('returns awaiting_review on second review.requested after revision.started', () => {
    // This is the core regression case: the bar must reappear at the second gate.
    const events = [
      evt('review.requested', 1),        // first gate
      evt('review.changes_requested', 2),
      evt('revision.started', 3),        // revision in progress
      evt('review.requested', 4),        // second gate
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('awaiting_review');
  });

  it('returns merged when merge.completed is the last lifecycle event', () => {
    const events = [
      evt('review.requested', 1),
      evt('review.approved', 2),
      evt('merge.started', 3),
      evt('merge.completed', 4),
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('merged');
  });

  it('returns merge_failed when merge.failed is the last lifecycle event', () => {
    const events = [
      evt('review.requested', 1),
      evt('review.approved', 2),
      evt('merge.failed', 3),
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('merge_failed');
  });

  it('returns declined when review.declined is the last lifecycle event', () => {
    const events = [
      evt('review.requested', 1),
      evt('review.declined', 2),
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('declined');
  });

  it('returns failed when run.failed is the last lifecycle event', () => {
    const events = [evt('run.failed', 1)];
    expect(deriveRunStatusFromEvents(events, false)).toBe('failed');
  });

  it('ignores non-lifecycle events and derives from lifecycle events only', () => {
    const events = [
      evt('agent.turn.start'),
      evt('tool.call'),
      evt('review.requested', 5),
      evt('tool.result'),
    ];
    expect(deriveRunStatusFromEvents(events, false)).toBe('awaiting_review');
  });
});
