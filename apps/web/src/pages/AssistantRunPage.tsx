import { useCallback, useMemo, useState } from 'react';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useParams } from 'react-router-dom';
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
  idleTimeoutNotice: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    maxWidth: '640px',
  },
  approvals: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
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

  // The operator run id is created lazily on the first composer submit; until then the
  // stream stays disabled ('') and the page shows the empty invitation state.
  const [runId, setRunId] = useState<string>('');
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { events, status: streamStatus } = useSeededRunStream(runId, undefined);

  const timelineModel = useMemo(
    () => buildRunTimeline(events, { stripSerializedWorkPlan: false }),
    [events],
  );
  const pendingApprovals = useMemo(
    () => (runId ? derivePendingApprovals(events, runId) : []),
    [events, runId],
  );

  // Detect idle-timeout: the backend emits run.completed {reason:"idle_timeout"} when the
  // operator run is closed due to inactivity. Treat this as a terminal state distinct from
  // a normal completion — the composer stays editable so the user can start a new run.
  const idleTimedOut = useMemo(
    () => events.some(
      (e) => e.type === 'run.completed' &&
        typeof e.payload === 'object' &&
        (e.payload as Record<string, unknown>)['reason'] === 'idle_timeout',
    ),
    [events],
  );

  const handleSubmit = useCallback(async () => {
    const message = input.trim();
    if (!message || busy) return;
    setBusy(true);
    setError(null);
    const isNewRun = !runId;
    try {
      if (isNewRun) {
        const created = await apiClient.createAssistantRun({
          message,
          project_id: effectiveProjectId,
        });
        setRunId(created.run_id);
      } else {
        await apiClient.sendAssistantMessage(runId, { message });
      }
      setInput('');
    } catch (err) {
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
        // The run was idle-closed by the server. Reset to the start state so the user can
        // open a new conversation; the transcript stays visible below the notice.
        setRunId('');
        setError('This conversation timed out. Start a new one below.');
      } else {
        setError(formatApiErrorMessage(err));
      }
    } finally {
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

      <div className={styles.transcript} data-testid="assistant-transcript">
        {!runId && (
          <Text className={styles.emptyState} data-testid="assistant-empty-state">
            Start a conversation below. Your first message opens an operator run and the reply
            streams in here.
          </Text>
        )}
        {runId && (
          <RunTimeline
            embedded
            steps={timelineModel.steps}
            running={timelineModel.running}
            emptyHint="Messages, tool calls, and activity will appear here as the assistant responds."
          />
        )}
        {idleTimedOut && (
          <Text className={styles.idleTimeoutNotice} data-testid="assistant-idle-timeout">
            Conversation ended due to inactivity. Start a new one below.
          </Text>
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
          disabled={busy}
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
