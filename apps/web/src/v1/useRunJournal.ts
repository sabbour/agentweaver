import { useEffect, useState } from 'react';
import { gatewayClient, GatewayError } from './api';
import type { SessionEventEnvelope } from './contracts';

class JournalScopeError extends Error {}

export function mergeOrderedEvents(
  current: SessionEventEnvelope[],
  incoming: SessionEventEnvelope[],
): SessionEventEnvelope[] {
  const byId = new Map<string, SessionEventEnvelope>();
  for (const event of current) byId.set(event.eventId, event);
  for (const event of incoming) byId.set(event.eventId, event);
  return [...byId.values()].sort((left, right) =>
    left.position - right.position || left.eventId.localeCompare(right.eventId));
}

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

export function useRunJournal(
  token: string | undefined,
  projectId: string,
  runId: string,
): Omit<JournalState, 'scopeId'> {
  const scopeId = JSON.stringify([token ?? null, projectId, runId]);
  const [state, setState] = useState<JournalState>({ ...initialState, scopeId: '' });

  useEffect(() => {
    let active = true;
    const abortController = new AbortController();
    const { signal } = abortController;
    const isActive = () => active && !signal.aborted;
    const updateState = (update: (current: JournalState) => JournalState) => {
      if (!isActive()) return;
      setState((current) => {
        if (!isActive()) return current;
        const scoped = current.scopeId === scopeId
          ? current
          : { ...initialState, loading: Boolean(token), scopeId };
        return { ...update(scoped), scopeId };
      });
    };
    if (!token) {
      return () => {
        active = false;
        abortController.abort();
      };
    }

    const addEvents = (incoming: SessionEventEnvelope[]) => {
      if (!incoming.length) return;
      if (incoming.some((event) =>
        event.identity?.projectId !== projectId || event.identity?.runId !== runId)) {
        throw new JournalScopeError('The Gateway returned a journal event outside the exact project and run scope.');
      }
      updateState((current) => ({
        ...current,
        events: mergeOrderedEvents(current.events, incoming),
      }));
    };

    const connect = async () => {
      let cursor: string | null = null;
      let replayed = false;
      let reconnectDelay = 500;
      try {
        while (!signal.aborted) {
          if (!replayed) {
            let pageCount = 0;
            while (!signal.aborted) {
              const page = await gatewayClient.replayEvents(token, projectId, runId, cursor, 100);
              if (!isActive()) return;
              addEvents(page.events);
              if (page.hasMore && (!page.nextCursor || page.nextCursor === cursor))
                throw new Error('The Gateway replay returned hasMore without advancing its journal cursor.');
              if (page.nextCursor) cursor = page.nextCursor;
              updateState((current) => ({ ...current, cursor }));
              if (!page.hasMore) break;
              if (++pageCount > 1000)
                throw new Error('The Gateway replay exceeded the bounded initial history limit.');
            }
            replayed = true;
            updateState((current) => ({ ...current, loading: false }));
          }

          try {
            updateState((current) => ({ ...current, connected: true, error: null }));
            for await (const frame of gatewayClient.streamEvents(
              token, projectId, runId, cursor, signal,
            )) {
              if (!isActive()) return;
              addEvents([frame.event]);
              cursor = frame.cursor;
              updateState((current) => ({ ...current, cursor }));
              reconnectDelay = 500;
            }
            if (!signal.aborted) throw new Error('The Gateway SSE stream ended.');
          } catch (error) {
            if (signal.aborted) return;
            updateState((current) => ({ ...current, connected: false }));
            if (error instanceof GatewayError && error.status >= 400 &&
                error.status < 500 && error.status !== 408 && error.status !== 429) {
              updateState((current) => ({ ...current, error }));
              return;
            }
            if (error instanceof JournalScopeError) {
              updateState((current) => ({ ...current, error }));
              return;
            }
            updateState((current) => ({
              ...current,
              error: error instanceof Error ? error : new Error('The run event stream disconnected.'),
            }));
            await new Promise((resolve) => window.setTimeout(resolve, reconnectDelay));
            reconnectDelay = Math.min(reconnectDelay * 2, 10_000);
          }
        }
      } catch (error) {
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
  }, [projectId, runId, scopeId, token]);

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
