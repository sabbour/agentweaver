import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { apiClient } from '../api/apiClient';
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

vi.mock('../hooks/useAiExecutionContext', () => ({
  useAiExecutionContext: () => ({
    context: null,
    providerKey: 'signed-provider-key',
    available: true,
    loading: false,
    error: null,
    announcement: '',
    refresh: vi.fn(),
    handleInvocationError: vi.fn(() => false),
    applyCompletedContext: vi.fn(),
    applyProvider: vi.fn(),
    setPhase: vi.fn(),
  }),
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

  it('resolves a pending human-review after an in_place_steer steering decision', () => {
    streamState.events = [
      evt(1, 'coordinator.assembly_review_requested', { gateKind: 'human-review' }),
      evt(2, 'coordinator.steering_decision', { decision: 'in_place_steer', subtaskIds: ['subtask-1'] }),
    ];
    const { result } = renderHook(() => useCoordinatorRunModel('run-1'));
    expect(result.current.gates.assemblyReviewPending).toBe(false);
  });

  it('does NOT resolve a pending human-review for an advisory steering decision', () => {
    streamState.events = [
      evt(1, 'coordinator.assembly_review_requested', { gateKind: 'human-review' }),
      evt(2, 'coordinator.steering_decision', { decision: 'advisory' }),
    ];
    const { result } = renderHook(() => useCoordinatorRunModel('run-1'));
    expect(result.current.gates.assemblyReviewPending).toBe(true);
  });

  it('forwards the prepared provider key only for actions that continue AI execution', async () => {
    vi.mocked(apiClient.steerCoordinator).mockResolvedValue({ status: 'applied' });
    vi.mocked(apiClient.confirmOutcomeSpec).mockResolvedValue(null);
    vi.mocked(apiClient.reviseOutcomeSpec).mockResolvedValue(null);
    vi.mocked(apiClient.reviewAssembly).mockResolvedValue(undefined);
    const { result } = renderHook(() => useCoordinatorRunModel('run-1'));

    await act(async () => {
      await result.current.sendMessage('continue');
      await result.current.confirmOutcomeSpec();
      await result.current.reviseOutcomeSpec('revise');
      await result.current.reviewAssembly('approve');
      await result.current.stop();
      await result.current.reviewAssembly('decline');
    });

    expect(apiClient.steerCoordinator).toHaveBeenNthCalledWith(
      1,
      'run-1',
      { kind: 'send', instruction: 'continue' },
      'signed-provider-key',
    );
    expect(apiClient.confirmOutcomeSpec).toHaveBeenCalledWith(
      'run-1',
      false,
      'signed-provider-key',
    );
    expect(apiClient.reviseOutcomeSpec).toHaveBeenCalledWith(
      'run-1',
      'revise',
      'signed-provider-key',
    );
    expect(apiClient.reviewAssembly).toHaveBeenNthCalledWith(
      1,
      'run-1',
      'approve',
      undefined,
      'signed-provider-key',
    );
    expect(apiClient.steerCoordinator).toHaveBeenNthCalledWith(
      2,
      'run-1',
      { kind: 'stop' },
    );
    expect(apiClient.reviewAssembly).toHaveBeenNthCalledWith(
      2,
      'run-1',
      'decline',
      undefined,
      undefined,
    );
  });
});
