import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useRefreshCountdown } from '../hooks/useRefreshCountdown';

const NOW = new Date('2026-06-22T12:00:00.000Z').getTime();

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});

afterEach(() => {
  vi.useRealTimers();
});

describe('useRefreshCountdown', () => {
  it('holds at the full interval before the first refresh', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 10000, lastRefreshedAt: null }),
    );
    expect(result.current.secondsRemaining).toBe(10);
    expect(result.current.label).toBe('Next refresh in 10s');
  });

  it('counts down from the last refresh time', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 10000, lastRefreshedAt: NOW }),
    );
    expect(result.current.secondsRemaining).toBe(10);

    act(() => {
      vi.advanceTimersByTime(3000);
    });
    expect(result.current.secondsRemaining).toBe(7);
    expect(result.current.label).toBe('Next refresh in 7s');
  });

  it('clamps at zero once the interval has elapsed', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 10000, lastRefreshedAt: NOW - 60000 }),
    );
    expect(result.current.secondsRemaining).toBe(0);
    expect(result.current.label).toBe('Next refresh in 0s');
  });

  it('accepts a Date for the last refresh time', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 5000, lastRefreshedAt: new Date(NOW) }),
    );
    expect(result.current.secondsRemaining).toBe(5);
  });

  it('shows the refreshing label while a fetch is in flight', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 10000, lastRefreshedAt: NOW, refreshing: true }),
    );
    expect(result.current.label).toBe('Refreshing\u2026');
  });

  it('shows the paused label when auto-refresh is off', () => {
    const { result } = renderHook(() =>
      useRefreshCountdown({ intervalMs: 10000, lastRefreshedAt: NOW, paused: true }),
    );
    expect(result.current.label).toBe('Auto-refresh off');
  });
});
