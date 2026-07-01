import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useAppVersion } from '../hooks/useAppVersion';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useAppVersion', () => {
  it('returns empty string initially before fetch completes', () => {
    vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise(() => {}));
    const { result } = renderHook(() => useAppVersion());
    expect(result.current).toBe('');
  });

  it('returns the version string after a successful fetch', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '0.6.0' }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await waitFor(() => expect(result.current).toBe('0.6.0'));
  });

  it('remains empty when the fetch response is not ok', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: false,
      json: () => Promise.resolve(null),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    // Give it a tick to settle
    await new Promise(r => setTimeout(r, 0));
    expect(result.current).toBe('');
  });

  it('remains empty when fetch throws', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('network error'));

    const { result } = renderHook(() => useAppVersion());
    await new Promise(r => setTimeout(r, 0));
    expect(result.current).toBe('');
  });
});
