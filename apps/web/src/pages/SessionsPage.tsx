import { useEffect, useMemo, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
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
    flexDirection: 'row',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    width: '100%',
  },
  rowMain: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    textAlign: 'left',
    cursor: 'pointer',
    flexGrow: 1,
    minWidth: 0,
    border: 'none',
    background: 'none',
    padding: 0,
  },
  rowTitle: {
    fontWeight: tokens.fontWeightSemibold,
  },
  rowMeta: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  dialogError: {
    color: tokens.colorPaletteRedForeground1,
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
  const [deleteTarget, setDeleteTarget] = useState<AssistantRunSummary | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
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

  const requestDelete = (run: AssistantRunSummary) => {
    setDeleteError(null);
    setDeleteTarget(run);
  };

  const closeDeleteDialog = () => {
    if (deleting) return;
    setDeleteTarget(null);
    setDeleteError(null);
  };

  const confirmDelete = () => {
    if (!deleteTarget) return;
    const runId = deleteTarget.run_id;
    setDeleting(true);
    setDeleteError(null);
    apiClient.deleteRun(runId)
      .then(() => {
        setRuns((prev) => prev.filter((r) => r.run_id !== runId));
        setDeleteTarget(null);
      })
      .catch((err) => setDeleteError(formatError(err)))
      .finally(() => setDeleting(false));
  };

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
            <div key={run.run_id} className={styles.row} data-testid="sessions-row">
              <button
                type="button"
                className={styles.rowMain}
                onClick={() => openRun(run.run_id)}
              >
                <Text className={styles.rowTitle}>{run.title?.trim() || 'Untitled conversation'}</Text>
                <Text className={styles.rowMeta}>{`${run.status} \u00b7 ${formatCreatedAt(run.created_at)}`}</Text>
              </button>
              <Button
                appearance="subtle"
                size="small"
                icon={<DeleteRegular />}
                aria-label="Delete session"
                data-testid="sessions-row-delete"
                onClick={(e) => {
                  e.stopPropagation();
                  requestDelete(run);
                }}
              />
            </div>
          ))}
        </div>
      )}

      <Dialog open={deleteTarget !== null} onOpenChange={(_, data) => { if (!data.open) closeDeleteDialog(); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Delete this conversation?</DialogTitle>
            <DialogContent>
              This cannot be undone.
              {deleteError && <Text className={styles.dialogError}>{deleteError}</Text>}
            </DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="secondary" disabled={deleting} onClick={closeDeleteDialog}>
                  Cancel
                </Button>
              </DialogTrigger>
              <Button appearance="primary" disabled={deleting} onClick={confirmDelete} data-testid="sessions-delete-confirm">
                {deleting ? <Spinner size="tiny" /> : 'Delete'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
