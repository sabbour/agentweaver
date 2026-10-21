import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { SessionEventEnvelope } from './contracts';
import { gatewayClient } from './api';
import { mergeOrderedEvents, useRunJournal } from './useRunJournal';

afterEach(() => {
  vi.restoreAllMocks();
});

/** Builds a minimal journal event. Pass another run or project id to fake an out-of-scope event. */
function event(
  eventId: string,
  position: number,
  runId = 'r1',
  projectId = 'p1',
): SessionEventEnvelope {
  return {
    schemaVersion: 1,
    eventVersion: 1,
    eventId,
    identity: { projectId, runId, sessionId: 's1' },
    position,
    occurredAt: '2026-01-01T00:00:00Z',
    kind: 'turn',
    payload: {},
    objectReferences: [],
  };
}

/** A promise the test resolves by hand, to control when a response "arrives". */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}

describe('run journal event merging', () => {
  it('deduplicates replay/live overlap by event ID and keeps journal position order', () => {
    const first = event('event-1', 1);
    const second = event('event-2', 2);
    const third = event('event-3', 3);

    expect(mergeOrderedEvents([second, first], [third, event('event-2', 2)])).toEqual([
      first,
      second,
      third,
    ]);
  });

  it('uses event ID as a deterministic tie-breaker for equal positions', () => {
    expect(mergeOrderedEvents([], [event('event-b', 4), event('event-a', 4)]).map((item) => item.eventId))
      .toEqual(['event-a', 'event-b']);
  });

  it('replays every page before connecting live and merges replay/live overlap by cursor', async () => {
    const firstReplay = vi.spyOn(gatewayClient, 'replayEvents').mockResolvedValueOnce({
      events: [event('event-1', 1)],
      nextCursor: 'cursor-1',
      hasMore: true,
    }).mockResolvedValueOnce({
      events: [event('event-2', 2)],
      nextCursor: 'cursor-2',
      hasMore: false,
    });
    let connectedCursor: string | null = null;
    // The live stream repeats event-2 (already replayed) to prove it is not shown twice,
    // then stays open until the test unmounts the hook.
    const liveStream = vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      async function* (_token, _projectId, _runId, cursor, signal) {
        connectedCursor = cursor;
        yield { cursor: 'cursor-2', event: event('event-2', 2) };
        yield { cursor: 'cursor-3', event: event('event-3', 3) };
        await new Promise<void>((resolve) => {
          if (signal.aborted) resolve();
          else signal.addEventListener('abort', () => resolve(), { once: true });
        });
      },
    );
    const { result, unmount } = renderHook(() => useRunJournal('broker-token', 'p1', 'r1'));

    await waitFor(() => {
      expect(result.current.events.map((item) => item.eventId)).toEqual(['event-1', 'event-2', 'event-3']);
      expect(result.current.connected).toBe(true);
    });
    expect(firstReplay).toHaveBeenCalledTimes(2);
    // Live reading must start from the last replay cursor, so no events fall in a gap.
    expect(connectedCursor).toBe('cursor-2');
    expect(liveStream).toHaveBeenCalledOnce();
    expect(result.current.cursor).toBe('cursor-3');
    unmount();
  });

  it('ignores an in-flight replay response after the run binding changes', async () => {
    const oldReplay = deferred<{
      events: SessionEventEnvelope[];
      nextCursor: string;
      hasMore: boolean;
    }>();
    // Run r1's replay hangs until the test resolves it. Run r2's replay answers at once.
    vi.spyOn(gatewayClient, 'replayEvents').mockImplementation((_token, _projectId, runId) =>
      runId === 'r1'
        ? oldReplay.promise
        : Promise.resolve({
          events: [event('current-run', 1, 'r2')],
          nextCursor: 'r2-cursor',
          hasMore: false,
        }));
    vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      async function* (_token, _projectId, _runId, _cursor, signal) {
        yield* [];
        await new Promise<void>((resolve) => {
          if (signal.aborted) resolve();
          else signal.addEventListener('abort', () => resolve(), { once: true });
        });
      },
    );
    const { result, rerender, unmount } = renderHook(
      ({ runId, tenantSelector }) => useRunJournal('broker-token', 'p1', runId, tenantSelector),
      { initialProps: { runId: 'r1', tenantSelector: 'tenant-1' } },
    );

    await waitFor(() =>
      expect(gatewayClient.replayEvents).toHaveBeenCalledWith(
        'broker-token', 'p1', 'r1', null, 100, 'tenant-1',
      ));
    rerender({ runId: 'r2', tenantSelector: 'tenant-1' });
    await waitFor(() =>
      expect(result.current.events.map((item) => item.eventId)).toEqual(['current-run']));
    await waitFor(() =>
      expect(gatewayClient.streamEvents).toHaveBeenCalledWith(
        'broker-token', 'p1', 'r2', 'r2-cursor', expect.any(AbortSignal), 'tenant-1',
      ));

    await act(async () => {
      // Let the old r1 replay finish late. Its events must not leak into the r2 journal.
      oldReplay.resolve({
        events: [event('stale-run', 99, 'r1')],
        nextCursor: 'stale-cursor',
        hasMore: false,
      });
      await oldReplay.promise;
    });
    expect(result.current.events.map((item) => item.eventId)).toEqual(['current-run']);
    expect(result.current.cursor).toBe('r2-cursor');
    unmount();
  });

  it('rejects replay events outside the exact project and run scope', async () => {
    vi.spyOn(gatewayClient, 'replayEvents').mockResolvedValue({
      events: [event('foreign-replay', 1, 'r1', 'p2')],
      nextCursor: 'foreign-cursor',
      hasMore: false,
    });
    const liveStream = vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      async function* () {},
    );
    const { result, unmount } = renderHook(() => useRunJournal('broker-token', 'p1', 'r1'));

    await waitFor(() =>
      expect(result.current.error?.message).toContain('outside the exact project and run scope'));
    expect(result.current.events).toEqual([]);
    // A scope violation during replay is fatal, so the live stream is never opened.
    expect(liveStream).not.toHaveBeenCalled();
    unmount();
  });

  it('reconnects quietly from the cursor when the live stream closes cleanly', async () => {
    vi.spyOn(gatewayClient, 'replayEvents').mockResolvedValue({ events: [], nextCursor: 'cursor-0', hasMore: false });
    const cursors: Array<string | null> = [];
    // First connection sends one event and closes cleanly. The second stays open.
    vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      async function* (_token, _projectId, _runId, cursor, signal) {
        cursors.push(cursor);
        if (cursors.length === 1) {
          yield { cursor: 'cursor-1', event: event('event-1', 1) };
          return;
        }
        await new Promise<void>((resolve) => signal.addEventListener('abort', () => resolve(), { once: true }));
      },
    );
    const { result, unmount } = renderHook(() => useRunJournal('broker-token', 'p1', 'r1'));

    await waitFor(() => expect(cursors).toEqual(['cursor-0', 'cursor-1']));
    // A clean close is routine, so no error is shown to the user.
    expect(result.current.error).toBeNull();
    expect(result.current.events.map((item) => item.eventId)).toEqual(['event-1']);
    unmount();
  });

  it('reports a transient stream failure only after three in a row', async () => {
    vi.spyOn(gatewayClient, 'replayEvents').mockResolvedValue({ events: [], nextCursor: null, hasMore: false });
    let attempts = 0;
    const errors: Array<string | null> = [];
    vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      // eslint-disable-next-line require-yield
      async function* () {
        attempts += 1;
        throw new Error('network down');
      },
    );
    const { result, unmount } = renderHook(() => {
      const journal = useRunJournal('broker-token', 'p1', 'r1');
      // Record the error seen on every render, to prove it stayed null before the third failure.
      errors.push(journal.error?.message ?? null);
      return journal;
    });

    // Backoff waits add up (0.5s + 1s), so allow more than the default wait time.
    await waitFor(() => expect(result.current.error?.message).toBe('network down'), { timeout: 5000 });
    expect(attempts).toBe(3);
    expect(errors.slice(0, errors.indexOf('network down')).every((message) => message === null)).toBe(true);
    unmount();
  });

  it('rejects live events outside the exact project and run scope without reconnecting', async () => {
    vi.spyOn(gatewayClient, 'replayEvents').mockResolvedValue({
      events: [],
      nextCursor: null,
      hasMore: false,
    });
    const liveStream = vi.spyOn(gatewayClient, 'streamEvents').mockImplementation(
      async function* () {
        yield { cursor: 'foreign-cursor', event: event('foreign-live', 1, 'other-run') };
      },
    );
    const { result, unmount } = renderHook(() => useRunJournal('broker-token', 'p1', 'r1'));

    await waitFor(() =>
      expect(result.current.error?.message).toContain('outside the exact project and run scope'));
    expect(result.current.events).toEqual([]);
    // Called once: a scope violation is not retried like a network failure.
    expect(liveStream).toHaveBeenCalledOnce();
    expect(result.current.connected).toBe(false);
    unmount();
  });
});
