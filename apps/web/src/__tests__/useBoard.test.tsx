import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { useBoard } from '../api/board';
import { makeBoard } from './fixtures/board';

vi.mock('../api/apiClient', () => ({
  apiClient: { getBoard: vi.fn() },
}));

import { apiClient } from '../api/apiClient';

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard());
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useBoard — board-level polling + refetch (FR-017/022)', () => {
  it('fetches the board on mount and exposes it', async () => {
    const { result } = renderHook(() => useBoard('proj-1', { intervalMs: 100000 }));
    await waitFor(() => expect(result.current.status).toBe('ready'));
    expect(result.current.board?.project_id).toBe('proj-1');
    expect(vi.mocked(apiClient.getBoard)).toHaveBeenCalledWith('proj-1', false);
  });

  it('polls on the injected interval', async () => {
    vi.useFakeTimers();
    try {
      renderHook(() => useBoard('proj-1', { intervalMs: 1000 }));
      // initial fetch
      await vi.advanceTimersByTimeAsync(0);
      expect(vi.mocked(apiClient.getBoard)).toHaveBeenCalledTimes(1);
      // one interval -> one more fetch
      await vi.advanceTimersByTimeAsync(1000);
      expect(vi.mocked(apiClient.getBoard)).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(1000);
      expect(vi.mocked(apiClient.getBoard)).toHaveBeenCalledTimes(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('refetch() re-reads the board immediately (post-mutation refresh)', async () => {
    const { result } = renderHook(() => useBoard('proj-1', { intervalMs: 100000 }));
    await waitFor(() => expect(result.current.status).toBe('ready'));
    const before = vi.mocked(apiClient.getBoard).mock.calls.length;

    await act(async () => { await result.current.refetch(); });

    expect(vi.mocked(apiClient.getBoard).mock.calls.length).toBe(before + 1);
  });
});
