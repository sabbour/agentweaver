import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useParams, useSearchParams } from 'react-router-dom';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiErrorMessage, parseApiBody } from '../api/errors';
import type { RunStreamEvent } from '../api/sse';
import { useSeededRunStream } from '../hooks/useSeededRunStream';
import { buildRunTimeline } from '../timeline/runTimelineSteps';
import { RunTimeline } from '../components/RunTimeline';
import { Composer } from '../components/ui/copilot';
import { ApprovalGate } from '../components/ui/agentic';

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: 0,
    height: '100%',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXXL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  title: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  subtitle: {
    color: tokens.colorNeutralForeground3,
  },
  transcript: {
    flex: 1,
    minHeight: 0,
    overflowY: 'auto',
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL}`,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  emptyState: {
    color: tokens.colorNeutralForeground3,
    maxWidth: '640px',
  },
  suggestions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
    maxWidth: '640px',
  },
  approvals: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  // Optimistic (not-yet-server-confirmed) user message — same visual weight as a real
  // user turn but dimmed with a pending indicator, so sending feels instant instead of
  // waiting several seconds for the server round trip before anything appears (#item-1).
  pendingMessage: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: tokens.spacingVerticalXXS,
    alignSelf: 'flex-end',
    opacity: 0.7,
  },
  pendingMessageRow: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  pendingMessageText: {
    whiteSpace: 'pre-wrap',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  pendingMessageStatus: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
  approvalHeading: {
    fontWeight: tokens.fontWeightSemibold,
  },
  composerStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalXXL}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  composerStatus: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

/**
 * Starter prompts shown on the empty-state screen (before a run exists) so a
 * first-time user or someone doing a quick smoke test doesn't have to think of a
 * prompt themselves. Each one is a realistic, self-contained request the operator
 * assistant can act on today via its MCP tool surface (project_list,
 * project_list_runs, run_status, coordinator_start/run_task, skill_list) — see
 * decisions/inbox/trinity-assistant-run-prompt-buttons.md for why these five and
 * not the landing page's longer, more elaborate demo scenarios.
 */
const SUGGESTED_PROMPTS: string[] = [
  'List my projects and each one\u2019s most recent run status.',
  'Start a quick smoke-test run: ask the coordinator to add a one-line README update to a project.',
  'Show me the status of my most recent run, and flag anything waiting on my approval.',
  'What MCP tools and skills do you currently have access to?',
  'Create a new test project and kick off a small run to verify everything is wired up.',
];

function readString(payload: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];
    if (value != null && String(value).trim() !== '') return String(value);
  }
  return undefined;
}

interface PendingApproval {
  event: RunStreamEvent;
  isShell: boolean;
  requestId: string;
  commandHash: string;
  toolName: string;
  targetRunId: string;
}

/**
 * Derive the unresolved tool/shell approval requests from the operator run's event
 * stream. Approval-required events are matched against later resolution events
 * (`tool.approval_resolved` / `tool.auto_approved`, and shell approve/deny reflections)
 * by request id / command hash, so an approved-or-denied gate is dropped from the UI.
 * The operator run has no children, so approvals always target the run itself.
 */
function derivePendingApprovals(events: RunStreamEvent[], runId: string): PendingApproval[] {
  const resolvedRequestIds = new Set<string>();
  const resolvedCommandHashes = new Set<string>();
  for (const evt of events) {
    if (evt.type === 'tool.approval_resolved' || evt.type === 'tool.auto_approved') {
      const id = readString(evt.payload, ['requestId', 'request_id']);
      if (id) resolvedRequestIds.add(id);
    }
    const hash = readString(evt.payload, ['commandHash', 'command_hash']);
    if (hash && (evt.type === 'tool.approval_resolved' || evt.type === 'tool.auto_approved')) {
      resolvedCommandHashes.add(hash);
    }
  }
  const pending: PendingApproval[] = [];
  for (const evt of events) {
    const isShell = evt.type === 'shell.approval_required';
    const isTool = evt.type === 'tool.approval_required';
    if (!isShell && !isTool) continue;
    const requestId = readString(evt.payload, ['requestId', 'request_id']) ?? '';
    const commandHash = readString(evt.payload, ['commandHash', 'command_hash']) ?? '';
    if (requestId && resolvedRequestIds.has(requestId)) continue;
    if (commandHash && resolvedCommandHashes.has(commandHash)) continue;
    pending.push({
      event: evt,
      isShell,
      requestId,
      commandHash,
      toolName: readString(evt.payload, ['toolName', 'tool_name']) ?? (isShell ? 'run_command' : 'tool'),
      targetRunId: readString(evt.payload, ['childRunId', 'child_run_id']) ?? runId,
    });
  }
  return pending;
}

function AssistantApprovalGate({ approval }: { approval: PendingApproval }) {
  const styles = useStyles();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [resolved, setResolved] = useState<string | null>(null);

  const settle = useCallback(async (fn: () => Promise<void>, outcome: string) => {
    if (busy || resolved || !approval.targetRunId) return;
    setBusy(true);
    setError(null);
    try {
      await fn();
      setResolved(outcome);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }, [approval.targetRunId, busy, resolved]);

  const approve = (scope: 'once' | 'run' | 'always' = 'once') => {
    void settle(
      () => (approval.isShell
        ? apiClient.approveShell(approval.targetRunId, approval.commandHash)
        : apiClient.approveTool(approval.targetRunId, approval.requestId, scope)),
      scope,
    );
  };
  const deny = () => {
    void settle(
      () => (approval.isShell
        ? apiClient.denyShell(approval.targetRunId, approval.commandHash)
        : apiClient.denyTool(approval.targetRunId, approval.requestId)),
      'deny',
    );
  };

  if (resolved) {
    const label = resolved === 'deny'
      ? `Denied · ${approval.toolName}`
      : `Allowed · ${approval.toolName}`;
    return <Text className={styles.composerStatus} data-testid="assistant-approval-resolved">{label}</Text>;
  }

  const command = readString(approval.event.payload, ['command']);
  const intention = readString(approval.event.payload, ['intention', 'message']);
  const target = approval.isShell ? (command ?? 'a shell command') : approval.toolName;
  const riskText = [
    `Allow ${target}?`,
    intention,
    'Nothing runs until you approve. You can review the results afterwards.',
  ].filter(Boolean).join(' ');

  return (
    <div data-testid="assistant-approval-gate">
      <Text className={styles.approvalHeading} weight="semibold">
        {approval.isShell ? 'Command approval required' : 'Tool Approval Required'}
      </Text>
      <ApprovalGate
        stepId={approval.requestId || approval.commandHash || `approval-${approval.event.sequence}`}
        riskText={riskText}
        approveLabel="Allow once"
        denyLabel="Deny"
        additionalActions={!approval.isShell ? (
          <>
            <Button appearance="secondary" size="small" disabled={busy} onClick={() => approve('run')}>
              Allow for session
            </Button>
            <Button appearance="secondary" size="small" disabled={busy} onClick={() => approve('always')}>
              Always allow
            </Button>
          </>
        ) : undefined}
        onApprove={() => approve()}
        onDeny={deny}
      />
      {error && <Text className={styles.composerStatus} role="alert">Approval failed: {error}</Text>}
    </div>
  );
}

export interface AssistantRunPageProps {
  /** Optional project scope forwarded to the create-run call for project-aware MCP tools. */
  projectId?: string;
}

/**
 * AssistantRunPage — MCP-driven operator assistant chat (#346).
 *
 * A leaner, purpose-built variant of CoordinatorRunPage's single-agent / no-work-plan
 * rendering path: no DAG, no work plan, no assembly chrome — the center IS the transcript.
 * It reuses the same run-stream primitives as the coordinator page (useSeededRunStream +
 * buildRunTimeline + RunTimeline) plus the shared Composer and ApprovalGate, so tool
 * approvals for gated MCP tools work through the existing approve/deny wiring.
 *
 * Follow-up refactor (noted for a later pass): extract CoordinatorRunPage's single-agent
 * transcript path into a shared component and have both pages consume it, rather than the
 * lean reuse-the-primitives approach taken here.
 */
export function AssistantRunPage({ projectId }: AssistantRunPageProps) {
  const styles = useStyles();
  const params = useParams<{ projectId?: string }>();
  const effectiveProjectId = projectId ?? params.projectId;
  const [searchParams, setSearchParams] = useSearchParams();

  // The operator run id is created lazily on the first composer submit; until then the
  // stream stays disabled ('') and the page shows the empty invitation state. If the page
  // loads with `?runId=...` already in the URL (a refresh, a bookmark, or the browser back
  // button), resume that run instead of losing it — the conversation otherwise had no way
  // to survive navigating away (#346 follow-up).
  const [runId, setRunId] = useState<string>(() => searchParams.get('runId') ?? '');
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Optimistically-rendered user message, shown immediately on send and cleared once the
  // server-confirmed copy shows up in the event stream (#item-1) — see the render + effect
  // below.
  const [pendingMessage, setPendingMessage] = useState<{ id: string; text: string } | null>(null);
  const transcriptRef = useRef<HTMLDivElement | null>(null);
  const scrolledForRunRef = useRef<string | null>(null);
  // Remembers the run id of a conversation that turned out to be genuinely, permanently
  // gone (404 run_not_found / 409 operator_run_closed below — NOT plain idle timeout, which
  // now wakes the same run transparently), so the NEXT createAssistantRun call (the user's
  // very next submit) can pass it as resume_from_run_id and auto-seed the new run's context
  // with whatever of that conversation's history is still recoverable. Only those two
  // reactive error branches ever set this — a ref (not state) so setting it never triggers
  // a render, and it's cleared right after being consumed so it can't leak into an unrelated
  // new conversation started later (e.g. via "New Session" from the Sessions page).
  const pendingResumeFromRunIdRef = useRef<string | null>(null);
  const sendingRef = useRef(false);

  // Populate (not submit) the composer with an example prompt — the user still reviews
  // and hits send themselves, matching the Composer's normal edit-then-submit flow rather
  // than auto-dispatching on click.
  const handleSuggestionClick = useCallback((prompt: string) => {
    setInput(prompt);
    setError(null);
  }, []);

  const { events, status: streamStatus } = useSeededRunStream(runId, undefined);

  // Keep the URL in sync with the active run id so a refresh or shared link resumes the
  // same conversation instead of dropping back to the empty invitation state.
  useEffect(() => {
    const current = searchParams.get('runId') ?? '';
    if (current === runId) return;
    const next = new URLSearchParams(searchParams);
    if (runId) next.set('runId', runId); else next.delete('runId');
    setSearchParams(next, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  const timelineModel = useMemo(
    () => buildRunTimeline(events, { stripSerializedWorkPlan: false }),
    [events],
  );
  const pendingApprovals = useMemo(
    () => (runId ? derivePendingApprovals(events, runId) : []),
    [events, runId],
  );

  // Clear the optimistic pending message once its server-confirmed counterpart appears in
  // the parsed timeline (a "user" role message with the same text) — the real message then
  // renders through the normal RunTimeline path instead (#item-1).
  useEffect(() => {
    if (!pendingMessage) return;
    const confirmed = timelineModel.steps.some((step) => step.messages.some(
      (msg) => msg.role === 'user' && msg.text.trim() === pendingMessage.text.trim(),
    ));
    const syncPendingMessage = async () => {
      if (confirmed) setPendingMessage(null);
    };
    void syncPendingMessage();
  }, [pendingMessage, timelineModel]);

  // Auto-scroll to the latest message once a resumed run's history has loaded (#item-9) —
  // without this, reopening `?runId=...` left the viewport scrolled to the top of a long
  // transcript instead of showing the most recent activity.
  useEffect(() => {
    if (!runId || events.length === 0) return;
    if (scrolledForRunRef.current === runId) return;
    scrolledForRunRef.current = runId;
    const node = transcriptRef.current;
    if (!node) return;
    requestAnimationFrame(() => {
      node.scrollTo({ top: node.scrollHeight });
    });
  }, [runId, events.length]);

  const handleSubmit = useCallback(async () => {
    const message = input.trim();
    if (!message || busy || sendingRef.current) return;
    sendingRef.current = true;
    setInput('');
    setBusy(true);
    setError(null);
    setPendingMessage({ id: `pending-${Date.now()}`, text: message });
    const isNewRun = !runId;
    try {
      if (isNewRun) {
        const created = await apiClient.createAssistantRun({
          message,
          project_id: effectiveProjectId,
          resume_from_run_id: pendingResumeFromRunIdRef.current ?? undefined,
        });
        // Consumed (or not needed) — clear so it never leaks into a later, unrelated new
        // conversation (e.g. one started via "New Session" from the Sessions page).
        pendingResumeFromRunIdRef.current = null;
        setRunId(created.run_id);
      } else {
        await apiClient.sendAssistantMessage(runId, { message });
      }
    } catch (err) {
      setPendingMessage(null);
      if (
        isNewRun &&
        err instanceof ApiError &&
        err.status === 429 &&
        parseApiBody(err.body).error === 'operator_run_limit'
      ) {
        setError(
          'You have too many active assistant conversations. End one before starting another.',
        );
      } else if (
        !isNewRun &&
        err instanceof ApiError &&
        err.status === 404 &&
        parseApiBody(err.body).error === 'run_not_found'
      ) {
        // Idle timeout no longer causes this — an idle run now goes dormant server-side and
        // wakes transparently (same run id, normal 200) the next time a message is sent.
        // This only fires for a genuinely gone run: a foreign/nonexistent run id, or a
        // legacy pre-fix zombie row from before that behavior shipped. Remember its id so
        // the next submit auto-seeds a fresh run with whatever history we can recover, then
        // reset to the start state so the user can send that next message.
        pendingResumeFromRunIdRef.current = runId;
        setRunId('');
        setError('This conversation could not be found, so it can no longer be continued. Send your message again to start a new one that remembers this conversation.');
      } else if (
        !isNewRun &&
        err instanceof ApiError &&
        err.status === 409 &&
        parseApiBody(err.body).error === 'operator_run_closed'
      ) {
        // The run's durable event stream is already sealed with a genuinely terminal
        // run.completed event — a real end-of-conversation, not plain inactivity (idle runs
        // are dormant, not sealed, and wake transparently). Remember its id (same auto-seed
        // handoff as the run_not_found case above) and reset to the start state.
        pendingResumeFromRunIdRef.current = runId;
        setRunId('');
        setError('This conversation has ended and can no longer be continued. Send your message again to start a new one that remembers this conversation.');
      } else {
        setError(formatApiErrorMessage(err));
      }
    } finally {
      sendingRef.current = false;
      setBusy(false);
    }
  }, [busy, effectiveProjectId, input, runId]);

  return (
    <div className={styles.page} data-testid="assistant-run-page">
      <div className={styles.header}>
        <Text className={styles.title}>Agentweaver assistant</Text>
        <Text className={styles.subtitle}>
          Ask the operator assistant to inspect or drive the platform. It routes through the
          Agentweaver MCP tools; anything that changes state asks for your approval first.
        </Text>
      </div>

      <div className={styles.transcript} data-testid="assistant-transcript" ref={transcriptRef}>
        {!runId && (
          <Text className={styles.emptyState} data-testid="assistant-empty-state">
            Start a conversation below. Your first message opens an operator run and the reply
            streams in here.
          </Text>
        )}
        {!runId && (
          <div className={styles.suggestions} data-testid="assistant-suggested-prompts">
            {SUGGESTED_PROMPTS.map((prompt) => (
              <Button
                key={prompt}
                appearance="outline"
                size="small"
                shape="circular"
                data-testid="assistant-suggested-prompt"
                onClick={() => handleSuggestionClick(prompt)}
              >
                {prompt}
              </Button>
            ))}
          </div>
        )}
        {runId && (
          <RunTimeline
            flat
            steps={timelineModel.steps}
            running={timelineModel.running}
            emptyHint="Messages, tool calls, and activity will appear here as the assistant responds."
          />
        )}
        {pendingMessage && (
          <div className={styles.pendingMessage} data-testid="assistant-pending-message">
            <div className={styles.pendingMessageRow}>
              <Spinner size="extra-tiny" aria-label="Sending" />
              <span className={styles.pendingMessageText}>{pendingMessage.text}</span>
            </div>
            <span className={styles.pendingMessageStatus}>Sending…</span>
          </div>
        )}
        {pendingApprovals.length > 0 && (
          <div className={styles.approvals} data-testid="assistant-approvals">
            {pendingApprovals.map((approval) => (
              <AssistantApprovalGate key={`approval-${approval.event.sequence}`} approval={approval} />
            ))}
          </div>
        )}
      </div>

      <div className={styles.composerStack}>
        {error && (
          <MessageBar intent="error">
            <MessageBarBody data-testid="assistant-error">{error}</MessageBarBody>
          </MessageBar>
        )}
        <Composer
          value={input}
          placeholder="Message the assistant..."
          onChange={(value) => { setInput(value); setError(null); }}
          onSubmit={(_, data) => { if (data.value.trim()) void handleSubmit(); }}
          isSending={busy}
          // Do NOT disable the whole textarea while a send is in flight — React blurs
          // disabled form elements, which stole focus from the composer after every send
          // (#item-1) and made the whole page feel frozen even though the request was
          // just an optimistic-UI background fetch (#item-2). Only the send affordance
          // itself is gated via disableSend, so the user can keep typing (and even queue
          // up their next message) while the previous one is still in flight; handleSubmit
          // already guards against a duplicate dispatch via `busy`/`sendingRef`.
          disableSend={busy || !input.trim()}
        />
        <Text className={styles.composerStatus} aria-live="polite">
          {runId
            ? `Connected to operator run ${runId} · stream ${streamStatus}`
            : 'Your first message creates an operator run.'}
        </Text>
      </div>
    </div>
  );
}
