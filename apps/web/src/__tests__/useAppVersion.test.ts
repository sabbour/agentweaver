import { useAppVersion, useAppVersionInfo } from '../hooks/useAppVersion';
import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
afterEach(() => {
  vi.restoreAllMocks();
});

describe('useAppVersion', () => {
  it('returns empty string initially before fetch completes', () => {
    vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise(() => {}));
    const { result } = renderHook(() => useAppVersion());
    expect(result.current).toBe('');
  });

  it('returns the version string after a successful fetch for a real release build', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '0.6.0', gitSha: null, isRelease: true }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await waitFor(() => expect(result.current).toBe('0.6.0'));
  });

  it('returns version+gitSha for a SHA-tagged local/upgrade build', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '0.9.71', gitSha: 'a1c11f1', isRelease: false }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await waitFor(() => expect(result.current).toBe('0.9.71-dev+a1c11f1'));
  });

  it('does not double-append "-dev" when the base version already has a suffix', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '0.9.71-dev', gitSha: 'a1c11f1', isRelease: false }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await waitFor(() => expect(result.current).toBe('0.9.71-dev+a1c11f1'));
  });

  it('falls back to the plain version when no gitSha is present (e.g. local dotnet run)', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '0.9.70', gitSha: null, isRelease: false }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await waitFor(() => expect(result.current).toBe('0.9.70'));
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

  it('remains empty when the host reports no version', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: null, gitSha: null, isRelease: false }),
    } as Response);

    const { result } = renderHook(() => useAppVersion());
    await new Promise(r => setTimeout(r, 0));
    expect(result.current).toBe('');
  });
});

describe('useAppVersionInfo', () => {
  it('keeps the release commit even though the badge text hides it', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ version: '1.5.0', gitSha: 'abcdef0', isRelease: true }),
    } as Response);

    const { result } = renderHook(() => useAppVersionInfo());
    await waitFor(() => expect(result.current).toEqual({
      display: '1.5.0', version: '1.5.0', gitSha: 'abcdef0', isRelease: true,
    }));
  });

  it('asks for an uncached answer', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise(() => {}));
    renderHook(() => useAppVersionInfo());
    expect(fetchSpy).toHaveBeenCalledWith('/api/version', { cache: 'no-store' });
  });
});
