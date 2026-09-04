import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Button,
  MessageBar,
  MessageBarActions,
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

interface OptimisticUserMessage {
  id: string;
  runId: string;
  text: string;
  normalizedText: string;
  expectedServerOccurrence: number | null;
  status: 'sending' | 'syncing';
}

function normalizeMessageText(text: string): string {
  return text.trim();
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
      const id = readString(evt.payload, ['requestId', 'request_id', 'RequestId']);
      if (id) resolvedRequestIds.add(id);
    }
    const hash = readString(evt.payload, ['commandHash', 'command_hash', 'CommandHash']);
    if (hash && (evt.type === 'tool.approval_resolved' || evt.type === 'tool.auto_approved')) {
      resolvedCommandHashes.add(hash);
    }
  }
  const pending: PendingApproval[] = [];
  for (const evt of events) {
    const isShell = evt.type === 'shell.approval_required';
    const isTool = evt.type === 'tool.approval_required' || evt.type === 'tool.approval_context';
    if (!isShell && !isTool) continue;
    const requestId = readString(evt.payload, ['requestId', 'request_id', 'RequestId']) ?? '';
    const commandHash = readString(evt.payload, ['commandHash', 'command_hash', 'CommandHash']) ?? '';
    if (requestId && resolvedRequestIds.has(requestId)) continue;
    if (commandHash && resolvedCommandHashes.has(commandHash)) continue;
    pending.push({
      event: evt,
      isShell,
      requestId,
      commandHash,
      toolName: readString(evt.payload, ['toolName', 'tool_name', 'ToolName']) ?? (isShell ? 'run_command' : 'tool'),
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
  const routeRunId = searchParams.get('runId') ?? '';

  // The URL is the conversation source of truth. AssistantRoute does not key this page by
  // runId, so assigning the first run id connects the stream without remounting this
  // component and discarding its optimistic message.
  const runId = routeRunId;
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [reconciliationError, setReconciliationError] = useState<string | null>(null);
  const [optimisticMessages, setOptimisticMessages] = useState<OptimisticUserMessage[]>([]);
  const transcriptRef = useRef<HTMLDivElement | null>(null);
  const messagesEndRef = useRef<HTMLDivElement | null>(null);
  const composerTextareaRef = useRef<HTMLTextAreaElement | null>(null);
  const scrolledForRunRef = useRef<string | null>(null);
  const shouldStickToBottomRef = useRef(true);
  const lastRenderedMessageSignatureRef = useRef('');
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
  const optimisticMessageIdRef = useRef(0);
  const automaticReconciliationAttemptedRef = useRef(new Set<string>());

  // Populate (not submit) the composer with an example prompt — the user still reviews
  // and hits send themselves, matching the Composer's normal edit-then-submit flow rather
  // than auto-dispatching on click.
  const handleSuggestionClick = useCallback((prompt: string) => {
    setInput(prompt);
    setError(null);
    const textarea = composerTextareaRef.current;
    if (!textarea) return;
    requestAnimationFrame(() => {
      textarea.focus();
      const cursor = prompt.length;
      textarea.setSelectionRange(cursor, cursor);
    });
  }, []);

  const {
    events,
    baselineEvents,
    baselineReady,
    status: streamStatus,
    error: streamError,
    seedError,
    reconnect,
    refresh,
  } = useSeededRunStream(runId);

  const timelineModel = useMemo(
    () => buildRunTimeline(events, { stripSerializedWorkPlan: false }),
    [events],
  );
  const renderedMessages = useMemo(
    () => timelineModel.steps.flatMap((step) => step.messages),
    [timelineModel.steps],
  );
  const renderedMessageSignature = useMemo(() => {
    const lastMessage = renderedMessages.at(-1);
    return `${renderedMessages.length}:${lastMessage?.role ?? ''}:${lastMessage?.text ?? ''}`;
  }, [renderedMessages]);
  const serverUserMessageCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const message of renderedMessages) {
      if (message.role !== 'user') continue;
      const normalizedText = normalizeMessageText(message.text);
      counts.set(normalizedText, (counts.get(normalizedText) ?? 0) + 1);
    }
    return counts;
  }, [renderedMessages]);
  const baselineUserMessageCounts = useMemo(() => {
    const counts = new Map<string, number>();
    const baselineTimeline = buildRunTimeline(
      baselineEvents,
      { stripSerializedWorkPlan: false },
    );
    for (const step of baselineTimeline.steps) {
      for (const message of step.messages) {
        if (message.role !== 'user') continue;
        const normalizedText = normalizeMessageText(message.text);
        counts.set(normalizedText, (counts.get(normalizedText) ?? 0) + 1);
      }
    }
    return counts;
  }, [baselineEvents]);
  const optimisticExpectedOccurrences = useMemo(() => {
    const nextExpectedOccurrence = new Map(baselineUserMessageCounts);
    const expectedByMessageId = new Map<string, number>();
    for (const message of optimisticMessages) {
      if (message.runId !== runId) continue;
      const currentMaximum = nextExpectedOccurrence.get(message.normalizedText) ?? 0;
      const expectedServerOccurrence = message.expectedServerOccurrence
        ?? (baselineReady ? currentMaximum + 1 : null);
      if (expectedServerOccurrence === null) continue;
      nextExpectedOccurrence.set(
        message.normalizedText,
        Math.max(currentMaximum, expectedServerOccurrence),
      );
      expectedByMessageId.set(message.id, expectedServerOccurrence);
    }
    return expectedByMessageId;
  }, [baselineReady, baselineUserMessageCounts, optimisticMessages, runId]);
  const visibleOptimisticMessages = useMemo(
    () => optimisticMessages.filter((message) => {
      if (message.runId !== runId) return false;
      const expectedServerOccurrence = optimisticExpectedOccurrences.get(message.id);
      return expectedServerOccurrence === undefined
        || (serverUserMessageCounts.get(message.normalizedText) ?? 0)
            < expectedServerOccurrence;
    }),
    [optimisticExpectedOccurrences, optimisticMessages, runId, serverUserMessageCounts],
  );
  const pendingApprovals = useMemo(
    () => (runId ? derivePendingApprovals(events, runId) : []),
    [events, runId],
  );

  const reconcileDurableHistory = useCallback(async () => {
    setReconciliationError(null);
    try {
      await refresh();
    } catch {
      setReconciliationError(
        'The sent message could not be confirmed from saved history. Retry sync to reconcile it.',
      );
    } finally {
      reconnect();
    }
  }, [reconnect, refresh]);

  useEffect(() => {
    if (!runId || streamStatus !== 'error') return;
    const candidates = optimisticMessages.filter(
      (message) => message.runId === runId && message.status === 'syncing',
    );
    const unattempted = candidates.filter(
      (message) => !automaticReconciliationAttemptedRef.current.has(message.id),
    );
    if (unattempted.length === 0) return;
    for (const message of unattempted) {
      automaticReconciliationAttemptedRef.current.add(message.id);
    }
    void reconcileDurableHistory();
  }, [optimisticMessages, reconcileDurableHistory, runId, streamStatus]);

  const updateShouldStickToBottom = useCallback(() => {
    const node = transcriptRef.current;
    if (!node) return;
    const distanceFromBottom = node.scrollHeight - node.scrollTop - node.clientHeight;
    shouldStickToBottomRef.current = distanceFromBottom <= 96;
  }, []);

  const scrollLatestMessageIntoView = useCallback((behavior: ScrollBehavior = 'smooth') => {
    messagesEndRef.current?.scrollIntoView({ behavior, block: 'end' });
  }, []);

  useEffect(() => {
    shouldStickToBottomRef.current = true;
    lastRenderedMessageSignatureRef.current = '';
  }, [runId]);

  // Always reveal the user's own just-sent optimistic message, even if they had scrolled up
  // to read history; once they're back at the bottom, assistant streaming can keep following.
  useEffect(() => {
    if (visibleOptimisticMessages.length === 0) return;
    shouldStickToBottomRef.current = true;
    requestAnimationFrame(() => {
      scrollLatestMessageIntoView('smooth');
    });
  }, [scrollLatestMessageIntoView, visibleOptimisticMessages.length]);

  // Auto-scroll to the latest message once a resumed run's history has loaded (#item-9) —
  // without this, reopening `?runId=...` left the viewport scrolled to the top of a long
  // transcript instead of showing the most recent activity.
  useEffect(() => {
    if (!runId || events.length === 0) return;
    if (scrolledForRunRef.current === runId) return;
    scrolledForRunRef.current = runId;
    requestAnimationFrame(() => {
      scrollLatestMessageIntoView('auto');
      shouldStickToBottomRef.current = true;
    });
  }, [events.length, runId, scrollLatestMessageIntoView]);

  useEffect(() => {
    if (!runId || renderedMessages.length === 0) return;
    const previousSignature = lastRenderedMessageSignatureRef.current;
    lastRenderedMessageSignatureRef.current = renderedMessageSignature;
    if (!previousSignature) return;
    if (!shouldStickToBottomRef.current) return;
    requestAnimationFrame(() => {
      scrollLatestMessageIntoView('smooth');
    });
  }, [renderedMessageSignature, renderedMessages.length, runId, scrollLatestMessageIntoView]);

  const handleSubmit = useCallback(async () => {
    const message = input.trim();
    if (!message || busy || sendingRef.current) return;
    sendingRef.current = true;
    setInput('');
    setBusy(true);
    setError(null);
    const normalizedText = normalizeMessageText(message);
    const isNewRun = !runId;
    const isResumingPriorConversation = isNewRun
      && pendingResumeFromRunIdRef.current !== null;
    const existingExpectedOccurrence = optimisticMessages
      .filter((candidate) => (
        candidate.runId === runId
        && candidate.normalizedText === normalizedText
        && candidate.expectedServerOccurrence !== null
      ))
      .reduce(
        (maximum, candidate) => Math.max(
          maximum,
          candidate.expectedServerOccurrence ?? 0,
        ),
        0,
      );
    const expectedServerOccurrence = baselineReady && !isResumingPriorConversation
      ? Math.max(
          serverUserMessageCounts.get(normalizedText) ?? 0,
          existingExpectedOccurrence,
        ) + 1
      : null;
    const optimisticMessage: OptimisticUserMessage = {
      id: `pending-${++optimisticMessageIdRef.current}`,
      runId,
      text: message,
      normalizedText,
      expectedServerOccurrence,
      status: 'sending',
    };
    setOptimisticMessages((current) => [...current, optimisticMessage]);
    try {
      if (isNewRun) {
        const created = await apiClient.createAssistantRun({
          message,
          defer_first_turn: true,
          project_id: effectiveProjectId,
          resume_from_run_id: pendingResumeFromRunIdRef.current ?? undefined,
        });
        // Consumed (or not needed) — clear so it never leaks into a later, unrelated new
        // conversation (e.g. one started via "New Session" from the Sessions page).
        pendingResumeFromRunIdRef.current = null;
        setOptimisticMessages((current) => current.map((candidate) => (
          candidate.id === optimisticMessage.id
            ? { ...candidate, runId: created.run_id }
            : candidate
        )));
        const next = new URLSearchParams(searchParams);
        next.set('runId', created.run_id);
        setSearchParams(next, { replace: true });
        // Create the conversation first so React can bind its SSE stream while this request is
        // still running. Supplying the opening message to createAssistantRun would keep the run id
        // hidden until the entire model turn completed, making the first reply impossible to stream.
        await apiClient.sendAssistantMessage(created.run_id, { message });
      } else {
        await apiClient.sendAssistantMessage(runId, { message });
      }
      setOptimisticMessages((current) => current.map((candidate) => (
        candidate.id === optimisticMessage.id
          ? { ...candidate, status: 'syncing' }
          : candidate
      )));
    } catch (err) {
      setOptimisticMessages((current) => current.filter(
        (candidate) => candidate.id !== optimisticMessage.id,
      ));
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
        const next = new URLSearchParams(searchParams);
        next.delete('runId');
        setSearchParams(next, { replace: true });
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
        const next = new URLSearchParams(searchParams);
        next.delete('runId');
        setSearchParams(next, { replace: true });
        setError('This conversation has ended and can no longer be continued. Send your message again to start a new one that remembers this conversation.');
      } else {
        setError(formatApiErrorMessage(err));
      }
    } finally {
      sendingRef.current = false;
      setBusy(false);
    }
  }, [
    busy,
    effectiveProjectId,
    input,
    optimisticMessages,
    baselineReady,
    runId,
    searchParams,
    serverUserMessageCounts,
    setSearchParams,
  ]);

  return (
    <div className={styles.page} data-testid="assistant-run-page">
      <div className={styles.header}>
        <Text className={styles.title}>Agentweaver assistant</Text>
        <Text className={styles.subtitle}>
          Ask the operator assistant to inspect or drive the platform. It routes through the
          Agentweaver MCP tools; anything that changes state asks for your approval first.
        </Text>
      </div>

      <div
        className={styles.transcript}
        data-testid="assistant-transcript"
        ref={transcriptRef}
        onScroll={updateShouldStickToBottom}
      >
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
        {visibleOptimisticMessages.map((pendingMessage) => (
          <div
            className={styles.pendingMessage}
            data-testid="assistant-pending-message"
            key={pendingMessage.id}
          >
            <div className={styles.pendingMessageRow}>
              <Spinner
                size="extra-tiny"
                aria-label={pendingMessage.status === 'sending' ? 'Sending' : 'Syncing'}
              />
              <span className={styles.pendingMessageText}>{pendingMessage.text}</span>
            </div>
            <span className={styles.pendingMessageStatus}>
              {pendingMessage.status === 'sending' ? 'Sending…' : 'Sent · syncing…'}
            </span>
          </div>
        ))}
        {pendingApprovals.length > 0 && (
          <div className={styles.approvals} data-testid="assistant-approvals">
            {pendingApprovals.map((approval) => (
              <AssistantApprovalGate key={`approval-${approval.event.sequence}`} approval={approval} />
            ))}
          </div>
        )}
        <div ref={messagesEndRef} aria-hidden="true" />
      </div>

      <div className={styles.composerStack}>
        {error && (
          <MessageBar intent="error">
            <MessageBarBody data-testid="assistant-error">{error}</MessageBarBody>
          </MessageBar>
        )}
        {runId && (streamStatus === 'error' || seedError || reconciliationError) && (
          <MessageBar intent="warning" data-testid="assistant-reconciliation-warning">
            <MessageBarBody>
              {reconciliationError
                ?? (seedError
                  ? `Saved conversation history could not be refreshed: ${seedError}`
                  : `Live updates disconnected${streamError ? `: ${streamError}` : '.'} Sent messages are reconciled from saved history.`)}
            </MessageBarBody>
            <MessageBarActions>
              <Button
                appearance="transparent"
                size="small"
                onClick={() => void reconcileDurableHistory()}
              >
                Retry sync
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        <Composer
          textareaRef={composerTextareaRef}
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
