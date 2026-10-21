import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { gatewayClient, GatewayError } from './api';
import type { useAuth } from './AuthContext';
import type { SessionEventEnvelope } from './contracts';

/** Who wrote a turn. Other `role` values are not chat turns and are skipped. */
export type TranscriptRole = 'user' | 'assistant';

/**
 * Load state of one turn's text. `notFound` means the owner has no content for the
 * event (404); `unavailable` means the caller can't read it or the owner answered for
 * a different event; `failed` is any other error and can be retried.
 */
export type TranscriptTurnState = 'loading' | 'loaded' | 'notFound' | 'unavailable' | 'failed';

/** One user or assistant turn of a run, in journal order. `text` is set only when `loaded`. */
export interface TranscriptTurn {
  eventId: string;
  sessionId: string;
  role: TranscriptRole;
  position: number;
  occurredAt: string;
  state: TranscriptTurnState;
  text: string | null;
}

/** Chat history rebuilt from committed `turn` events and their owner-read text. */
export interface TurnTranscript {
  turns: TranscriptTurn[];
  /** True after a 403: every text is cleared and no further reads run until `retry`. */
  accessLost: boolean;
  /** Reads failed turns again, or all turns after access was lost. */
  retry: () => void;
}

type ApiCall = ReturnType<typeof useAuth>['apiCall'];
/** Load result for one turn, keyed by eventId in the store. */
type Entry = { state: TranscriptTurnState; text: string | null };

/** Reads at most this many turns at once, so a long replay doesn't flood the Gateway. */
export const TURN_READ_CONCURRENCY = 4;

/** Returns the user and assistant turn events for exactly this project and run. */
export function turnEventsOf(events: SessionEventEnvelope[], projectId: string, runId: string): SessionEventEnvelope[] {
  return events.filter((event) => event.kind === 'turn'
    && event.identity.projectId === projectId
    && event.identity.runId === runId
    && (event.payload.role === 'user' || event.payload.role === 'assistant'));
}

/**
 * Reads the text of each replayed or live `turn` event with `readSessionTurnContent`
 * and the token bound to this run, so the chat history survives reload and reconnect.
 * Each event is read once per mount. A response for another event or identity is
 * treated as unavailable. A 403 means project read access was lost: all text is
 * dropped from memory and nothing else is read until `retry`.
 */
export function useTurnTranscript(
  events: SessionEventEnvelope[],
  projectId: string,
  runId: string,
  apiCall: ApiCall,
): TurnTranscript {
  // `scope` names the run the data belongs to. Loaded text is stored together
  // with its scope, so text from one run is never shown for another.
  const scope = `${projectId}/${runId}`;
  const turnEvents = useMemo(() => turnEventsOf(events, projectId, runId), [events, projectId, runId]);
  const [store, setStore] = useState<{ scope: string; entries: Record<string, Entry>; accessLost: boolean }>(
    { scope, entries: {}, accessLost: false },
  );
  // Bumped by retry(). Reads started for an older generation are ignored.
  const [generation, setGeneration] = useState(0);
  // Event ids already requested in this scope and generation, so each turn is read only once.
  const requested = useRef<{ scope: string; generation: number; ids: Set<string> }>({ scope, generation, ids: new Set() });
  // If the stored data belongs to another run, treat it as empty right away (before any effect runs).
  const current = store.scope === scope ? store : { scope, entries: {}, accessLost: false };

  // Reads outlive effect re-runs (new live events re-run it); results are dropped only
  // after unmount or when the scope or generation they were read for is gone.
  const live = useRef({ mounted: true, scope, generation });
  useEffect(() => {
    live.current = { mounted: true, scope, generation };
    return () => { live.current = { ...live.current, mounted: false }; };
  }, [scope, generation]);

  useEffect(() => {
    // A new run or a retry starts a fresh "already requested" set.
    if (requested.current.scope !== scope || requested.current.generation !== generation) {
      requested.current = { scope, generation, ids: new Set() };
    }
    const claimed = requested.current;
    // Read only turns that are not requested and not loaded yet. After access
    // is lost, read nothing until retry.
    const queue = current.accessLost ? [] : turnEvents.filter((event) =>
      !claimed.ids.has(event.eventId) && !current.entries[event.eventId]);
    if (queue.length === 0) return;
    // Claim the ids now, so the next effect run (from a new live event) does not read them again.
    queue.forEach((event) => claimed.ids.add(event.eventId));
    // True while this batch's results still apply: mounted, same run, same generation.
    const valid = () => live.current.mounted && live.current.scope === scope && live.current.generation === generation;
    // Stores one turn's result. Uses the functional form so parallel reads do not overwrite each other.
    const update = (eventId: string, entry: Entry) => setStore((value) => {
      const base = value.scope === scope ? value : { scope, entries: {}, accessLost: false };
      // After a 403 no text may come back into memory, even from reads still in flight.
      if (base.accessLost) return base;
      return { ...base, entries: { ...base.entries, [eventId]: entry } };
    });
    // Reads one turn's text with the run-bound token and records the outcome.
    const read = async (event: SessionEventEnvelope) => {
      try {
        const content = await apiCall((token, tenant) => gatewayClient.readSessionTurnContent(
          token, projectId, runId, event.identity.sessionId, event.eventId, tenant,
        ), { projectId, runId });
        // The response must be for exactly the event, project, run and session asked for.
        // Anything else is shown as unavailable, never as text.
        const exact = content.eventId === event.eventId
          && content.identity.projectId === projectId
          && content.identity.runId === runId
          && content.identity.sessionId === event.identity.sessionId;
        if (valid()) update(event.eventId, exact ? { state: 'loaded', text: content.text } : { state: 'unavailable', text: null });
      } catch (reason) {
        if (!valid()) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          // Access lost: drop all text for this run at once and stop further reads.
          setStore({ scope, entries: {}, accessLost: true });
        } else if (reason instanceof GatewayError && reason.status === 404) {
          // The owner has no content for this event. This is final; retry does not reread it.
          update(event.eventId, { state: 'notFound', text: null });
        } else {
          // Network or server error. Retry will read it again.
          update(event.eventId, { state: 'failed', text: null });
        }
      }
    };
    // A small worker pool: each worker takes the next turn from the shared queue
    // until it is empty or the batch is no longer valid.
    const pending = [...queue];
    const worker = async () => {
      while (valid() && pending.length > 0) await read(pending.shift()!);
    };
    void Promise.all(Array.from({ length: Math.min(TURN_READ_CONCURRENCY, pending.length) }, worker));
    // `current` is derived from `store`; reading it here must not re-run the reads.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiCall, generation, projectId, runId, scope, turnEvents, current.accessLost]);
  // Retry: after access loss, clear everything and read all turns again.
  // Otherwise keep loaded, not-found and unavailable turns, and drop only
  // failed ones so they are read again. The new generation resets the claimed ids.
  const retry = useCallback(() => {
    setStore((value) => value.accessLost
      ? { scope: value.scope, entries: {}, accessLost: false }
      : { ...value, entries: Object.fromEntries(Object.entries(value.entries).filter(([, entry]) => entry.state !== 'failed')) });
    setGeneration((value) => value + 1);
  }, []);

  // Build the visible list in journal order. Turns with no result yet show as loading.
  // Text is passed through only for loaded turns.
  const turns = current.accessLost ? [] : turnEvents.map((event): TranscriptTurn => {
    const entry = current.entries[event.eventId] ?? { state: 'loading' as const, text: null };
    return {
      eventId: event.eventId,
      sessionId: event.identity.sessionId,
      role: event.payload.role as TranscriptRole,
      position: event.position,
      occurredAt: event.occurredAt,
      state: entry.state,
      text: entry.state === 'loaded' ? entry.text : null,
    };
  });
  return { turns, accessLost: current.accessLost, retry };
}
