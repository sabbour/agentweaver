import { useRunPoll } from '../api/sse';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mockGetRun = vi.fn();

vi.mock('../api/client', () => ({
  AgentweaverApiClient: vi.fn().mockImplementation(() => ({
    getRun: mockGetRun,
  })),
}));

describe('useRunPoll', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('stops polling when a run reaches assemble_ready', async () => {
    vi.useFakeTimers();
    mockGetRun.mockResolvedValue({
      run_id: 'run-1',
      status: 'assemble_ready',
    });

    const { result } = renderHook(() => useRunPoll('run-1', 'http://localhost'));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(result.current.status).toBe('done');
    expect(mockGetRun).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });

    expect(mockGetRun).toHaveBeenCalledTimes(1);
  });
});
