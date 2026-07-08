import { useEffect, useMemo, useState } from 'react';
import { useRunStream, type RunStreamEvent, type EventType, type StreamStatus } from '../api/sse';
import { apiClient } from '../api/apiClient';
import { mergeRunEvents, SEED_STATUSES } from '../timeline/mergeRunEvents';

export interface SeededRunStream {
  /** Persisted seed folded under the live SSE deltas — feed this to useTimelineItems. */
  events: RunStreamEvent[];
  /** The raw live SSE deltas (pre-merge). */
  liveEvents: RunStreamEvent[];
  /** The REST-seeded persisted history (empty until a parked/terminal run is seeded). */
  seedEvents: RunStreamEvent[];
  status: StreamStatus;
  error: string | null;
  /** Count of events evicted from the live buffer — forward to useTimelineItems. */
  droppedEventCount: number;
  reconnect: () => void;
}

/**
 * useRunStream + persisted-history seeding, extracted so every consumer binds to
 * an already-running OR parked/completed run correctly (BLOCKING #3).
 *
 * useRunStream alone only resumes the LIVE SSE stream via Last-Event-ID; it never
 * calls getRunEvents, so a parked/terminal run (closed stream) would render empty.
 * The run detail surfaces and the agent session panel each hand-rolled this seed+merge;
 * the browser console TUI needs the exact same behaviour, so it lives here once.
 *
 * @param runId the run to bind to ('' disables the stream).
 * @param status the run's lifecycle status; when it is terminal/parked
 *   (SEED_STATUSES) the persisted events endpoint is fetched and folded under the
 *   live deltas. Pass undefined for an unknown/active run (seed skipped).
 */
export function useSeededRunStream(runId: string, status?: string): SeededRunStream {
  const {
    events: liveEvents,
    droppedEventCount,
    status: streamStatus,
    error,
    reconnect,
  } = useRunStream(runId);

  const [seedEvents, setSeedEvents] = useState<RunStreamEvent[]>([]);

  useEffect(() => {
    if (!runId) { setSeedEvents([]); return; } // eslint-disable-line react-hooks/set-state-in-effect
    if (!status || !SEED_STATUSES.has(status)) { setSeedEvents([]); return; }
    let cancelled = false;
    apiClient.getRunEvents(runId)
      .then((persisted) => {
        if (cancelled) return;
        setSeedEvents(persisted.map((e) => ({
          sequence: e.sequence,
          type: e.type as EventType,
          payload: e.payload,
        })));
      })
      .catch(() => { /* durable log may 404 — fall back to the live stream */ });
    return () => { cancelled = true; };
  }, [runId, status]);

  const events = useMemo<RunStreamEvent[]>(
    () => mergeRunEvents(seedEvents, liveEvents),
    [seedEvents, liveEvents],
  );

  return { events, liveEvents, seedEvents, status: streamStatus, error, droppedEventCount, reconnect };
}
