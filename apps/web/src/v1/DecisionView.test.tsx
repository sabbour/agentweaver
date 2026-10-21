import { act, fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { useAuth } from './AuthContext';
import type { CoordinatorDecisionStateView, SessionStatusBlocker, SessionStatusSnapshot } from './contracts';
import { DecisionList, DecisionView, STALE_DECISION_NOTICE } from './DecisionView';

const mocks = vi.hoisted(() => ({
  getDecisions: vi.fn(),
  answerGate: vi.fn(),
  resolveGate: vi.fn(),
}));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return {
    ...actual,
    gatewayClient: {
      getDecisions: mocks.getDecisions,
      answerGate: mocks.answerGate,
      resolveGate: mocks.resolveGate,
    },
  };
});

const decisionKey = { projectId: 'p1', runId: 'r1', sessionId: 's1', requestId: 'req-1' };
const approval: SessionStatusBlocker = { kind: 'awaitingApproval', requestId: 'req-1', choices: [], allowsFreeform: false, prompt: 'Deploy?' };
const question: SessionStatusBlocker = { kind: 'awaitingInput', requestId: 'req-1', choices: ['Yes', 'No'], allowsFreeform: true, prompt: 'Continue?' };

/** Builds the decision state of session s1. A null requestId means no gate is pending. */
function decisions(requestId: string | null, stateVersion = 4): CoordinatorDecisionStateView {
  return {
    stateVersion,
    executionFence: 1,
    outcomeConfirmed: false,
    workflowConfirmed: false,
    canDecompose: false,
    canDispatch: false,
    pendingGate: requestId ? {
      requestId, kind: 'approval', subjectId: 'x', authorizedActorId: 'me', fence: 1, allowedChoices: [], allowsFreeform: false,
    } : null,
  };
}

const accepted = { decisionId: 'd1', stateVersion: 8, accepted: true, executionFence: 1, issues: [] };
// Fake apiCall: runs the operation with a fixed token and tenant and records the binding it got.
const apiCallMock = vi.fn((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
const apiCall = apiCallMock as unknown as ReturnType<typeof useAuth>['apiCall'];

function renderView(props: Partial<Parameters<typeof DecisionView>[0]> = {}) {
  const refresh = vi.fn().mockResolvedValue(undefined);
  render(
    <DecisionView
      decisionKey={decisionKey}
      blocker={approval}
      decisions={decisions('req-1', 4)}
      apiCall={apiCall}
      refresh={refresh}
      snapshotBusy={false}
      {...props}
    />,
  );
  return { refresh };
}

/** Clicks and waits for the async submit to settle. */
async function click(element: HTMLElement) {
  await act(async () => { fireEvent.click(element); });
}

describe('DecisionView', () => {
  beforeEach(() => {
    mocks.getDecisions.mockReset();
    mocks.answerGate.mockReset();
    mocks.resolveGate.mockReset();
    apiCallMock.mockClear();
  });

  it('shows the title, prompt, and request identity', () => {
    renderView();
    expect(screen.getByRole('article', { name: 'Approval needed' })).toBeTruthy();
    expect(screen.getByText('Deploy?')).toBeTruthy();
    expect(screen.getByText('Session s1 · request req-1')).toBeTruthy();
  });

  it('rereads decisions before approving and sends the latest state version', async () => {
    // The screen shows version 4, but the server is at 7. No version was pinned, so 7 is sent.
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 7));
    mocks.resolveGate.mockResolvedValue(accepted);
    const { refresh } = renderView();
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(mocks.getDecisions).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'tenant-1');
    // The reread must happen before the resolve call.
    expect(mocks.getDecisions.mock.invocationCallOrder[0]).toBeLessThan(mocks.resolveGate.mock.invocationCallOrder[0]);
    expect(mocks.resolveGate).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'req-1', 7, true, expect.any(String), 'tenant-1');
    expect(apiCallMock).toHaveBeenCalledWith(expect.any(Function), { projectId: 'p1', runId: 'r1' });
    expect(screen.getByRole('status').textContent).toBe('Response sent. The run updates when the service reports the next state.');
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('sends a denial as approve=false', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 4));
    mocks.resolveGate.mockResolvedValue(accepted);
    renderView();
    await click(screen.getByTestId('approval-gate-deny-btn'));
    expect(mocks.resolveGate).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'req-1', 4, false, expect.any(String), 'tenant-1');
  });

  it('sends nothing when the reread shows the request is no longer pending', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-2', 9));
    const { refresh } = renderView();
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(mocks.resolveGate).not.toHaveBeenCalled();
    expect(mocks.answerGate).not.toHaveBeenCalled();
    expect(screen.getByRole('status').textContent)
      .toBe('This request was already answered or replaced. The view was refreshed and nothing was sent.');
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('sends nothing when the reread state version differs from the opened one', async () => {
    // The user opened version 4 (for example from a notification); the server is now at 6.
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 6));
    mocks.resolveGate.mockResolvedValue(accepted);
    const { refresh } = renderView({ decisionKey: { ...decisionKey, stateVersion: 4 } });
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(mocks.getDecisions).toHaveBeenCalledTimes(1);
    expect(mocks.resolveGate).not.toHaveBeenCalled();
    expect(mocks.answerGate).not.toHaveBeenCalled();
    expect(screen.getByRole('status').textContent).toBe(STALE_DECISION_NOTICE);
    expect(refresh).toHaveBeenCalledTimes(1);

    // After the notice, a second click is accepted and sends the reread version.
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(mocks.resolveGate).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'req-1', 6, true, expect.any(String), 'tenant-1');
  });

  it('sends when the reread state version matches the opened one', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 4));
    mocks.resolveGate.mockResolvedValue(accepted);
    renderView({ decisionKey: { ...decisionKey, stateVersion: 4 } });
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(mocks.resolveGate).toHaveBeenCalledWith('token', 'p1', 'r1', 's1', 'req-1', 4, true, expect.any(String), 'tenant-1');
  });

  it('disables actions and warns when the snapshot no longer has this request pending', async () => {
    renderView({ decisions: decisions('req-other') });
    expect(screen.getByText('This request is no longer the pending one. Refresh the run to see its current state.')).toBeTruthy();
    const approve = screen.getByTestId('approval-gate-approve-btn') as HTMLButtonElement;
    expect(approve.disabled).toBe(true);
    // Even a forced click must not reach the server.
    await click(approve);
    expect(mocks.getDecisions).not.toHaveBeenCalled();
  });

  it('disables actions while the snapshot is loading', () => {
    renderView({ snapshotBusy: true });
    expect((screen.getByTestId('approval-gate-approve-btn') as HTMLButtonElement).disabled).toBe(true);
  });

  it('answers a question with a chosen option', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 5));
    mocks.answerGate.mockResolvedValue(accepted);
    renderView({ blocker: question });
    const send = screen.getByRole('button', { name: 'Send answer' }) as HTMLButtonElement;
    // Send stays off until a choice or text is given.
    expect(send.disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));
    await click(send);
    expect(mocks.answerGate).toHaveBeenCalledWith(
      'token', 'p1', 'r1', 's1', 'req-1', 5, { choiceId: 'Yes', freeformAnswer: undefined }, expect.any(String), 'tenant-1',
    );
    expect(mocks.resolveGate).not.toHaveBeenCalled();
  });

  it('answers a question with free text', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1', 5));
    mocks.answerGate.mockResolvedValue(accepted);
    renderView({ blocker: question });
    fireEvent.change(screen.getByRole('textbox', { name: 'Your answer' }), { target: { value: 'Use main' } });
    await click(screen.getByRole('button', { name: 'Send answer' }));
    expect(mocks.answerGate).toHaveBeenCalledWith(
      'token', 'p1', 'r1', 's1', 'req-1', 5, { choiceId: undefined, freeformAnswer: 'Use main' }, expect.any(String), 'tenant-1',
    );
  });

  it('reports issues when the service does not accept the response', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1'));
    mocks.resolveGate.mockResolvedValue({ ...accepted, accepted: false, issues: [{ code: 'x', path: '$.choice', message: 'Bad choice.' }] });
    renderView();
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(screen.getByRole('status').textContent).toBe('The service did not accept this response. Bad choice. ($.choice)');
  });

  it('refreshes and explains a 409 conflict', async () => {
    mocks.getDecisions.mockResolvedValue(decisions('req-1'));
    mocks.resolveGate.mockRejectedValue(new GatewayError(409, { code: 'state_version_conflict' }, 'Conflict'));
    const { refresh } = renderView();
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(screen.getByRole('status').textContent).toBe('This request changed while you were viewing it. The view was refreshed.');
    expect(screen.getByText(/HTTP 409 \(state_version_conflict\): Conflict/)).toBeTruthy();
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('shows other failures without refreshing', async () => {
    // A non-409 failure (here the reread itself) shows the error but does not refresh.
    mocks.getDecisions.mockRejectedValue(new Error('Network down'));
    const { refresh } = renderView();
    await click(screen.getByTestId('approval-gate-approve-btn'));
    expect(screen.getByText('Network down')).toBeTruthy();
    expect(screen.queryByRole('status')).toBeNull();
    expect(refresh).not.toHaveBeenCalled();
  });
});

describe('DecisionList', () => {
  // Minimal session status with only the fields DecisionList reads.
  function status(sessionId: string, blockers: SessionStatusBlocker[]): SessionStatusSnapshot {
    return { identity: { projectId: 'p1', runId: 'r1', sessionId }, blockers } as SessionStatusSnapshot;
  }

  it('says when nothing needs input', () => {
    render(<DecisionList projectId="p1" runId="r1" statuses={{ s1: status('s1', []) }} decisions={{}} apiCall={apiCall} refresh={vi.fn()} snapshotBusy={false} />);
    expect(screen.getByText('Nothing needs your input in this run.')).toBeTruthy();
  });

  it('renders one decision per pending blocker across sessions', () => {
    render(
      <DecisionList
        projectId="p1"
        runId="r1"
        statuses={{ s1: status('s1', [approval]), s2: status('s2', [{ ...question, requestId: 'req-9' }]) }}
        decisions={{ s1: decisions('req-1') }}
        apiCall={apiCall}
        refresh={vi.fn()}
        snapshotBusy={false}
      />,
    );
    expect(screen.getByRole('article', { name: 'Approval needed' })).toBeTruthy();
    expect(screen.getByRole('article', { name: 'Question' })).toBeTruthy();
    expect(screen.getByText('Session s2 · request req-9')).toBeTruthy();
  });
});
