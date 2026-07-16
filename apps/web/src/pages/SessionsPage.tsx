import { useEffect, useMemo, useState } from 'react';
import {
  Button,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { AssistantRunSummary } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { ErrorState } from '../components/ui';

// Sessions — a simple global list of the current user's assistant conversations.
// Data comes from Tank's caller-scoped GET /api/assistant/runs, so the page now
// intentionally spans all projects rather than being tied to /projects/:projectId.

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
  const projectId = searchParams.get('project') ?? undefined;
  const [runs, setRuns] = useState<AssistantRunSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const assistantBasePath = useMemo(() => {
    const params = new URLSearchParams();
    if (projectId) params.set('project', projectId);
    const query = params.toString();
    return query ? `/assistant?${query}` : '/assistant';
  }, [projectId]);

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
    const timeoutId = window.setTimeout(() => {
      void load();
    }, 0);
    return () => window.clearTimeout(timeoutId);
  }, []);

  const openRun = (runId: string) => {
    const params = new URLSearchParams();
    if (projectId) params.set('project', projectId);
    params.set('runId', runId);
    navigate(`/assistant?${params.toString()}`);
  };
  const startNew = () => navigate(assistantBasePath);

  return (
    <div className={styles.root} data-testid="sessions-page">
      <PageHeader
        title="Sessions"
        subtitle="Your assistant conversations across Agentweaver. Resume one, or start a new one."
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
