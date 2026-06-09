import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useTimelineItems } from '../timeline/useTimelineItems';
import type { RunStreamEvent } from '../api/sse';
import type { TurnGroupItem, AgentMessageItem } from '../timeline/types';

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
});
