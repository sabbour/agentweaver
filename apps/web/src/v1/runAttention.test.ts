import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SessionStatusBlocker, UserNotification } from './contracts';
import { attentionPath, notificationAttentionItems, toAttentionItems, useRunAttention } from './runAttention';

const mocks = vi.hoisted(() => {
  // apiCall runs the operation with a fixed token and tenant, so tests can check the exact calls.
  const apiCall = vi.fn((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
  return {
    session: null as null | { accessToken: string; binding: { projectId: string; runId: string } | null },
    apiCall,
    getRunStatus: vi.fn(),
    getSessionTree: vi.fn(),
    getSessionStatus: vi.fn(),
  };
});

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ session: mocks.session, apiCall: mocks.apiCall }),
}));

vi.mock('./api', () => ({
  gatewayClient: {
    getRunStatus: mocks.getRunStatus,
    getSessionTree: mocks.getSessionTree,
    getSessionStatus: mocks.getSessionStatus,
  },
}));

const question: SessionStatusBlocker = { kind: 'awaitingInput', requestId: 'q1', choices: [], allowsFreeform: true, prompt: 'Which branch?' };
const approval: SessionStatusBlocker = { kind: 'awaitingApproval', requestId: 'a1', choices: [], allowsFreeform: false };

describe('toAttentionItems', () => {
  it('flattens blockers from every session and defaults a missing prompt to null', () => {
    expect(toAttentionItems('p1', 'r1', [
      { sessionId: 's1', blockers: [question] },
      { sessionId: 's2', blockers: [] },
      { sessionId: 's3', blockers: [approval] },
    ])).toEqual([
      { projectId: 'p1', runId: 'r1', sessionId: 's1', requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?' },
      { projectId: 'p1', runId: 'r1', sessionId: 's3', requestId: 'a1', kind: 'awaitingApproval', prompt: null },
    ]);
  });
});

describe('attentionPath', () => {
  it('opens the run Approvals view with encoded IDs', () => {
    expect(attentionPath({ projectId: 'p 1', runId: 'r/1', sessionId: 's', requestId: 'q', kind: 'awaitingInput', prompt: null }))
      .toBe('/projects/p%201/runs/r%2F1?view=approvals');
  });
});

describe('useRunAttention', () => {
  beforeEach(() => {
    mocks.session = null;
    mocks.apiCall.mockClear();
    mocks.getRunStatus.mockReset();
    mocks.getSessionTree.mockReset();
    mocks.getSessionStatus.mockReset();
  });

  it('reports no binding and does not read when the session is not bound to a run', () => {
    mocks.session = { accessToken: 't', binding: null };
    const { result } = renderHook(() => useRunAttention());
    expect(result.current.binding).toBeNull();
    expect(result.current.loading).toBe(false);
    expect(result.current.items).toEqual([]);
    expect(mocks.getRunStatus).not.toHaveBeenCalled();
  });

  it('reads the bound run tree and returns pending requests for that run only', async () => {
    mocks.session = { accessToken: 't', binding: { projectId: 'p1', runId: 'r1' } };
    mocks.getRunStatus.mockResolvedValue({ projectId: 'p1', runId: 'r1', rootSessionId: 'root' });
    mocks.getSessionTree.mockResolvedValue({
      rootSessionId: 'root',
      nodes: [{ identity: { sessionId: 'root' } }, { identity: { sessionId: 'child' } }, { identity: { sessionId: 'other' } }],
    });
    // The 'other' session reports a different run. Its blocker must be dropped.
    mocks.getSessionStatus.mockImplementation((_token: string, _p: string, _r: string, sessionId: string) => Promise.resolve({
      identity: { projectId: 'p1', runId: sessionId === 'other' ? 'r2' : 'r1', sessionId },
      blockers: sessionId === 'root' ? [] : [question],
    }));

    const { result } = renderHook(() => useRunAttention());
    expect(result.current.binding).toEqual({ projectId: 'p1', runId: 'r1' });
    expect(result.current.loading).toBe(true);

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).toBeNull();
    expect(result.current.items).toEqual([
      { projectId: 'p1', runId: 'r1', sessionId: 'child', requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?' },
    ]);
    expect(mocks.getRunStatus).toHaveBeenCalledWith('token', 'p1', 'r1', 'tenant-1');
    expect(mocks.getSessionTree).toHaveBeenCalledWith('token', 'p1', 'r1', 'root', 'tenant-1');
    // Run reads must ask for a token bound to this exact run.
    expect(mocks.apiCall).toHaveBeenCalledWith(expect.any(Function), { projectId: 'p1', runId: 'r1' });
  });

  it('reports an error when the service returns a different run', async () => {
    mocks.session = { accessToken: 't', binding: { projectId: 'p1', runId: 'r1' } };
    mocks.getRunStatus.mockResolvedValue({ projectId: 'p1', runId: 'other', rootSessionId: 'root' });

    const { result } = renderHook(() => useRunAttention());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect((result.current.error as Error).message).toBe('The service returned a different run.');
    expect(result.current.items).toEqual([]);
    // The read stops at the mismatch, so nothing from the wrong run is fetched.
    expect(mocks.getSessionTree).not.toHaveBeenCalled();
  });
});

describe('notificationAttentionItems', () => {
  const base: UserNotification = {
    notificationId: 'n1', projectId: 'p1', runId: 'r1', sessionId: 's1',
    requestId: 'q1', kind: 'awaitingInput', prompt: 'Which branch?', createdAt: '2026-10-20T00:00:00Z',
    gateKind: 'question', stateVersion: 3, updatedAt: '2026-10-20T00:00:00Z',
  };

  it('keeps pending gates and drops backlog promotion notices', () => {
    // A notification from another source must not appear as a pending gate.
    const promoted = {
      ...base, notificationId: 'n2', source: 'backlogPromoted',
      promotionId: 'pr1', taskCount: 2,
    };
    const items = notificationAttentionItems({ items: [base, promoted], count: 2, nextCursor: null });
    expect(items).toEqual([expect.objectContaining({ notificationId: 'n1', requestId: 'q1', stateVersion: 3 })]);
  });

  it('accepts the canonical pending-gate contract without a source field', () => {
    const items = notificationAttentionItems({ items: [base], count: 1, nextCursor: null });
    expect(items.map((item) => item.notificationId)).toEqual(['n1']);
  });
});