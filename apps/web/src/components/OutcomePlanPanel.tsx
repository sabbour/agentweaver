import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  tokens,
  } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import {
  CheckmarkCircleRegular,
  ChevronLeftRegular,
  DismissCircleRegular,
  EditRegular,
  InfoRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { AgentStepList } from './ui/agentic';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { RunStreamEvent, StreamStatus } from '../api/sse';
import type { OutcomeSpec, OutcomeSpecStatus } from '../api/types';
import { isTerminalRunStatus, normalizeRunStatus } from '../utils/runStatus';
const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    minWidth: 0,
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  headerRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  title: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    flexGrow: 1,
  },
  subtitle: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  statusChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: tokens.fontSizeBase100,
    padding: '2px 8px',
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'nowrap',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
  },
  sectionLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
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
  drafting: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
  },
  actionRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  reviseFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  reviseHint: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  qaList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
});

// Split Open questions into individual items. The coordinator sometimes returns several
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

const OUTCOME_SPEC_POLL_MS = 2_000;
const RUN_FAILURE_STATUSES = new Set(['failed', 'declined', 'merge_failed']);

const STATUS_META: Record<OutcomeSpecStatus, { label: string; tone: 'info' | 'warning' | 'success' | 'danger' }> = {
  drafting: { label: 'Drafting', tone: 'info' },
  awaiting_confirmation: { label: 'Awaiting confirmation', tone: 'warning' },
  confirmed: { label: 'Confirmed', tone: 'success' },
  declined: { label: 'Declined', tone: 'danger' },
};

function apiErrorCode(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  try {
    const body = JSON.parse(err.body) as Record<string, unknown>;
    return typeof body.error === 'string' ? body.error : null;
  } catch {
    if (err.body.includes('no_pending_gate')) return 'no_pending_gate';
    if (err.body.includes('run_not_active')) return 'run_not_active';
    return null;
  }
}

function actionErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    const code = apiErrorCode(err);
    if (err.status === 409 && code === 'no_pending_gate') {
      return 'This run is no longer awaiting outcome-plan confirmation.';
    }
    if (err.status === 409 && code === 'run_not_active') {
      return 'This run is no longer active, so the Outcome plan cannot be confirmed.';
    }
    return `API error ${err.status}: ${err.body}`;
  }
  return err instanceof Error ? err.message : String(err);
}

interface OutcomePlanPanelProps {
  runId: string;
  events: RunStreamEvent[];
  streamStatus: StreamStatus;
  runStatus?: string;
  onCollapse?: () => void;
  onReconnect?: () => void;
  /** Reconcile the parent run snapshot after confirmation changes coordinator state. */
  onConfirmed?: () => void;
  onClarifyPlan?: () => void;
  clarificationSent?: boolean;
  /**
   * When provided, the confirm/clarify action row (and the Independent task promotion
   * field above it) is reported to the caller instead of being rendered inline. This lets a
   * host such as SlidePanel pin it outside a scrollable body so it's always visible without
   * scrolling. Called with `null` whenever the plan isn't awaiting confirmation.
   */
  onFooterChange?: (node: ReactNode) => void;
}

export function OutcomePlanPanel({ runId, events, streamStatus, runStatus, onCollapse, onReconnect, onConfirmed, onClarifyPlan, clarificationSent = false, onFooterChange }: OutcomePlanPanelProps) {
  const styles = useStyles();

  const [specFromApi, setSpecFromApi] = useState<OutcomeSpec | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [reviseOpen, setReviseOpen] = useState(false);
  const [answers, setAnswers] = useState<string[]>([]);
  const [extraFeedback, setExtraFeedback] = useState('');
  const [revising, setRevising] = useState(false);
  // Default to the fully-expanded plan so the goal/scope/assumptions are visible immediately
  // when the panel is opened from the run-wide Goal chip (was previously minimized to just the
  // goal behind a "View full plan" button).
  const [fullPlanOpen, setFullPlanOpen] = useState(true);
  // Snapshot of spec content at the moment a revise request is submitted. Used to detect
  // when the coordinator has finished re-drafting (content changes while revising=true).
  const revisingSnapshotRef = useRef<string | null>(null);
  const [allowTaskPromotion, setAllowTaskPromotion] = useState(false);

  // Tracks whether the most recent getOutcomeSpec call returned 404 (spec not yet created).
  // While true and the coordinator run is still live, we poll until the draft is available.
  const specNotFoundRef = useRef(false);

  // Synchronous in-flight guard for confirm — prevents a second click from firing before
  // React has had a chance to re-render the button as disabled (acting state update is async).
  const confirmInFlightRef = useRef(false);
  const reviseInFlightRef = useRef(false);

  const fetchSpec = useCallback(async () => {
    try {
      const spec = await apiClient.getOutcomeSpec(runId);
      setSpecFromApi(spec);
      setLoadError(null);
      specNotFoundRef.current = false;
    } catch (err) {
      // A 404 before the coordinator drafts is expected — the stream will fill in.
      if (err instanceof ApiError && err.status === 404) {
        setLoadError(null);
        specNotFoundRef.current = true;
      } else {
        setLoadError(err instanceof Error ? err.message : String(err));
      }
    }
  }, [runId]);

  useEffect(() => {
    if (!runId) return;
    specNotFoundRef.current = false; // Reset for each new run.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void fetchSpec();
  }, [runId, fetchSpec]);

  // When the SSE stream closes (server ends it at review gate), refresh the spec from the
  // REST API so the confirmed/awaiting-confirmation state is always current — even when
  // events were missed due to a different API replica serving the stream.
  // If the spec is still returning 404, the polling effect below owns the next retry.
  useEffect(() => {
    if (streamStatus === 'done' && !specNotFoundRef.current) {
      void fetchSpec();
    }
  }, [streamStatus, fetchSpec]);

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

    // REST GET is authoritative for terminal states. After confirm/decline, specFromApi is
    // updated immediately but the confirming SSE event may have been seen already (its
    // sequence ≤ lastSeqRef) and gets filtered on reconnect. Trust the REST snapshot.
    if (!confirmedEvent && (specFromApi?.status === 'confirmed' || specFromApi?.status === 'declined')) {
      merged.status = specFromApi.status;
      if (specFromApi.confirmedBy) merged.confirmedBy = specFromApi.confirmedBy;
    }

    return merged;
  }, [specFromApi, events]);

  const normalizedRunStatus = normalizeRunStatus(runStatus);
  const runTerminal = isTerminalRunStatus(runStatus);

  // A 404 means the coordinator has not drafted the spec yet, not that the gate is absent.
  // Keep the panel visible and poll until REST returns the drafted spec, unless the run ends.
  useEffect(() => {
    if (runTerminal || specFromApi != null || events.some((evt) => evt.type === 'coordinator.outcome_spec')) return;
    let cancelled = false;
    const timer = setInterval(() => {
      if (!cancelled && specNotFoundRef.current) void fetchSpec();
    }, OUTCOME_SPEC_POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [events, fetchSpec, runTerminal, specFromApi]);

  const handleConfirm = async () => {
    // Synchronous guard: blocks re-entrant clicks before React re-renders with acting=true.
    if (confirmInFlightRef.current) return;
    confirmInFlightRef.current = true;
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
          const updated = await apiClient.confirmOutcomeSpec(runId, allowTaskPromotion);
          if (updated) setSpecFromApi(updated);
          else await fetchSpec();
          // The parent derives its header/tree from separate REST snapshots, rather
          // than this panel's spec state. Refresh those snapshots immediately.
          onConfirmed?.();
          // Reconnect the SSE stream so post-confirmation events (outcome_spec.confirmed,
          // coordinator work plan, subtask events) arrive without a manual page refresh.
          onReconnect?.();
          return;
        } catch (err) {
          const isGateArming =
            err instanceof ApiError && err.status === 409 && apiErrorCode(err) === 'no_pending_gate';
          if (!isGateArming || attempt >= maxAttempts) throw err;
          await new Promise((resolve) => setTimeout(resolve, backoffMs));
        }
      }
    } catch (err) {
      setActionError(actionErrorMessage(err));
      if (err instanceof ApiError && err.status === 409) await fetchSpec();
    } finally {
      confirmInFlightRef.current = false;
      setActing(false);
    }
  };

  const handleRevise = async () => {
    const composed = composedFeedback.trim();
    if (!composed || reviseInFlightRef.current) return;
    reviseInFlightRef.current = true;
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
      setActionError(actionErrorMessage(err));
    } finally {
      reviseInFlightRef.current = false;
      setActing(false);
    }
  };

  const status = spec?.status ?? 'drafting';
  const statusMeta = STATUS_META[status] ?? STATUS_META.drafting;
  const awaiting = status === 'awaiting_confirmation';
  const runInterrupted = actionError?.includes('no longer active') ?? false;
  const revisionPending = revising || clarificationSent;

  const hasContent = spec != null && (spec.goal || spec.desiredOutcome || toLines(spec.scope).length > 0 || toLines(spec.assumptions).length > 0);
  const failedBeforeDraft = !hasContent && !revising && normalizedRunStatus !== '' && RUN_FAILURE_STATUSES.has(normalizedRunStatus);
  const terminalBeforeDraft = !hasContent && !revising && runTerminal && !failedBeforeDraft;
  // The "zombie plan" bug this guards against: a plan was successfully drafted
  // (hasContent) and is still sitting at awaiting_confirmation, but the run itself has
  // since ended (crashed, declined, merge-failed, or otherwise gone terminal) — the
  // OutcomeSpec row's own status field never flips when the run dies, so without this
  // check the panel would show a live, confirmable plan for a run that can no longer
  // accept it (the user only found out via a jarring 409 after clicking Confirm).
  const confirmDeadAfterDraft = hasContent && runTerminal && status === 'awaiting_confirmation';
  const clarifying = useMemo(() => splitQuestions(toLines(spec?.clarifyingQuestions)), [spec?.clarifyingQuestions]);

  // Compose the revise feedback from the per-question answers plus any free-form feedback.
  // Each answered question becomes a "Q: …\nA: …" block the coordinator re-drafts from.
  const composedFeedback = (() => {
    const qa = clarifying
      .map((q, i) => ({ q, a: (answers[i] ?? '').trim() }))
      .filter((x) => x.a.length > 0)
      .map((x) => `Q: ${x.q}\nA: ${x.a}`)
      .join('\n\n');
    return [qa, extraFeedback.trim()].filter((s) => s.length > 0).join('\n\n');
  })();

  useEffect(() => {
    const syncAllowTaskPromotion = async () => {
      setAllowTaskPromotion(spec?.allowTaskPromotion ?? false);
    };
    void syncAllowTaskPromotion();
  }, [spec?.allowTaskPromotion, spec?.status]);

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
    if (onClarifyPlan) {
      onClarifyPlan();
      return;
    }
    setAnswers(clarifying.map(() => ''));
    setExtraFeedback('');
    setReviseOpen(true);
  };

  // The confirm/clarify action row, plus the Independent task promotion field above it.
  // Rendered inline by default, but when a host supplies `onFooterChange` (e.g. SlidePanel
  // pinning it outside a scrollable body) it's reported via that callback instead so it stays
  // visible without scrolling.
  const footerContent = awaiting ? (
    <>
      <Field
        label="Independent task promotion"
        hint="Optional: allow the coordinator to split genuinely separate deliverables into standalone backlog tasks. Leave off to keep all work inline in this run."
      >
        <Checkbox
          checked={allowTaskPromotion}
          disabled={acting || revisionPending || runInterrupted || runTerminal}
          label="Allow standalone backlog tasks for independent deliverables"
          onChange={(_, data) => setAllowTaskPromotion(Boolean(data.checked))}
        />
      </Field>
      <div role="group" className={styles.actionRow}>
        <Button
          appearance="primary"
          icon={<CheckmarkCircleRegular />}
          disabled={acting || revisionPending || runInterrupted || runTerminal}
          onClick={() => void handleConfirm()}
        >
          {acting ? 'Confirming plan...' : 'Confirm plan'}
        </Button>
        <Button
          appearance="secondary"
          icon={<EditRegular />}
          disabled={acting || revisionPending || runInterrupted || runTerminal}
          onClick={openRevise}
        >
          Clarify plan
        </Button>
      </div>
    </>
  ) : null;

  useEffect(() => {
    if (!onFooterChange) return;
    onFooterChange(footerContent);
    return () => onFooterChange(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [onFooterChange, awaiting, allowTaskPromotion, acting, revisionPending, runInterrupted, runTerminal]);

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <div className={styles.headerRow}>
          <Text className={styles.title}>Outcome plan</Text>
          <span className={styles.statusChip}>{statusMeta.label}</span>
          {streamStatus === 'connecting' && <Spinner size="extra-tiny" aria-label="Loading" />}
          {onCollapse && (
            <Button
              appearance="subtle"
              icon={<ChevronLeftRegular />}
              aria-label="Collapse Outcome plan"
              onClick={onCollapse}
            />
          )}
        </div>
        <Text className={styles.subtitle}>
          Review and confirm the proposed goal, scope, assumptions, and questions.
        </Text>
      </div>

      {/* Dispatch gate — make the safety property explicit (US1 / FR-008). Suppressed once
          the run itself has gone terminal: telling the user to "confirm or clarify" a plan
          whose run can no longer accept either action would contradict the dead-run banner
          rendered further down. */}
      {(status === 'drafting' || status === 'awaiting_confirmation') && !runTerminal && (
        <MessageBar intent="info" icon={<LockClosedRegular />}>
          <MessageBarBody>
            The coordinator translated your goal into a proposed outcome, scope, assumptions, and open questions.
            {' '}Confirm it to dispatch work, or clarify what should change.
          </MessageBarBody>
        </MessageBar>
      )}
      {status === 'confirmed' && (
        <MessageBar intent="success" icon={<CheckmarkCircleRegular />}>
          <MessageBarBody>
            Outcome plan confirmed{spec?.confirmedBy ? ` by ${spec.confirmedBy}` : ''}. Dispatch is unblocked.
          </MessageBarBody>
        </MessageBar>
      )}
      {status === 'declined' && (
        <MessageBar intent="warning" icon={<DismissCircleRegular />}>
          <MessageBarBody>Outcome plan declined. No subagent work was dispatched.</MessageBarBody>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      )}

      {revisionPending && (
        <span className={styles.drafting} role="status" aria-live="polite" aria-atomic="true">
          <Spinner size="extra-tiny" aria-hidden="true" />
          <InfoRegular aria-hidden="true" />
          <Text>Clarification sent — the coordinator is revising the Outcome plan.</Text>
        </span>
      )}

      {failedBeforeDraft ? (
        <MessageBar intent="error">
          <MessageBarBody>The run failed before the Outcome plan could be drafted.</MessageBarBody>
        </MessageBar>
      ) : terminalBeforeDraft ? (
        <MessageBar intent="info">
          <MessageBarBody>The run finished before the Outcome plan could be drafted.</MessageBarBody>
        </MessageBar>
      ) : !hasContent && !revisionPending ? (
        <AgentStepList
          steps={[{
            id: 'drafting-outcome-plan',
            title: 'Drafting the Outcome plan...',
            status: 'running',
          }]}
        />
      ) : hasContent && status === 'confirmed' && !fullPlanOpen ? (
        <>
          <SpecSection label="Goal" value={spec?.goal} />
          <div role="group" className={styles.actionRow}>
            <Button appearance="secondary" onClick={() => setFullPlanOpen(true)}>View full plan</Button>
          </div>
        </>
      ) : hasContent ? (
        <>
          <SpecSection label="Goal" value={spec?.goal} />
          <SpecSection label="Outcome" value={spec?.desiredOutcome} />
          <SpecSection label="Scope" value={spec?.scope} />
          <SpecSection label="Assumptions" value={spec?.assumptions} />
          {clarifying.length > 0 && (
            <SpecSection label="Open questions" value={spec?.clarifyingQuestions} />
          )}
        </>
      ) : null}

      {actionError && !reviseOpen && (
        <MessageBar intent="error">
          <MessageBarBody>{actionError}</MessageBarBody>
        </MessageBar>
      )}

      {confirmDeadAfterDraft && (
        <MessageBar intent="warning" icon={<DismissCircleRegular />} data-testid="outcome-plan-dead-run-banner">
          <MessageBarBody>
            This run {RUN_FAILURE_STATUSES.has(normalizedRunStatus) ? 'failed' : 'ended'} after this
            Outcome plan was drafted, so it can no longer be confirmed. The plan below is kept for
            reference only.
          </MessageBarBody>
        </MessageBar>
      )}

      {!onFooterChange && footerContent}


      <Dialog open={reviseOpen} onOpenChange={(_, d) => { setReviseOpen(d.open); if (!d.open) { setAnswers([]); setExtraFeedback(''); } }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Clarify plan</DialogTitle>
            <DialogContent>
              <div className={styles.reviseFields}>
                <Text>
                  Describe what to change. After you send, the coordinator re-drafts and
                  re-presents the plan for your confirmation; no subagent work is dispatched
                  until you confirm.
                </Text>
                {clarifying.length > 0 && (
                  <div className={styles.section}>
                    <Text className={styles.sectionLabel}>Open questions</Text>
                    <Text className={styles.reviseHint}>
                      Answer any that apply — your answers refine the plan.
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
                {actionError && (
                  <MessageBar intent="error">
                    <MessageBarBody>{actionError}</MessageBarBody>
                  </MessageBar>
                )}
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

    </div>
  );
}
