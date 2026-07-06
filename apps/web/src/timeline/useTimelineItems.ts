import { useEffect, useReducer, useRef } from 'react';
import type { RunStreamEvent } from '../api/sse';
import { timelineReducer, initialTimelineState } from './reducer';
import type { TimelineReducerState } from './types';

/**
 * Incrementally feeds SSE events into the timeline reducer.
 *
 * RD-B1 (Reconnection fix): useRunStream resets events[] to [] on reconnect to
 * the same runId. When we detect the total received count going backwards we
 * reset the reducer state and re-fold from scratch.
 *
 * BLOCKING #2 (Stream buffer stall fix): useRunStream caps its buffer at
 * DEFAULT_EVENT_BUFFER_LIMIT (1000) and slices old events off the front on
 * overflow (sse.ts). A length-based cursor stalls once the buffer caps, because
 * events.length stays at the limit while new events replace old ones. We instead
 * track the monotonic *total received* count = droppedEventCount + events.length.
 * New events since the last fold = totalReceived - prevTotalReceived, so we can
 * process exactly the genuinely-new tail regardless of front-eviction, and never
 * stall on long-lived runs. Callers should pass droppedEventCount from
 * useRunStream; it defaults to 0 for callers that never overflow.
 */
export function useTimelineItems(
  events: RunStreamEvent[],
  runId: string,
  droppedEventCount = 0,
): TimelineReducerState {
  const [state, dispatch] = useReducer(timelineReducer, initialTimelineState);
  const prevTotalRef = useRef(0);

  // Reset on runId change (navigating to a different run)
  useEffect(() => {
    dispatch({ type: 'reset' });
    prevTotalRef.current = 0;
  }, [runId]);

  useEffect(() => {
    const len = events.length;
    const totalReceived = droppedEventCount + len;
    const prevTotal = prevTotalRef.current;

    let start: number;
    if (totalReceived < prevTotal) {
      // Total went backwards → reconnect reset (useRunStream cleared events[]).
      // Re-fold whatever we now have.
      dispatch({ type: 'reset' });
      start = 0;
    } else {
      const newLogical = totalReceived - prevTotal;
      if (newLogical >= len) {
        // First fold, or so many events dropped that our processed window is
        // entirely gone — re-fold the current buffer from scratch.
        dispatch({ type: 'reset' });
        start = 0;
      } else {
        // Process only the genuinely-new tail. When the buffer has evicted
        // events off the front, `len - newLogical` still points just past the
        // last event we already folded, because newLogical accounts for the
        // evicted events too.
        start = len - newLogical;
      }
    }

    for (let i = start; i < len; i++) {
      dispatch({ type: 'event', event: events[i] });
    }
    prevTotalRef.current = totalReceived;
  }, [events, droppedEventCount]);

  return state;
}
