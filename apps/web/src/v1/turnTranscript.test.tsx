import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { gatewayClient, GatewayError } from './api';
import type { SessionEventEnvelope, SessionTurnContent } from './contracts';
import { TRANSCRIPT_ACCESS_LOST, TranscriptPanel } from './TranscriptPanel';
import { turnEventsOf, useTurnTranscript } from './turnTranscript';

afterEach(() => vi.restoreAllMocks());

/** Builds a turn event for run r1. The payload holds only an opaque reference; text is read separately. */
function turn(eventId: string, position: number, role: string, overrides: Partial<SessionEventEnvelope> = {}): SessionEventEnvelope {
  return {
    schemaVersion: 1, eventVersion: 1, eventId, identity: { projectId: 'p1', runId: 'r1', sessionId: 's1' }, position,
    occurredAt: '2026-10-20T12:00:00Z', kind: 'turn', payload: { kind: 'turn', role, content: { reference: 'opaque' } },
    objectReferences: [], ...overrides,
  };
}

function content(eventId: string, text: string, sessionId = 's1'): SessionTurnContent {
  return { eventId, identity: { projectId: 'p1', runId: 'r1', sessionId }, text };
}

// Fake apiCall that records each run binding so tests can check reads were run-bound.
const bindings: unknown[] = [];
const apiCall = (async (operation: (token: string, tenant?: string) => Promise<unknown>, binding: unknown) => {
  bindings.push(binding);
  return operation('tok.p1.r1', 'tenant-1');
}) as never;

describe('turnEventsOf', () => {
  it('keeps user and assistant turns for exactly this run', () => {
    // Drops: a system turn, a turn from another run, and a non-turn event.
    const events = [
      turn('e1', 1, 'user'),
      turn('e2', 2, 'assistant'),
      turn('e3', 3, 'system'),
      turn('e4', 4, 'user', { identity: { projectId: 'p1', runId: 'r2', sessionId: 's1' } }),
      { ...turn('e5', 5, 'user'), kind: 'toolCall' },
    ];
    expect(turnEventsOf(events, 'p1', 'r1').map((event) => event.eventId)).toEqual(['e1', 'e2']);
  });
});

describe('useTurnTranscript', () => {
  it('reads each turn once with the run-bound token and rebuilds the chat after a remount', async () => {
    const read = vi.spyOn(gatewayClient, 'readSessionTurnContent')
      .mockImplementation(async (_token, _p, _r, _s, eventId) => content(eventId, eventId === 'e1' ? 'Fix the flaky test' : 'Done. One retry fixed it.'));
    const events = [turn('e1', 1, 'user'), turn('e2', 2, 'assistant')];
    const first = renderHook(({ list }) => useTurnTranscript(list, 'p1', 'r1', apiCall), { initialProps: { list: events } });
    await waitFor(() => expect(first.result.current.turns.every((item) => item.state === 'loaded')).toBe(true));
    expect(first.result.current.turns.map((item) => [item.role, item.text])).toEqual([
      ['user', 'Fix the flaky test'], ['assistant', 'Done. One retry fixed it.'],
    ]);
    expect(read).toHaveBeenCalledWith('tok.p1.r1', 'p1', 'r1', 's1', 'e1', 'tenant-1');
    expect(bindings.at(-1)).toEqual({ projectId: 'p1', runId: 'r1' });

    // A live event replays the list; only the new turn is read.
    first.rerender({ list: [...events, turn('e3', 3, 'user')] });
    await waitFor(() => expect(first.result.current.turns).toHaveLength(3));
    await waitFor(() => expect(first.result.current.turns[2].state).toBe('loaded'));
    expect(read).toHaveBeenCalledTimes(3);
    first.unmount();

    // A fresh mount reads the text again; nothing is cached across mounts.
    const reloaded = renderHook(() => useTurnTranscript(events, 'p1', 'r1', apiCall));
    await waitFor(() => expect(reloaded.result.current.turns[0].text).toBe('Fix the flaky test'));
  });

  it('marks 404 as Not found, other errors as failed, and retries only failed turns', async () => {
    const read = vi.spyOn(gatewayClient, 'readSessionTurnContent')
      .mockRejectedValueOnce(new GatewayError(404, { error: 'turn_content_not_found' }, 'Not found'))
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(content('e2', 'Recovered'));
    const { result } = renderHook(() => useTurnTranscript([turn('e1', 1, 'user'), turn('e2', 2, 'assistant')], 'p1', 'r1', apiCall));
    await waitFor(() => expect(result.current.turns.map((item) => item.state)).toEqual(['notFound', 'failed']));
    // Retry rereads only the failed turn; Not found is final.
    act(() => result.current.retry());
    await waitFor(() => expect(result.current.turns[1].text).toBe('Recovered'));
    expect(read).toHaveBeenCalledTimes(3);
    expect(result.current.turns[0].state).toBe('notFound');
  });

  it('treats a response for another event or session as unavailable', async () => {
    vi.spyOn(gatewayClient, 'readSessionTurnContent').mockResolvedValue(content('e1', 'Wrong session', 's9'));
    const { result } = renderHook(() => useTurnTranscript([turn('e1', 1, 'user')], 'p1', 'r1', apiCall));
    await waitFor(() => expect(result.current.turns[0].state).toBe('unavailable'));
    expect(result.current.turns[0].text).toBeNull();
  });

  it('drops every loaded text after a 403 and reads nothing more until retry', async () => {
    const read = vi.spyOn(gatewayClient, 'readSessionTurnContent')
      .mockResolvedValueOnce(content('e1', 'Secret plan'))
      .mockRejectedValueOnce(new GatewayError(403, { error: 'project_read_permission_denied' }, 'Forbidden'));
    const { result, rerender } = renderHook(({ list }) => useTurnTranscript(list, 'p1', 'r1', apiCall), {
      initialProps: { list: [turn('e1', 1, 'user')] },
    });
    await waitFor(() => expect(result.current.turns[0].text).toBe('Secret plan'));
    rerender({ list: [turn('e1', 1, 'user'), turn('e2', 2, 'assistant')] });
    await waitFor(() => expect(result.current.accessLost).toBe(true));
    // Text read before the 403 must be removed, not just hidden.
    expect(result.current.turns).toEqual([]);
    // A new event after access loss must not start another read.
    rerender({ list: [turn('e1', 1, 'user'), turn('e2', 2, 'assistant'), turn('e3', 3, 'user')] });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(read).toHaveBeenCalledTimes(2);
  });
});

describe('TranscriptPanel', () => {
  it('shows text for loaded turns and only the state otherwise', () => {
    render(<TranscriptPanel transcript={{
      accessLost: false,
      retry: vi.fn(),
      turns: [
        { eventId: 'e1', sessionId: 's1', role: 'user', position: 1, occurredAt: '2026-10-20T12:00:00Z', state: 'loaded', text: 'Fix it' },
        { eventId: 'e2', sessionId: 's1', role: 'assistant', position: 2, occurredAt: '2026-10-20T12:01:00Z', state: 'notFound', text: null },
        { eventId: 'e3', sessionId: 's1', role: 'assistant', position: 3, occurredAt: '2026-10-20T12:02:00Z', state: 'failed', text: null },
      ],
    }} />);
    expect(screen.getByText('Fix it')).toBeTruthy();
    expect(screen.getByText('Not found')).toBeTruthy();
    expect(screen.getByText('Could not load this message.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('shows no message content after access is lost', () => {
    render(<TranscriptPanel transcript={{ accessLost: true, retry: vi.fn(), turns: [] }} />);
    expect(screen.getByText(TRANSCRIPT_ACCESS_LOST)).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Messages' })).toBeNull();
  });
});
