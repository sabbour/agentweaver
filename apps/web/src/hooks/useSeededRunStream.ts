import { apiClient } from '../api/apiClient';
import { useRunStream } from '../api/sse';
import { mergeRunEvents } from '../timeline/mergeRunEvents';
import { useEffect, useMemo, useState } from 'react';
import type { EventType, RunStreamEvent, StreamStatus } from '../api/sse';
export interface SeededRunStream {
  /** Persisted seed folded under the live SSE deltas — feed this to useTimelineItems. */
  events: RunStreamEvent[];
  /** The raw live SSE deltas (pre-merge). */
  liveEvents: RunStreamEvent[];
  /** The REST-seeded persisted history (empty until a parked/terminal run is seeded). */
  seedEvents: RunStreamEvent[];
  status: StreamStatus;
  error: string | null;
  /** Failure loading the durable event history; live SSE events remain available. */
  seedError: string | null;
  /** Count of events evicted from the live buffer — forward to useTimelineItems. */
  droppedEventCount: number;
  reconnect: () => void;
}

/**
 * useRunStream + persisted-history seeding, extracted so every consumer binds to
 * a run's durable event log plus live deltas consistently.
 *
 * useRunStream alone only resumes the LIVE SSE stream via Last-Event-ID; it never
 * calls getRunEvents, so a parked/terminal run (closed stream) would render empty.
 * The run detail surfaces and the agent session panel each hand-rolled this seed+merge;
 * the browser console TUI needs the exact same behaviour, so it lives here once.
 *
 * @param runId the run to bind to ('' disables the stream).
 */
export function useSeededRunStream(runId: string): SeededRunStream {
  const {
    events: liveEvents,
    droppedEventCount,
    status: streamStatus,
    error,
    reconnect,
  } = useRunStream(runId);

  const [seedEvents, setSeedEvents] = useState<RunStreamEvent[]>([]);
  const [seedError, setSeedError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    if (!runId) {
      queueMicrotask(() => {
        if (!cancelled) {
          setSeedEvents([]);
          setSeedError(null);
        }
      });
      return () => { cancelled = true; };
    }
    queueMicrotask(() => {
      if (!cancelled) setSeedError(null);
    });
    apiClient.getRunEvents(runId)
      .then((persisted) => {
        if (cancelled) return;
        setSeedEvents(persisted.map((e) => ({
          sequence: e.sequence,
          type: e.type as EventType,
          payload: e.payload,
        })));
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setSeedError(err instanceof Error ? err.message : 'The saved event history could not be loaded.');
        }
      });
    return () => { cancelled = true; };
  }, [runId]);

  const events = useMemo<RunStreamEvent[]>(
    () => mergeRunEvents(seedEvents, liveEvents),
    [seedEvents, liveEvents],
  );

  return { events, liveEvents, seedEvents, status: streamStatus, error, seedError, droppedEventCount, reconnect };
}
