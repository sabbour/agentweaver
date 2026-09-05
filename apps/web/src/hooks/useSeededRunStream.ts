import { apiClient } from '../api/apiClient';
import { useRunStream } from '../api/sse';
import { mergeRunEvents } from '../timeline/mergeRunEvents';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { EventType, RunStreamEvent, StreamStatus } from '../api/sse';
export interface SeededRunStream {
  /** Persisted seed folded under the live SSE deltas — feed this to useTimelineItems. */
  events: RunStreamEvent[];
  /** The raw live SSE deltas (pre-merge). */
  liveEvents: RunStreamEvent[];
  /** The REST-seeded persisted history (empty until a parked/terminal run is seeded). */
  seedEvents: RunStreamEvent[];
  /** The first durable snapshot requested for this run, used as a stable reconciliation baseline. */
  baselineEvents: RunStreamEvent[];
  /** Whether the first durable snapshot for the current run has loaded successfully. */
  baselineReady: boolean;
  status: StreamStatus;
  error: string | null;
  /** Failure loading the durable event history; live SSE events remain available. */
  seedError: string | null;
  /** Reload the durable event history without recreating the live SSE connection. */
  refresh: () => Promise<RunStreamEvent[]>;
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
  const [seedRunId, setSeedRunId] = useState('');
  const [baselineEvents, setBaselineEvents] = useState<RunStreamEvent[]>([]);
  const [baselineRunId, setBaselineRunId] = useState<string | null>(null);
  const [seedError, setSeedError] = useState<string | null>(null);
  const baselineRunIdRef = useRef<string | null>(null);
  const refreshGenerationRef = useRef(0);
  const runGenerationRef = useRef(0);

  useEffect(() => {
    refreshGenerationRef.current += 1;
    runGenerationRef.current += 1;
    baselineRunIdRef.current = null;
  }, [runId]);

  const loadPersistedEvents = useCallback(async (): Promise<RunStreamEvent[]> => {
    const refreshGeneration = ++refreshGenerationRef.current;
    const runGeneration = runGenerationRef.current;
    const isCurrentRefresh = () => refreshGeneration === refreshGenerationRef.current;
    const isCurrentRun = () => runGeneration === runGenerationRef.current;
    if (isCurrentRefresh()) setSeedError(null);
    if (!runId) {
      if (isCurrentRefresh()) {
        setSeedEvents([]);
        setSeedRunId('');
      }
      if (isCurrentRun() && baselineRunIdRef.current !== '') {
        baselineRunIdRef.current = '';
        setBaselineEvents([]);
        setBaselineRunId('');
      }
      return [];
    }
    try {
      const persisted = await apiClient.getRunEvents(runId);
      const refreshed = persisted.map((e) => ({
        sequence: e.sequence,
        type: e.type as EventType,
        payload: e.payload,
      }));
      if (isCurrentRun() && baselineRunIdRef.current !== runId) {
        baselineRunIdRef.current = runId;
        setBaselineEvents(refreshed);
        setBaselineRunId(runId);
      }
      if (isCurrentRefresh()) {
        setSeedEvents(refreshed);
        setSeedRunId(runId);
        setSeedError(null);
      }
      return refreshed;
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : 'The saved event history could not be loaded.';
      if (isCurrentRefresh()) setSeedError(message);
      throw err;
    }
  }, [runId]);

  const refresh = useCallback(
    () => loadPersistedEvents(),
    [loadPersistedEvents],
  );

  useEffect(() => {
    void loadPersistedEvents().catch(() => {});
  }, [loadPersistedEvents]);

  const currentSeedEvents = useMemo(
    () => (seedRunId === runId ? seedEvents : []),
    [runId, seedEvents, seedRunId],
  );

  const events = useMemo<RunStreamEvent[]>(
    () => mergeRunEvents(currentSeedEvents, liveEvents),
    [currentSeedEvents, liveEvents],
  );

  return {
    events,
    liveEvents,
    seedEvents: currentSeedEvents,
    baselineEvents: baselineRunId === runId ? baselineEvents : [],
    baselineReady: baselineRunId === runId,
    status: streamStatus,
    error,
    seedError,
    droppedEventCount,
    reconnect,
    refresh,
  };
}
