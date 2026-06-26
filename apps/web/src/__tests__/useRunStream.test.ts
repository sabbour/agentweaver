import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useRunStream } from '../api/sse';

describe('useRunStream — AbortController lifecycle', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  // RS-01: reconnect() must abort the in-flight fetch of the previous effect
  it('aborts the previous fetch when reconnect is called', async () => {
    // A fetch that never resolves — simulates a hanging SSE connection
    vi.spyOn(globalThis, 'fetch').mockReturnValue(new Promise(() => {}));

    const abortSpy = vi.spyOn(AbortController.prototype, 'abort');

    const { result } = renderHook(() =>
      useRunStream('run-1', 'http://localhost'),
    );

    // Trigger reconnect — React will run effect cleanup (controller.abort())
    // before starting the new effect.
    await act(async () => {
      result.current.reconnect();
    });

    // Exactly one abort: the cleanup of the first effect.
    // (The second effect's controller has not yet been cleaned up.)
    expect(abortSpy).toHaveBeenCalledTimes(1);
  });
});
