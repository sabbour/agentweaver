/**
 * Shared decision view for pending questions and approvals. The run page
 * Approvals tab, the notification bell, and later Flow and Priorities all use
 * it, so a decision looks and behaves the same everywhere.
 *
 * Each decision is keyed by project, run, session, request ID, and the
 * decision-state version. Before sending, the view reads the session's current
 * decision state again. If the request is no longer the pending one, nothing is
 * sent, so a stale screen can't answer a request that was already resolved or
 * replaced.
 */
import { Badge, Button, Field, Textarea } from '@fluentui/react-components';
import { useState } from 'react';
import { ApprovalGate } from '../components/ui/agentic';
import type { useAuth } from './AuthContext';
import { gatewayClient, GatewayError } from './api';
import type {
  CoordinatorDecisionStateView,
  SessionStatusBlocker,
  SessionStatusSnapshot,
} from './contracts';
import { DECISION_TITLES } from './decisionTitles';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice, Panel } from './pageParts';

/** The authentication hook's API-call function. */
type ApiCall = ReturnType<typeof useAuth>['apiCall'];

/** Identifies one pending decision exactly. */
export interface DecisionKey {
  projectId: string;
  runId: string;
  sessionId: string;
  requestId: string;
  /**
   * Decision-state version the user saw, for example from a notification. When set,
   * a reread with a different version sends nothing; the next action uses the reread version.
   */
  stateVersion?: number;
}

/** Shown when the reread finds a different decision-state version than the one the user opened. */
export const STALE_DECISION_NOTICE = 'This request changed since you opened it. Nothing was sent. Review it and try again.';

/** Sends an answer or decision for one exact pending request. */
export function DecisionView({
  decisionKey,
  blocker,
  decisions,
  apiCall,
  refresh,
  snapshotBusy,
}: {
  decisionKey: DecisionKey;
  blocker: SessionStatusBlocker;
  decisions?: CoordinatorDecisionStateView;
  apiCall: ApiCall;
  refresh: () => Promise<void>;
  snapshotBusy: boolean;
}) {
  const { projectId, runId, sessionId, requestId } = decisionKey;
  const binding = { projectId, runId };
  const [answer, setAnswer] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // Version the next send must match. It starts as the version the user opened
  // (if known). After a stale reread it moves to the reread version, so the
  // user's next click, after reviewing, can go through.
  const [expectedVersion, setExpectedVersion] = useState(decisionKey.stateVersion);
  // Act only when the session's pending gate is this exact request.
  const exactGate = decisions?.pendingGate?.requestId === requestId;
  // Also block while a send or a snapshot refresh is running.
  const canAct = exactGate && !busy && !snapshotBusy;

  // Sends one decision. `approve` is for approvals; `choiceId` or `freeformAnswer` is for questions.
  const submit = async (approve?: boolean, choiceId?: string, freeformAnswer?: string) => {
    if (!exactGate) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      // Read the decision state again right before sending. The snapshot on
      // screen may be old.
      const latest = await apiCall((token, tenantSelector) => gatewayClient.getDecisions(
        token, projectId, runId, sessionId, tenantSelector,
      ), binding);
      // Another request is pending now (or none). Send nothing and refresh.
      if (latest.pendingGate?.requestId !== requestId) {
        setNotice('This request was already answered or replaced. The view was refreshed and nothing was sent.');
        await refresh();
        return;
      }
      // Same request, but its state version differs from the one the user saw.
      // Send nothing, remember the new version, and let the user review.
      if (expectedVersion !== undefined && latest.stateVersion !== expectedVersion) {
        setExpectedVersion(latest.stateVersion);
        setNotice(STALE_DECISION_NOTICE);
        await refresh();
        return;
      }
      // A new key per click. The request id and state version sent with it let
      // the server reject an answer for a request that has since changed.
      const idempotencyKey = crypto.randomUUID();
      // Questions are answered; approvals, plan approvals and outcome
      // confirmations are resolved with approve or deny.
      const result = blocker.kind === 'awaitingInput'
        ? await apiCall((token, tenantSelector) => gatewayClient.answerGate(
          token, projectId, runId, sessionId, requestId, latest.stateVersion,
          { choiceId, freeformAnswer }, idempotencyKey, tenantSelector,
        ), binding)
        : await apiCall((token, tenantSelector) => gatewayClient.resolveGate(
          token, projectId, runId, sessionId, requestId, latest.stateVersion, Boolean(approve),
          idempotencyKey, tenantSelector,
        ), binding);
      if (!result.accepted) {
        // The server answered but rejected the content (for example an invalid
        // answer). List its validation issues so the user can fix them.
        const details = result.issues.map((issue) => `${issue.message} (${issue.path})`).join(' ');
        setNotice(`The service did not accept this response.${details ? ` ${details}` : ''}`);
      } else {
        setNotice('Response sent. The run updates when the service reports the next state.');
      }
      // Refresh either way so the view shows the run's next state.
      await refresh();
    } catch (reason) {
      setError(reason);
      // A 409 means the request changed between our reread and the send. Refresh so the view is current.
      if (reason instanceof GatewayError && reason.status === 409) {
        setNotice('This request changed while you were viewing it. The view was refreshed.');
        await refresh();
      }
    } finally {
      setBusy(false);
    }
  };

  // The answer box text is free-form unless it exactly matches one of the
  // offered choices; then it is sent as a choice id instead.
  const freeformChosen = !blocker.choices.includes(answer);
  return (
    <article className="v1-record" aria-label={DECISION_TITLES[blocker.kind]}>
      <div className="v1-record-title">
        <div><h3>{DECISION_TITLES[blocker.kind]}</h3><p className="v1-muted">Session {sessionId} · request {requestId}</p></div>
        <Badge appearance="tint" color={blocker.kind === 'awaitingInput' ? 'informative' : 'warning'}>Needs input</Badge>
      </div>
      {blocker.prompt && <p>{blocker.prompt}</p>}
      {blocker.kind === 'awaitingInput' ? (
        <div className="v1-form">
          {blocker.choices.map((choice) => (
            <Button key={choice} appearance={answer === choice ? 'primary' : 'secondary'} disabled={!canAct} onClick={() => setAnswer(choice)}>
              {choice}
            </Button>
          ))}
          {blocker.allowsFreeform && (
            <Field label="Your answer">
              <Textarea value={answer} onChange={(_, data) => setAnswer(data.value)} rows={3} disabled={!canAct} />
            </Field>
          )}
          <Button
            appearance="primary"
            disabled={!canAct || (freeformChosen && !(blocker.allowsFreeform && answer.trim()))}
            onClick={() => void submit(undefined, freeformChosen ? undefined : answer, freeformChosen ? answer : undefined)}
          >
            {busy ? 'Sending…' : 'Send answer'}
          </Button>
        </div>
      ) : (
        <ApprovalGate
          stepId={requestId}
          body={blocker.kind === 'awaitingOutcomeConfirmation'
            ? 'Confirm the outcome this run should deliver.'
            : blocker.kind === 'awaitingPlanApproval'
              ? 'Approve the work plan before the run continues.'
              : 'Approve this step before the run continues.'}
          disclaimer="The service checks that this request is still pending before it applies your decision."
          disabled={!canAct}
          onApprove={() => void submit(true)}
          onDeny={() => void submit(false)}
        />
      )}
      {!exactGate && <p className="v1-warning">This request is no longer the pending one. Refresh the run to see its current state.</p>}
      {error != null && <ErrorNotice code={errorCode(error)}>{errorMessage(error)}</ErrorNotice>}
      {notice && <p className="v1-muted" role="status">{notice}</p>}
    </article>
  );
}

/** Lists every pending request in a run, one `DecisionView` each. */
export function DecisionList({
  projectId,
  runId,
  statuses,
  decisions,
  apiCall,
  refresh,
  snapshotBusy,
  pinned,
}: {
  projectId: string;
  runId: string;
  /** Request and state version the user opened, for example from Flow; applies to the matching request only. */
  pinned?: { requestId: string; stateVersion: number } | null;
  statuses: Record<string, SessionStatusSnapshot>;
  decisions: Record<string, CoordinatorDecisionStateView>;
  apiCall: ApiCall;
  refresh: () => Promise<void>;
  snapshotBusy: boolean;
}) {
  // Flatten to one entry per blocker across all sessions of the run.
  const pending = Object.values(statuses).flatMap((status) =>
    status.blockers.map((blocker) => ({ status, blocker })));
  return (
    <Panel title="Pending decisions" description="Questions and approvals that block this run.">
      {pending.length === 0 ? <p className="v1-muted">Nothing needs your input in this run.</p> : (
        <div className="v1-record-list">
          {pending.map(({ status, blocker }) => (
            <DecisionView
              key={`${status.identity.sessionId}/${blocker.requestId}`}
              decisionKey={{
                projectId,
                runId,
                sessionId: status.identity.sessionId,
                requestId: blocker.requestId,
                stateVersion: pinned?.requestId === blocker.requestId ? pinned.stateVersion : undefined,
              }}
              blocker={blocker}
              decisions={decisions[status.identity.sessionId]}
              apiCall={apiCall}
              refresh={refresh}
              snapshotBusy={snapshotBusy}
            />
          ))}
        </div>
      )}
    </Panel>
  );
}
