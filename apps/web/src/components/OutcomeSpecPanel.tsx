import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  AppsListDetailRegular,
  CheckmarkCircleRegular,
  ChevronLeftRegular,
  DismissCircleRegular,
  EditRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { RunStreamEvent, StreamStatus } from '../api/sse';
import type { OutcomeSpec, OutcomeSpecStatus, ProposedBacklogItem } from '../api/types';
import { DecomposePreviewDialog } from './DecomposePreviewDialog';

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  spacer: { flex: 1 },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  sectionLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  body: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    whiteSpace: 'pre-wrap',
  },
  list: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalL,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  listItem: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  empty: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  drafting: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
  },
  reviseFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  reviseHint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  qaList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

// Split clarifying questions into individual items. The coordinator sometimes returns several
// questions crammed into one string as an inline numbered list ("1. ... 2. ..."); break those
// apart so each question can be answered on its own. Leading "N." / "N)" prefixes are stripped.
function splitQuestions(lines: string[]): string[] {
  const out: string[] = [];
  for (const line of lines) {
    const matches = line.match(/\d+\.\s+[\s\S]*?(?=\s+\d+\.\s+|$)/g);
    if (matches && matches.length > 1) {
      for (const m of matches) out.push(m.trim());
    } else {
      out.push(line.trim());
    }
  }
  return out.map((q) => q.replace(/^\d+[.)]\s*/, '').trim()).filter((q) => q.length > 0);
}

// Render a value that may be a single string or a list of strings.
function toLines(value?: string | string[]): string[] {
  if (value == null) return [];
  if (Array.isArray(value)) {
    return value.map((v) => String(v).trim()).filter((v) => v.length > 0);
  }
  const s = String(value).trim();
  return s.length > 0 ? [s] : [];
}

function SpecSection({ label, value }: { label: string; value?: string | string[] }) {
  const styles = useStyles();
  const lines = toLines(value);
  return (
    <div className={styles.section}>
      <Text className={styles.sectionLabel}>{label}</Text>
      {lines.length === 0 ? (
        <Text className={styles.empty}>Not specified yet.</Text>
      ) : lines.length === 1 ? (
        <Text className={styles.body}>{lines[0]}</Text>
      ) : (
        <ul className={styles.list}>
          {lines.map((line, i) => (
            <li key={i} className={styles.listItem}>{line}</li>
          ))}
        </ul>
      )}
    </div>
  );
}

const RUN_NOT_ACTIVE_MESSAGES: Record<string, string> = {
  agent_quota_exceeded: 'The cluster ran out of capacity to start your agent. The system is retrying — please wait a moment before trying again.',
  agent_stall_timeout: 'The agent pod took too long to respond. This may be a transient issue — please start a new task.',
  agent_pod_reconciler_error: 'The agent pod failed to start due to a configuration error. Please contact support if this persists.',
  capacity_unavailable: 'No agent capacity is available after multiple retries. Please try again later.',
};
const DEFAULT_INTERRUPTED_MESSAGE = 'This run was interrupted (server restart or timeout) and can no longer be confirmed. Please start a new task.';

const STATUS_META: Record<OutcomeSpecStatus, { label: string; color: 'informative' | 'warning' | 'success' | 'danger' }> = {
  drafting: { label: 'Drafting', color: 'informative' },
  awaiting_confirmation: { label: 'Awaiting confirmation', color: 'warning' },
  confirmed: { label: 'Confirmed', color: 'success' },
  declined: { label: 'Declined', color: 'danger' },
};

interface OutcomeSpecPanelProps {
  runId: string;
  projectId?: string;
  events: RunStreamEvent[];
  streamStatus: StreamStatus;
  onCollapse?: () => void;
}

export function OutcomeSpecPanel({ runId, projectId, events, streamStatus, onCollapse }: OutcomeSpecPanelProps) {
  const styles = useStyles();

  const [specFromApi, setSpecFromApi] = useState<OutcomeSpec | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [reviseOpen, setReviseOpen] = useState(false);
  const [answers, setAnswers] = useState<string[]>([]);
  const [extraFeedback, setExtraFeedback] = useState('');
  const [revising, setRevising] = useState(false);
  // Snapshot of spec content at the moment a revise request is submitted. Used to detect
  // when the coordinator has finished re-drafting (content changes while revising=true).
  const revisingSnapshotRef = useRef<string | null>(null);

  // Decompose / "Break into tasks" state
  const [decomposePreviewOpen, setDecomposePreviewOpen] = useState(false);
  const [decomposeItems, setDecomposeItems] = useState<ProposedBacklogItem[]>([]);
  const [decomposeWasCapped, setDecomposeWasCapped] = useState(false);
  const [decomposeTotal, setDecomposeTotal] = useState(0);
  const [decomposeLoading, setDecomposeLoading] = useState(false);
  const [decomposeError, setDecomposeError] = useState<string | null>(null);
  const [decomposeSuccess, setDecomposeSuccess] = useState(false);

  const fetchSpec = useCallback(async () => {
    try {
      const spec = await apiClient.getOutcomeSpec(runId);
      setSpecFromApi(spec);
      setLoadError(null);
    } catch (err) {
      // A 404 before the coordinator drafts is expected — the stream will fill in.
      if (err instanceof ApiError && err.status === 404) {
        setLoadError(null);
      } else {
        setLoadError(err instanceof Error ? err.message : String(err));
      }
    }
  }, [runId]);

  useEffect(() => {
    if (!runId) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void fetchSpec();
  }, [runId, fetchSpec]);

  // Derive the live spec: the event stream (ordered/deduped by sequence) is the
  // authoritative live source; the GET snapshot seeds fields an event may omit
  // (e.g. goal, clarifyingQuestions, confirmedBy). Thin client — no spec logic here.
  const spec = useMemo<OutcomeSpec | null>(() => {
    let latestSpecEvent: RunStreamEvent | undefined;
    let confirmedEvent: RunStreamEvent | undefined;
    for (const evt of events) {
      if (evt.type === 'coordinator.outcome_spec') {
        if (!latestSpecEvent || evt.sequence >= latestSpecEvent.sequence) latestSpecEvent = evt;
      } else if (evt.type === 'coordinator.outcome_spec.confirmed') {
        if (!confirmedEvent || evt.sequence >= confirmedEvent.sequence) confirmedEvent = evt;
      }
    }

    if (!specFromApi && !latestSpecEvent && !confirmedEvent) return null;

    const merged: OutcomeSpec = { status: 'drafting', ...(specFromApi ?? {}) };

    if (latestSpecEvent) {
      const p = latestSpecEvent.payload;
      if (typeof p['goal'] === 'string') merged.goal = p['goal'] as string;
      if (typeof p['desiredOutcome'] === 'string') merged.desiredOutcome = p['desiredOutcome'] as string;
      if (p['scope'] !== undefined) merged.scope = p['scope'] as string | string[];
      if (p['assumptions'] !== undefined) merged.assumptions = p['assumptions'] as string | string[];
      if (p['clarifyingQuestions'] !== undefined) merged.clarifyingQuestions = p['clarifyingQuestions'] as string[];
      if (typeof p['status'] === 'string') merged.status = p['status'] as OutcomeSpecStatus;
      if (typeof p['confirmedBy'] === 'string') merged.confirmedBy = p['confirmedBy'] as string;
    }

    if (confirmedEvent) {
      merged.status = 'confirmed';
      if (typeof confirmedEvent.payload['confirmedBy'] === 'string') {
        merged.confirmedBy = confirmedEvent.payload['confirmedBy'] as string;
      }
    }

    return merged;
  }, [specFromApi, events]);

  const handleConfirm = async () => {
    setActing(true);
    setActionError(null);
    // Defense-in-depth for the gate-arming race: after a revise re-draft, the spec can be
    // emitted as `awaiting_confirmation` (enabling this button) a moment before the backend
    // re-arms its in-memory confirmation gate. A fast Confirm click in that window gets a
    // 409 `no_pending_gate`. Auto-retry only that case a few times; surface everything else.
    const maxAttempts = 5;
    const backoffMs = 400;
    try {
      for (let attempt = 1; ; attempt++) {
        try {
          const updated = await apiClient.confirmOutcomeSpec(runId);
          if (updated) setSpecFromApi(updated);
          else await fetchSpec();
          return;
        } catch (err) {
          const isGateArming =
            err instanceof ApiError && err.status === 409 && err.body.includes('no_pending_gate');
          if (!isGateArming || attempt >= maxAttempts) throw err;
          await new Promise((resolve) => setTimeout(resolve, backoffMs));
        }
      }
    } catch (err) {
      setActionError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setActing(false);
    }
  };

  const handleRevise = async () => {
    const composed = composedFeedback.trim();
    if (!composed) return;
    setActing(true);
    setActionError(null);
    try {
      const updated = await apiClient.reviseOutcomeSpec(runId, composed);
      if (updated) setSpecFromApi(updated);
      else await fetchSpec();
      revisingSnapshotRef.current = JSON.stringify({ goal: spec?.goal, desiredOutcome: spec?.desiredOutcome });
      setRevising(true);
      setReviseOpen(false);
      setAnswers([]);
      setExtraFeedback('');
    } catch (err) {
      setActionError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setActing(false);
    }
  };

  const status = spec?.status ?? 'drafting';
  const statusMeta = STATUS_META[status] ?? STATUS_META.drafting;
  const awaiting = status === 'awaiting_confirmation';
  const runInterrupted = actionError?.includes('run_not_active') ?? false;

  // Map run_not_active detail codes to human-readable messages.
  const runInterruptedMessage = useMemo(() => {
    if (!runInterrupted || !actionError) return DEFAULT_INTERRUPTED_MESSAGE;
    try {
      const jsonStart = actionError.indexOf('{');
      if (jsonStart >= 0) {
        const body = JSON.parse(actionError.slice(jsonStart)) as Record<string, unknown>;
        const detail = typeof body.detail === 'string' ? body.detail : '';
        return RUN_NOT_ACTIVE_MESSAGES[detail] ?? DEFAULT_INTERRUPTED_MESSAGE;
      }
    } catch { /* body is not JSON */ }
    return DEFAULT_INTERRUPTED_MESSAGE;
  }, [runInterrupted, actionError]);
  const hasContent = spec != null && (spec.goal || spec.desiredOutcome || toLines(spec.scope).length > 0 || toLines(spec.assumptions).length > 0);
  const clarifying = useMemo(() => splitQuestions(toLines(spec?.clarifyingQuestions)), [spec?.clarifyingQuestions]);

  // Compose the revise feedback from the per-question answers plus any free-form feedback.
  // Each answered question becomes a "Q: …\nA: …" block the coordinator re-drafts from.
  const composedFeedback = useMemo(() => {
    const qa = clarifying
      .map((q, i) => ({ q, a: (answers[i] ?? '').trim() }))
      .filter((x) => x.a.length > 0)
      .map((x) => `Q: ${x.q}\nA: ${x.a}`)
      .join('\n\n');
    return [qa, extraFeedback.trim()].filter((s) => s.length > 0).join('\n\n');
  }, [clarifying, answers, extraFeedback]);

  // Clear the revising spinner when the spec content changes — this fires when the coordinator
  // finishes re-drafting, even when the status stays `awaiting_confirmation` throughout
  // (i.e. the backend never transitions through `drafting`). The snapshot was captured at the
  // moment the revise request was sent, so any content change signals a fresh draft.
  const specContentKey = JSON.stringify({ goal: spec?.goal, desiredOutcome: spec?.desiredOutcome });
  useEffect(() => {
    if (!revising || revisingSnapshotRef.current === null) return;
    if (specContentKey !== revisingSnapshotRef.current) {
      setRevising(false);
      revisingSnapshotRef.current = null;
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revising, specContentKey]);

  // 30-second safety-net: clear the spinner even if the content hasn't changed (e.g. the
  // coordinator re-drafted an identical spec, or the SSE stream didn't deliver new events).
  useEffect(() => {
    if (!revising) return;
    const timer = setTimeout(() => {
      setRevising(false);
      revisingSnapshotRef.current = null;
    }, 30_000);
    return () => clearTimeout(timer);
  }, [revising]);

  // Open the revise dialog with one empty answer slot per clarifying question.
  // Answering the questions IS the revise feedback the coordinator re-drafts from.
  const openRevise = () => {
    setAnswers(clarifying.map(() => ''));
    setExtraFeedback('');
    setReviseOpen(true);
  };

  const handleBreakIntoTasks = async () => {
    if (!projectId) return;
    setDecomposeLoading(true);
    setDecomposeError(null);
    setDecomposeItems([]);
    setDecomposeSuccess(false);
    setDecomposePreviewOpen(true);
    try {
      const result = await apiClient.decomposeSpec(projectId, null, false, runId);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  const handleDecomposeConfirm = async () => {
    if (!projectId) return;
    setDecomposeLoading(true);
    setDecomposeError(null);
    try {
      const result = await apiClient.decomposeSpec(projectId, null, true, runId);
      setDecomposeItems(result.proposed_items);
      setDecomposeWasCapped(result.was_capped);
      setDecomposeTotal(result.total_found);
      setDecomposePreviewOpen(false);
      setDecomposeSuccess(true);
    } catch (err) {
      setDecomposeError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setDecomposeLoading(false);
    }
  };

  return (
    <div className={styles.panel}>
      <div className={styles.headerRow}>
        <Title3>Outcome spec</Title3>
        <Badge appearance="tint" color={statusMeta.color}>{statusMeta.label}</Badge>
        <div className={styles.spacer} />
        {streamStatus === 'connecting' && <Spinner size="extra-tiny" aria-label="Connecting" />}
        {onCollapse && (
          <Button
            appearance="subtle"
            size="small"
            icon={<ChevronLeftRegular />}
            aria-label="Collapse outcome spec"
            onClick={onCollapse}
          />
        )}
      </div>

      {/* Dispatch gate — make the safety property explicit (US1 / FR-008) */}
      {(status === 'drafting' || status === 'awaiting_confirmation') && (
        <MessageBar intent="info" icon={<LockClosedRegular />}>
          <MessageBarBody>
            No subagent work is dispatched until you confirm this outcome spec.
          </MessageBarBody>
        </MessageBar>
      )}
      {status === 'confirmed' && (
        <MessageBar intent="success" icon={<CheckmarkCircleRegular />}>
          <MessageBarBody>
            Outcome spec confirmed{spec?.confirmedBy ? ` by ${spec.confirmedBy}` : ''}. Dispatch is unblocked.
          </MessageBarBody>
        </MessageBar>
      )}
      {status === 'declined' && (
        <MessageBar intent="warning" icon={<DismissCircleRegular />}>
          <MessageBarBody>Outcome spec declined. No subagent work was dispatched.</MessageBarBody>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      )}

      {revising && (
        <div className={styles.drafting}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text>Coordinator is incorporating your changes and re-drafting the spec...</Text>
        </div>
      )}

      {!hasContent && !revising ? (
        <div className={styles.drafting}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text>Coordinator is drafting the outcome spec...</Text>
        </div>
      ) : hasContent ? (
        <>
          <SpecSection label="Goal" value={spec?.goal} />
          <SpecSection label="Desired outcome" value={spec?.desiredOutcome} />
          <SpecSection label="Scope" value={spec?.scope} />
          <SpecSection label="Assumptions" value={spec?.assumptions} />
          {clarifying.length > 0 && (
            <SpecSection label="Clarifying questions" value={spec?.clarifyingQuestions} />
          )}
        </>
      ) : null}

      {actionError && (
        <MessageBar intent="error">
          <MessageBarBody>
            {runInterrupted
              ? runInterruptedMessage
              : actionError}
          </MessageBarBody>
        </MessageBar>
      )}

      {decomposeSuccess && (
        <MessageBar intent="success">
          <MessageBarBody>Tasks created successfully.</MessageBarBody>
        </MessageBar>
      )}

      {awaiting && (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<CheckmarkCircleRegular />}
            disabled={acting || revising || runInterrupted}
            onClick={() => void handleConfirm()}
          >
            {acting ? 'Confirming' : 'Confirm'}
          </Button>
          <Button
            appearance="secondary"
            icon={<EditRegular />}
            disabled={acting || revising || runInterrupted}
            onClick={openRevise}
          >
            Clarify and request changes
          </Button>
          {acting && <Spinner size="extra-tiny" aria-hidden="true" />}
        </div>
      )}

      {status === 'confirmed' && projectId && (
        <div className={styles.actions}>
          <Button
            appearance="secondary"
            icon={<AppsListDetailRegular />}
            onClick={() => void handleBreakIntoTasks()}
          >
            Break into tasks
          </Button>
        </div>
      )}

      <Dialog open={reviseOpen} onOpenChange={(_, d) => { setReviseOpen(d.open); if (!d.open) { setAnswers([]); setExtraFeedback(''); } }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Clarify and request changes</DialogTitle>
            <DialogContent>
              <div className={styles.reviseFields}>
                <Text>
                  Describe what to change. After you send, the coordinator re-drafts and
                  re-presents the spec for your confirmation; no subagent work is dispatched
                  until you confirm.
                </Text>
                {clarifying.length > 0 && (
                  <div className={styles.section}>
                    <Text className={styles.sectionLabel}>Clarifying questions</Text>
                    <Text className={styles.reviseHint}>
                      Answer any that apply — your answers refine the spec.
                    </Text>
                    <div className={styles.qaList}>
                      {clarifying.map((q, i) => (
                        <Field key={i} label={`${i + 1}. ${q}`}>
                          <Textarea
                            value={answers[i] ?? ''}
                            onChange={(_, v) => setAnswers((prev) => {
                              const next = prev.length === clarifying.length ? [...prev] : clarifying.map((_, j) => prev[j] ?? '');
                              next[i] = v.value;
                              return next;
                            })}
                            placeholder="Your answer…"
                            rows={2}
                          />
                        </Field>
                      ))}
                    </div>
                  </div>
                )}
                <Field
                  label={clarifying.length > 0 ? 'Additional feedback' : 'Feedback'}
                  required={clarifying.length === 0}
                >
                  <Textarea
                    value={extraFeedback}
                    onChange={(_, v) => setExtraFeedback(v.value)}
                    placeholder="e.g. Narrow the scope to the API only; assume Postgres, not MySQL."
                    rows={3}
                  />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={acting} onClick={() => { setReviseOpen(false); setAnswers([]); setExtraFeedback(''); }}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={!composedFeedback.trim() || acting}
                onClick={() => void handleRevise()}
              >
                {acting ? 'Sending' : 'Send'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <DecomposePreviewDialog
        isOpen={decomposePreviewOpen}
        onClose={() => { setDecomposePreviewOpen(false); setDecomposeError(null); }}
        onConfirm={handleDecomposeConfirm}
        proposedItems={decomposeItems}
        wasCapped={decomposeWasCapped}
        totalFound={decomposeTotal}
        isLoading={decomposeLoading}
        error={decomposeError}
      />
    </div>
  );
}
