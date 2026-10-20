import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SessionStatusBlocker } from './contracts';
import { attentionPath, toAttentionItems, useRunAttention } from './runAttention';

const mocks = vi.hoisted(() => {
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
    expect(mocks.apiCall).toHaveBeenCalledWith(expect.any(Function), { projectId: 'p1', runId: 'r1' });
  });

  it('reports an error when the service returns a different run', async () => {
    mocks.session = { accessToken: 't', binding: { projectId: 'p1', runId: 'r1' } };
    mocks.getRunStatus.mockResolvedValue({ projectId: 'p1', runId: 'other', rootSessionId: 'root' });

    const { result } = renderHook(() => useRunAttention());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect((result.current.error as Error).message).toBe('The service returned a different run.');
    expect(result.current.items).toEqual([]);
    expect(mocks.getSessionTree).not.toHaveBeenCalled();
  });
});
