import { describe, it, expect, vi, afterEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
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

  it('does not mark the stream done on coordinator.assembly_blocked without a done frame', async () => {
    const encoder = new TextEncoder();
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode(
          'id: 1\nevent: coordinator.assembly_blocked\ndata: {"reason":"integration_conflict"}\n\n',
        ));
      },
    });
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(stream, { status: 200 }));

    const { result, unmount } = renderHook(() =>
      useRunStream('run-1', 'http://localhost'),
    );

    await waitFor(() =>
      expect(result.current.events.some((evt) => evt.type === 'coordinator.assembly_blocked')).toBe(true),
    );
    expect(result.current.status).toBe('streaming');

    unmount();
  });
});
