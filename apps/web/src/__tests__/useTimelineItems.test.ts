import { useTimelineItems } from '../timeline/useTimelineItems';
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { RunStreamEvent } from '../api/sse';
import type { AgentMessageItem, TurnGroupItem } from '../timeline/types';
function makeEvent(
  type: RunStreamEvent['type'],
  payload: Record<string, unknown>,
  seq = 0,
): RunStreamEvent {
  return { sequence: seq, type, payload };
}

describe('useTimelineItems', () => {

  // H-01: events array grows incrementally — no duplicates
  it('processes incremental events without duplication', () => {
    const initialEvents: RunStreamEvent[] = [
      makeEvent('agent.turn.start', { turnId: 'T1' }, 1),
    ];
    const { result, rerender } = renderHook(
      ({ events, runId }: { events: RunStreamEvent[]; runId: string }) =>
        useTimelineItems(events, runId),
      { initialProps: { events: initialEvents, runId: 'run-1' } },
    );

    expect(result.current.items).toHaveLength(1);

    const moreEvents: RunStreamEvent[] = [
      ...initialEvents,
      makeEvent('agent.message.delta', { delta: 'hello', messageId: 'M1' }, 2),
      makeEvent('agent.turn.end', { turnId: 'T1' }, 3),
    ];

    act(() => {
      rerender({ events: moreEvents, runId: 'run-1' });
    });

    expect(result.current.items).toHaveLength(1);
    const turn = result.current.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(1);
    expect(turn.active).toBe(false);
  });

  // H-02: runId changes → items clear
  it('clears items when runId changes', () => {
    const events: RunStreamEvent[] = [
      makeEvent('agent.turn.start', { turnId: 'T1' }, 1),
    ];
    const { result, rerender } = renderHook(
      ({ events, runId }: { events: RunStreamEvent[]; runId: string }) =>
        useTimelineItems(events, runId),
      { initialProps: { events, runId: 'run-1' } },
    );

    expect(result.current.items).toHaveLength(1);

    act(() => {
      rerender({ events: [], runId: 'run-2' });
    });

    expect(result.current.items).toHaveLength(0);
  });

  // H-03: 200 delta events → single AgentMessageItem
  it('200 delta events accumulate into a single AgentMessageItem', () => {
    const events: RunStreamEvent[] = [
      makeEvent('agent.turn.start', { turnId: 'T1' }, 1),
    ];
    for (let i = 0; i < 200; i++) {
      events.push(makeEvent('agent.message.delta', { delta: 'a', messageId: 'M1' }, i + 2));
    }

    const { result } = renderHook(
      ({ events, runId }: { events: RunStreamEvent[]; runId: string }) =>
        useTimelineItems(events, runId),
      { initialProps: { events, runId: 'run-1' } },
    );

    const turn = result.current.items[0] as TurnGroupItem;
    expect(turn.steps).toHaveLength(1);
    const msg = turn.steps[0] as AgentMessageItem;
    expect(msg.kind).toBe('agent-message');
    expect(msg.content).toBe('a'.repeat(200));
  });

  // RD-B1: reconnection to same run — events array resets to [] then re-grows
  it('reconnection to same runId: detects reset and re-folds without dropping events', () => {
    const initialEvents: RunStreamEvent[] = [
      makeEvent('agent.turn.start', { turnId: 'T1' }, 1),
      makeEvent('agent.message.delta', { delta: 'first', messageId: 'M1' }, 2),
      makeEvent('agent.turn.end', { turnId: 'T1' }, 3),
    ];

    const { result, rerender } = renderHook(
      ({ events, runId }: { events: RunStreamEvent[]; runId: string }) =>
        useTimelineItems(events, runId),
      { initialProps: { events: initialEvents, runId: 'run-1' } },
    );

    // Initial state: one closed turn
    expect(result.current.items).toHaveLength(1);
    expect((result.current.items[0] as TurnGroupItem).active).toBe(false);

    // Simulate reconnect: useRunStream resets events to []
    act(() => {
      rerender({ events: [], runId: 'run-1' });
    });

    // Simulate replay + new events coming in after reconnect
    const afterReconnect: RunStreamEvent[] = [
      makeEvent('agent.turn.start', { turnId: 'T1' }, 1),
      makeEvent('agent.message.delta', { delta: 'first', messageId: 'M1' }, 2),
      makeEvent('agent.turn.end', { turnId: 'T1' }, 3),
      makeEvent('agent.turn.start', { turnId: 'T2' }, 4),
      makeEvent('agent.message.delta', { delta: 'after reconnect', messageId: 'M2' }, 5),
    ];

    act(() => {
      rerender({ events: afterReconnect, runId: 'run-1' });
    });

    // Must have 2 turns — no dropped events
    const turns = result.current.items.filter((i) => i.kind === 'turn-group') as TurnGroupItem[];
    expect(turns).toHaveLength(2);
    expect((turns[1].steps[0] as AgentMessageItem).content).toBe('after reconnect');
  });

  // BLOCKING #2: buffer overflow — useRunStream caps its buffer at 1000 and slices
  // events off the front, incrementing droppedEventCount. A length-based cursor
  // would stall (length stays 1000). The totalReceived high-water mark must keep
  // processing genuinely-new events past 1000.
  it('keeps processing after the 1000-event buffer caps and evicts old events', () => {
    const LIMIT = 1000;
    // Fill the buffer to the cap with tool-call pairs so each produces a step.
    let seq = 0;
    const initial: RunStreamEvent[] = [makeEvent('agent.turn.start', { turnId: 'T1' }, ++seq)];
    while (initial.length < LIMIT) {
      const id = `c${seq}`;
      initial.push(makeEvent('tool.call', { callId: id, name: 'noop', args: {} }, ++seq));
    }

    const { result, rerender } = renderHook(
      ({ events, runId, dropped }: { events: RunStreamEvent[]; runId: string; dropped: number }) =>
        useTimelineItems(events, runId, dropped),
      { initialProps: { events: initial, runId: 'run-1', dropped: 0 } },
    );

    const turnBefore = result.current.items.find((i) => i.kind === 'turn-group') as TurnGroupItem;
    const stepsBefore = turnBefore.steps.length;
    expect(stepsBefore).toBeGreaterThan(0);

    // Simulate 500 more events arriving. The buffer stays at 1000 by slicing the
    // oldest 500 off the front, so droppedEventCount === 500. This is exactly the
    // condition that stalled the old length-based cursor.
    let buffer = [...initial];
    let dropped = 0;
    for (let i = 0; i < 500; i++) {
      const id = `n${seq}`;
      buffer.push(makeEvent('tool.call', { callId: id, name: 'noop', args: {} }, ++seq));
      if (buffer.length > LIMIT) {
        buffer = buffer.slice(buffer.length - LIMIT);
        dropped += 1;
      }
    }
    expect(buffer.length).toBe(LIMIT);
    expect(dropped).toBe(500);

    act(() => {
      rerender({ events: buffer, runId: 'run-1', dropped });
    });

    const turnAfter = result.current.items.find((i) => i.kind === 'turn-group') as TurnGroupItem;
    // The reducer kept folding the new tail: strictly more steps than before the
    // overflow. A stalled length-based cursor would leave stepsAfter === stepsBefore.
    expect(turnAfter.steps.length).toBeGreaterThan(stepsBefore);
  });
});
