import { useRunPoll } from '../api/sse';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mockGetRun = vi.fn();

vi.mock('../api/client', () => ({
  // vitest 4 invokes the mock implementation with `new` when the real export
  // is constructed via `new AgentweaverApiClient(...)` -- an arrow function
  // is never constructible in JS, so this must be a regular `function`
  // (returning the mock instance overrides the `new`-created `this`).
  AgentweaverApiClient: vi.fn().mockImplementation(function AgentweaverApiClientMock() {
    return { getRun: mockGetRun };
  }),
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
