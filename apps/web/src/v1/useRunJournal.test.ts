import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { SessionEventEnvelope } from './contracts';
import { gatewayClient } from './api';
import { mergeOrderedEvents, useRunJournal } from './useRunJournal';

afterEach(() => {
  vi.restoreAllMocks();
});

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
      ({ runId }) => useRunJournal('broker-token', 'p1', runId),
      { initialProps: { runId: 'r1' } },
    );

    await waitFor(() =>
      expect(gatewayClient.replayEvents).toHaveBeenCalledWith('broker-token', 'p1', 'r1', null, 100));
    rerender({ runId: 'r2' });
    await waitFor(() =>
      expect(result.current.events.map((item) => item.eventId)).toEqual(['current-run']));

    await act(async () => {
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
    expect(liveStream).not.toHaveBeenCalled();
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
    expect(liveStream).toHaveBeenCalledOnce();
    expect(result.current.connected).toBe(false);
    unmount();
  });
});
