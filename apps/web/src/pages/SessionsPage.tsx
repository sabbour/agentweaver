import { useEffect, useState } from 'react';
import {
  Button,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { Navigate, useSearchParams, useNavigate } from 'react-router-dom';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { AssistantRunSummary } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { ErrorState } from '../components/ui';
import { resolveAssistantFlag } from '../utils/assistantFlag';

// Sessions — a simple list of the current user's assistant conversations (#4/#5,
// replaces the old "Operator dock" nav entry). Data comes from Tank's caller-scoped
// GET /api/assistant/runs (see .squad/decisions/inbox/tank-assistant-approval-sink.md).
// Mirrors the existing OrchestrationsPage list pattern, kept intentionally plain per
// the brief (a list/table is fine — no pagination or filtering needed yet).

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXL,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    textAlign: 'left',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    cursor: 'pointer',
    width: '100%',
  },
  rowTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  rowMeta: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  loadingState: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalXL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
});

function formatError(err: unknown): string {
  return err instanceof ApiError
    ? `API error ${err.status}: ${err.body}`
    : err instanceof Error
      ? err.message
      : String(err);
}

function formatCreatedAt(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

export function SessionsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  // Sessions is still behind the same `?assistant=1` flag as the Assistant page itself
  // (see routes/AssistantRoute.tsx / utils/assistantFlag.ts) — don't fully expose it
  // to all users until that page has proven out.
  const assistantEnabled = resolveAssistantFlag(searchParams.get('assistant'));
  const [runs, setRuns] = useState<AssistantRunSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = () => {
    setLoading(true);
    return apiClient.listAssistantRuns(50)
      .then((res) => {
        setRuns(res.runs);
        setError(null);
      })
      .catch((err) => setError(formatError(err)))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    if (!assistantEnabled) return;
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assistantEnabled]);

  if (!assistantEnabled) return <Navigate to="/" replace />;

  // Reuses the same `?assistant=1&runId=...` shape AssistantRunPage/AssistantRoute
  // already read (see routes/AssistantRoute.tsx), so opening a session resumes that
  // conversation and starting fresh drops runId entirely.
  const openRun = (runId: string) => navigate(`/assistant?assistant=1&runId=${encodeURIComponent(runId)}`);
  const startNew = () => navigate('/assistant?assistant=1');

  return (
    <div className={styles.root} data-testid="sessions-page">
      <PageHeader
        title="Sessions"
        subtitle="Your assistant conversations. Resume one, or start a new one."
        actions={(
          <Button
            appearance="primary"
            icon={<AddRegular />}
            onClick={startNew}
            data-testid="sessions-new-button"
          >
            New session
          </Button>
        )}
      />

      {loading && (
        <div className={styles.loadingState} data-testid="sessions-loading">
          <Spinner size="small" />
          <Text>Loading sessions…</Text>
        </div>
      )}

      {!loading && error && (
        <ErrorState title="Couldn't load sessions" message={error} onRetry={load} />
      )}

      {!loading && !error && runs.length === 0 && (
        <div className={styles.emptyState} data-testid="sessions-empty-state">
          <Text>No assistant conversations yet.</Text>
          <Button appearance="primary" icon={<AddRegular />} onClick={startNew}>
            Start your first session
          </Button>
        </div>
      )}

      {!loading && !error && runs.length > 0 && (
        <div className={styles.list} data-testid="sessions-list">
          {runs.map((run) => (
            <button
              key={run.run_id}
              type="button"
              className={styles.row}
              data-testid="sessions-row"
              onClick={() => openRun(run.run_id)}
            >
              <Text className={styles.rowTitle}>{run.title?.trim() || 'Untitled conversation'}</Text>
              <Text className={styles.rowMeta}>{`${run.status} \u00b7 ${formatCreatedAt(run.created_at)}`}</Text>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
