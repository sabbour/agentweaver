import { useCallback, useEffect, useState } from 'react';
import type { BoardDto } from './types';
import { apiClient } from './apiClient';
import { ApiError } from './client';

export type BoardStatus = 'loading' | 'ready' | 'error';

export interface UseBoardResult {
  board: BoardDto | null;
  status: BoardStatus;
  error: string | null;
  refetch: () => Promise<void>;
}

export interface UseBoardOptions {
  intervalMs?: number;
  includeTerminalHistory?: boolean;
}

// Board-level live updates (design section 8): poll GET /board on a fixed interval
// (default 3000ms) and expose refetch() for an immediate refresh after a mutation.
// No fakes — the board reflects fully materialized server state every few seconds.
const DEFAULT_INTERVAL_MS = 3000;

export function useBoard(projectId: string, options?: UseBoardOptions): UseBoardResult {
  const intervalMs = options?.intervalMs ?? DEFAULT_INTERVAL_MS;
  const includeTerminalHistory = options?.includeTerminalHistory ?? false;

  const [board, setBoard] = useState<BoardDto | null>(null);
  const [status, setStatus] = useState<BoardStatus>('loading');
  const [error, setError] = useState<string | null>(null);

  const refetch = useCallback(async () => {
    if (!projectId) return;
    try {
      const next = await apiClient.getBoard(projectId, includeTerminalHistory);
      setBoard(next);
      setStatus('ready');
      setError(null);
    } catch (err) {
      setStatus('error');
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error ? err.message : String(err),
      );
    }
  }, [projectId, includeTerminalHistory]);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    const tick = () => { if (!cancelled) void refetch(); };
    tick();
    const iv = setInterval(tick, intervalMs);
    return () => { cancelled = true; clearInterval(iv); };
  }, [projectId, intervalMs, refetch]);

  return { board, status, error, refetch };
}
