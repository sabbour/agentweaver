import { renderHook } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { RunStreamEvent } from '../api/sse';

const streamState = vi.hoisted(() => ({
  events: [] as RunStreamEvent[],
}));

vi.mock('../hooks/useSeededRunStream', () => ({
  useSeededRunStream: () => ({
    events: streamState.events,
    status: 'done',
    error: null,
    droppedEventCount: 0,
    reconnect: vi.fn(),
  }),
}));

vi.mock('../api/apiClient', () => ({
  apiClient: {
    steerCoordinator: vi.fn(),
    confirmOutcomeSpec: vi.fn(),
    reviseOutcomeSpec: vi.fn(),
    reviewAssembly: vi.fn(),
  },
}));

import { useCoordinatorRunModel } from '../hooks/useCoordinatorRunModel';

function evt(sequence: number, type: RunStreamEvent['type'], payload: Record<string, unknown> = {}): RunStreamEvent {
  return { sequence, type, payload };
}

describe('useCoordinatorRunModel gate derivation', () => {
  beforeEach(() => {
    streamState.events = [];
  });

  it('does not mark assembly review pending for automated build-test or rubberduck gates', () => {
    streamState.events = [
      evt(1, 'coordinator.assembly_review_requested', { gateKind: 'build-test' }),
      evt(2, 'coordinator.assembly_review_requested', { gateKind: 'rubberduck' }),
    ];

    const { result } = renderHook(() => useCoordinatorRunModel('run-1'));

    expect(result.current.gates.assemblyReviewPending).toBe(false);
  });

  it('marks assembly review pending for human-review and legacy review events', () => {
    streamState.events = [
      evt(1, 'coordinator.assembly_review_requested', { gateKind: 'human-review' }),
    ];
    const human = renderHook(() => useCoordinatorRunModel('run-1'));
    expect(human.result.current.gates.assemblyReviewPending).toBe(true);
    human.unmount();

    streamState.events = [
      evt(2, 'coordinator.assembly_review_requested'),
    ];
    const legacy = renderHook(() => useCoordinatorRunModel('run-1'));
    expect(legacy.result.current.gates.assemblyReviewPending).toBe(true);
  });
});
