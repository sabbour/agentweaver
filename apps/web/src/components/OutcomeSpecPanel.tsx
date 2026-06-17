import { useCallback, useEffect, useMemo, useState } from 'react';
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
  CheckmarkCircleRegular,
  DismissCircleRegular,
  EditRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { RunStreamEvent, StreamStatus } from '../api/sse';
import type { OutcomeSpec, OutcomeSpecStatus } from '../api/types';

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
});

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

const STATUS_META: Record<OutcomeSpecStatus, { label: string; color: 'informative' | 'warning' | 'success' | 'danger' }> = {
  drafting: { label: 'Drafting', color: 'informative' },
  awaiting_confirmation: { label: 'Awaiting confirmation', color: 'warning' },
  confirmed: { label: 'Confirmed', color: 'success' },
  declined: { label: 'Declined', color: 'danger' },
};

interface OutcomeSpecPanelProps {
  runId: string;
  events: RunStreamEvent[];
  streamStatus: StreamStatus;
}

export function OutcomeSpecPanel({ runId, events, streamStatus }: OutcomeSpecPanelProps) {
  const styles = useStyles();

  const [specFromApi, setSpecFromApi] = useState<OutcomeSpec | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [reviseOpen, setReviseOpen] = useState(false);
  const [feedback, setFeedback] = useState('');
  const [revising, setRevising] = useState(false);

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
    try {
      const updated = await apiClient.confirmOutcomeSpec(runId);
      if (updated) setSpecFromApi(updated);
      else await fetchSpec();
    } catch (err) {
      setActionError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setActing(false);
    }
  };

  const handleRevise = async () => {
    if (!feedback.trim()) return;
    setActing(true);
    setActionError(null);
    try {
      const updated = await apiClient.reviseOutcomeSpec(runId, feedback.trim());
      if (updated) setSpecFromApi(updated);
      else await fetchSpec();
      setRevising(true);
      setReviseOpen(false);
      setFeedback('');
    } catch (err) {
      setActionError(err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err));
    } finally {
      setActing(false);
    }
  };

  const status = spec?.status ?? 'drafting';
  const statusMeta = STATUS_META[status] ?? STATUS_META.drafting;
  const awaiting = status === 'awaiting_confirmation';
  const hasContent = spec != null && (spec.goal || spec.desiredOutcome || toLines(spec.scope).length > 0 || toLines(spec.assumptions).length > 0);
  const clarifying = toLines(spec?.clarifyingQuestions);

  // Once a freshly re-drafted spec returns to awaiting_confirmation, clear the
  // "incorporating your changes" state so the panel reflects the new spec.
  useEffect(() => {
    if (status === 'awaiting_confirmation') setRevising(false);
  }, [status]);

  // Open the revise dialog, seeding a Q/A template from the clarifying questions
  // when the user has not already typed feedback. Answering the questions IS the
  // revise feedback the coordinator re-drafts from.
  const openRevise = () => {
    if (!feedback.trim() && clarifying.length > 0) {
      setFeedback(clarifying.map((q) => `Q: ${q}\nA: `).join('\n\n'));
    }
    setReviseOpen(true);
  };

  return (
    <div className={styles.panel}>
      <div className={styles.headerRow}>
        <Title3>Outcome spec</Title3>
        <Badge appearance="tint" color={statusMeta.color}>{statusMeta.label}</Badge>
        <div className={styles.spacer} />
        {streamStatus === 'connecting' && <Spinner size="extra-tiny" aria-label="Connecting" />}
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

      {!hasContent ? (
        <div className={styles.drafting}>
          <Spinner size="extra-tiny" aria-hidden="true" />
          <Text>
            {revising
              ? 'Coordinator is incorporating your changes and re-drafting the spec...'
              : 'Coordinator is drafting the outcome spec...'}
          </Text>
        </div>
      ) : (
        <>
          <SpecSection label="Goal" value={spec?.goal} />
          <SpecSection label="Desired outcome" value={spec?.desiredOutcome} />
          <SpecSection label="Scope" value={spec?.scope} />
          <SpecSection label="Assumptions" value={spec?.assumptions} />
          {clarifying.length > 0 && (
            <SpecSection label="Clarifying questions" value={spec?.clarifyingQuestions} />
          )}
        </>
      )}

      {actionError && (
        <MessageBar intent="error">
          <MessageBarBody>{actionError}</MessageBarBody>
        </MessageBar>
      )}

      {awaiting && (
        <div className={styles.actions}>
          <Button
            appearance="primary"
            icon={<CheckmarkCircleRegular />}
            disabled={acting}
            onClick={() => void handleConfirm()}
          >
            {acting ? 'Confirming' : 'Confirm'}
          </Button>
          <Button
            appearance="secondary"
            icon={<EditRegular />}
            disabled={acting}
            onClick={openRevise}
          >
            Clarify and request changes
          </Button>
          {acting && <Spinner size="extra-tiny" aria-hidden="true" />}
        </div>
      )}

      <Dialog open={reviseOpen} onOpenChange={(_, d) => { setReviseOpen(d.open); if (!d.open) setFeedback(''); }}>
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
                      Answer these to refine the spec, or edit the feedback freely.
                    </Text>
                    <ul className={styles.list}>
                      {clarifying.map((q, i) => (
                        <li key={i} className={styles.listItem}>{q}</li>
                      ))}
                    </ul>
                  </div>
                )}
                <Field label="Feedback" required>
                  <Textarea
                    value={feedback}
                    onChange={(_, v) => setFeedback(v.value)}
                    placeholder="e.g. Narrow the scope to the API only; assume Postgres, not MySQL."
                    rows={4}
                  />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={acting} onClick={() => { setReviseOpen(false); setFeedback(''); }}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={!feedback.trim() || acting}
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
