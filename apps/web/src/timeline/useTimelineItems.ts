import { useEffect, useReducer, useRef } from 'react';
import type { RunStreamEvent } from '../api/sse';
import { timelineReducer, initialTimelineState } from './reducer';
import type { TimelineReducerState } from './types';

/**
 * Incrementally feeds SSE events into the timeline reducer.
 *
 * RD-B1 (Reconnection fix): useRunStream resets events[] to [] on reconnect to
 * the same runId.  When we detect events.length < processedCountRef.current we
 * know a reset occurred — we reset the reducer state and re-fold from scratch.
 */
export function useTimelineItems(
  events: RunStreamEvent[],
  runId: string,
): TimelineReducerState {
  const [state, dispatch] = useReducer(timelineReducer, initialTimelineState);
  const processedCountRef = useRef(0);

  // Reset on runId change (navigating to a different run)
  useEffect(() => {
    dispatch({ type: 'reset' });
    processedCountRef.current = 0;
  }, [runId]);

  useEffect(() => {
    const newCount = events.length;

    // RD-B1: if the events array shrank (useRunStream reset on reconnect),
    // we must re-fold the entire history from scratch.
    if (newCount < processedCountRef.current) {
      dispatch({ type: 'reset' });
      processedCountRef.current = 0;
    }

    if (newCount <= processedCountRef.current) return;

    // Process only the new tail
    for (let i = processedCountRef.current; i < newCount; i++) {
      dispatch({ type: 'event', event: events[i] });
    }
    processedCountRef.current = newCount;
  }, [events]);

  return state;
}
