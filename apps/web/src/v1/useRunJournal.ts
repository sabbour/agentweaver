import { useEffect, useState } from 'react';
import { gatewayClient, GatewayError } from './api';
import type { SessionEventEnvelope } from './contracts';

/** Signals that a live or replayed event violates this journal's selected run scope. */
class JournalScopeError extends Error {}

/** Combines replay and live events by event ID, then orders them by owner position. */
export function mergeOrderedEvents(
  current: SessionEventEnvelope[],
  incoming: SessionEventEnvelope[],
): SessionEventEnvelope[] {
  const byId = new Map<string, SessionEventEnvelope>();
  // Events seen in both lists keep one copy. The incoming copy wins because it is newer.
  for (const event of current) byId.set(event.eventId, event);
  for (const event of incoming) byId.set(event.eventId, event);
  // Sort by owner position. The event id breaks ties so the order is always stable.
  return [...byId.values()].sort((left, right) =>
    left.position - right.position || left.eventId.localeCompare(right.eventId));
}

/** Local connection, replay, and cursor state for one exact journal scope. */
interface JournalState {
  events: SessionEventEnvelope[];
  cursor: string | null;
  loading: boolean;
  error: Error | null;
  connected: boolean;
  scopeId: string;
}

const initialState: Omit<JournalState, 'scopeId'> = {
  events: [],
  cursor: null,
  loading: true,
  error: null,
  connected: false,
};

/**
 * Replays a run's persisted owner events, then follows its live event stream.
 * Replay is cursor-bounded, live events are deduplicated by ID, and any event
 * outside the exact project/run scope stops the stream instead of being shown.
 * A cleanly closed stream reconnects quietly with backoff; `error` reports only
 * fatal responses or three transient failures in a row.
 */
export function useRunJournal(
  token: string | undefined,
  projectId: string,
  runId: string,
  tenantSelector?: string,
): Omit<JournalState, 'scopeId'> {
  // The scope id names one exact journal: token, project, run, and tenant.
  // State saved under a different scope id is never shown.
  const scopeId = JSON.stringify([token ?? null, projectId, runId, tenantSelector ?? null]);
  const [state, setState] = useState<JournalState>({ ...initialState, scopeId: '' });

  useEffect(() => {
    // Two stop signals: `active` for React cleanup and an AbortController that
    // also cancels the open network stream.
    let active = true;
    const abortController = new AbortController();
    const { signal } = abortController;
    const isActive = () => active && !signal.aborted;
    /**
     * Applies a state change only while this effect is still running. If stored
     * state is from an older scope, the change starts from a clean state instead,
     * so events from another run are never mixed in.
     */
    const updateState = (update: (current: JournalState) => JournalState) => {
      if (!isActive()) return;
      setState((current) => {
        // Check again: the effect may have stopped between scheduling and running the updater.
        if (!isActive()) return current;
        const scoped = current.scopeId === scopeId
          ? current
          : { ...initialState, loading: Boolean(token), scopeId };
        return { ...update(scoped), scopeId };
      });
    };
    // Signed out: nothing to read. Still return cleanup so the contract stays the same.
    if (!token) {
      return () => {
        active = false;
        abortController.abort();
      };
    }

    /** Adds new events after checking that every one belongs to this project and run. */
    const addEvents = (incoming: SessionEventEnvelope[]) => {
      if (!incoming.length) return;
      // One wrong event stops the whole journal. Showing another run's events
      // would leak data, so this is treated as fatal, not skipped.
      if (incoming.some((event) =>
        event.identity?.projectId !== projectId || event.identity?.runId !== runId)) {
        throw new JournalScopeError('The server returned a journal event outside the exact project and run scope.');
      }
      updateState((current) => ({
        ...current,
        events: mergeOrderedEvents(current.events, incoming),
      }));
    };

    /**
     * Main loop. First it replays stored history page by page, then it follows the
     * live stream from the last cursor. When the stream drops, it reconnects from
     * that cursor, so no events are missed or replayed twice.
     */
    const connect = async () => {
      // `cursor` is the last position read. Replay and live reads both continue from it.
      let cursor: string | null = null;
      let replayed = false;
      let reconnectDelay = 500;
      let transientFailures = 0;
      try {
        while (!signal.aborted) {
          // History is replayed once. Later reconnects only resume the live stream.
          if (!replayed) {
            let pageCount = 0;
            while (!signal.aborted) {
              const page = await gatewayClient.replayEvents(
                token, projectId, runId, cursor, 100, tenantSelector,
              );
              if (!isActive()) return;
              addEvents(page.events);
              // Guard against a server bug that would loop forever on the same page.
              if (page.hasMore && (!page.nextCursor || page.nextCursor === cursor))
                throw new Error('The server returned hasMore without advancing its journal cursor.');
              if (page.nextCursor) cursor = page.nextCursor;
              updateState((current) => ({ ...current, cursor }));
              if (!page.hasMore) break;
              // Hard cap on history pages so a runaway server cannot keep the page busy forever.
              if (++pageCount > 1000)
                throw new Error('The server exceeded the bounded initial history limit.');
            }
            replayed = true;
            updateState((current) => ({ ...current, loading: false }));
          }

          // Step 2: follow the live stream from the cursor until it ends or fails.
          try {
            updateState((current) => ({ ...current, connected: true, error: null }));
            for await (const frame of gatewayClient.streamEvents(
              token, projectId, runId, cursor, signal, tenantSelector,
            )) {
              if (!isActive()) return;
              addEvents([frame.event]);
              // Save the cursor after each event so a reconnect resumes right after it.
              cursor = frame.cursor;
              updateState((current) => ({ ...current, cursor }));
              // A working stream resets the backoff and the failure count.
              reconnectDelay = 500;
              transientFailures = 0;
            }
            if (signal.aborted) return;
            // A stream that closes cleanly (for example, a proxy idle timeout) is routine:
            // reconnect from the cursor with backoff and keep the status strip as the only signal.
            transientFailures = 0;
            updateState((current) => ({ ...current, connected: false }));
            await new Promise((resolve) => window.setTimeout(resolve, reconnectDelay));
            // Double the wait each time, up to 10 seconds.
            reconnectDelay = Math.min(reconnectDelay * 2, 10_000);
          } catch (error) {
            // Aborted by cleanup: stop quietly, this is not a failure.
            if (signal.aborted) return;
            updateState((current) => ({ ...current, connected: false }));
            // Most 4xx errors (for example, no access or not found) will not fix
            // themselves, so stop and show the error. 408 (timeout) and 429 (rate
            // limit) are temporary, so they fall through and retry.
            if (error instanceof GatewayError && error.status >= 400 &&
                error.status < 500 && error.status !== 408 && error.status !== 429) {
              updateState((current) => ({ ...current, error }));
              return;
            }
            // A scope violation is never retried.
            if (error instanceof JournalScopeError) {
              updateState((current) => ({ ...current, error }));
              return;
            }
            updateState((current) => ({
              ...current,
              // One dropped connection is routine; report only a run of failures in a row.
              error: ++transientFailures < 3 ? current.error
                : error instanceof Error ? error : new Error('The run event stream disconnected.'),
            }));
            await new Promise((resolve) => window.setTimeout(resolve, reconnectDelay));
            reconnectDelay = Math.min(reconnectDelay * 2, 10_000);
          }
        }
      } catch (error) {
        // Errors thrown during replay land here. Replay has no retry, so the
        // journal stops loading and shows the error.
        if (!isActive()) return;
        updateState((current) => ({
          ...current,
          loading: false,
          connected: false,
          error: error instanceof Error ? error : new Error('Unable to read the run journal.'),
        }));
      }
    };

    void connect();
    return () => {
      active = false;
      abortController.abort();
    };
  }, [projectId, runId, scopeId, tenantSelector, token]);

  // On the first render after the scope changes, the effect has not reset state yet.
  // Show a clean loading state instead of the previous run's events.
  const visibleState = state.scopeId === scopeId
    ? state
    : { ...initialState, scopeId, loading: Boolean(token) };
  return {
    events: visibleState.events,
    cursor: visibleState.cursor,
    loading: visibleState.loading,
    error: visibleState.error,
    connected: visibleState.connected,
  };
}
