import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, DeleteRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { isCoordinatorRun } from '../utils/runKind';
import type { Project, WorkflowRunDto } from '../api/types';
import { PageHeader } from '../components/PageHeader';

// Orchestrations — a project-level list of coordinator orchestration runs. Each
// row opens the existing coordinator topology view. Data comes from the project's
// runs API (real data); coordinator runs are detected via isCoordinatorRun.

// A run in any of these states has finished its lifecycle: there is no live workflow
// to stop. Stop is only offered for non-terminal (running) orchestrations.
const RUN_TERMINAL_STATUSES = new Set(['completed', 'failed', 'declined', 'merged', 'merge_failed']);

function isRunTerminal(status: string | undefined): boolean {
  if (!status) return false;
  return RUN_TERMINAL_STATUSES.has(status.toLowerCase().replace(/[^a-z_]/g, ''));
}

function coordinatorStatusLabel(status: string | undefined): string | undefined {
  if (!status) return undefined;
  const k = status.toLowerCase().replace(/[^a-z]/g, '');
  if (k.includes('awaitingassembly')) return 'Awaiting assembly';
  if (k.includes('assembling')) return 'Assembling';
  if (k.includes('inreview')) return 'In review';
  if (k.includes('dispatch')) return 'Dispatching';
  if (k.includes('complete')) return 'Complete';
  if (k.includes('declin')) return 'Declined';
  if (k.includes('block')) return 'Blocked';
  if (k.includes('fail')) return 'Failed';
  return status;
}

function badgeColor(label: string | undefined): 'success' | 'danger' | 'warning' | 'informative' {
  if (label === 'Complete') return 'success';
  if (label === 'Failed' || label === 'Blocked' || label === 'Declined') return 'danger';
  if (label === 'In review' || label === 'Awaiting assembly') return 'warning';
  return 'informative';
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  rowMain: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    flex: 1,
    minWidth: 0,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  task: {
    fontWeight: tokens.fontWeightSemibold,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  meta: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    textAlign: 'center',
  },
});

export function OrchestrationsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<WorkflowRunDto | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = (showSpinner: boolean) => {
    if (!projectId) return Promise.resolve();
    if (showSpinner) setLoading(true);
    return Promise.all([
      apiClient.listProjectRuns(projectId),
      apiClient.getProject(projectId).catch(() => null as Project | null),
    ])
      .then(([runList, proj]) => {
        setRuns([...runList].reverse().filter(isCoordinatorRun));
        setProject(proj);
        setError(null);
      })
      .catch((err) => setError(formatError(err)))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    let cancelled = false;
    if (!projectId) return;
    void load(true);
    return () => { cancelled = true; void cancelled; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const runIdOf = (run: WorkflowRunDto) => run.workflow_run_id ?? run.execution_id;

  const handleStop = (run: WorkflowRunDto) => {
    const runId = runIdOf(run);
    if (!window.confirm('Stop this orchestration? The running work will be cancelled, but the run is kept so you can inspect it.')) return;
    setBusyId(runId);
    apiClient
      .cancelRun(runId)
      .then(() => load(false))
      .catch((err) => setError(formatError(err)))
      .finally(() => setBusyId(null));
  };

  const confirmDelete = () => {
    if (!deleteTarget) return;
    const runId = runIdOf(deleteTarget);
    setBusyId(runId);
    apiClient
      .deleteRun(runId)
      .then(() => {
        setRuns((prev) => prev.filter((r) => runIdOf(r) !== runId));
        setDeleteTarget(null);
      })
      .catch((err) => setError(formatError(err)))
      .finally(() => setBusyId(null));
  };

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Orchestrations"
        subtitle="Coordinator runs across this project."
        breadcrumb={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
              {project?.name ?? projectId}
            </Link>
            <span>/</span>
            <span>Orchestrations</span>
          </div>
        }
        actions={
          <Button
            appearance="secondary"
            icon={<ArrowSyncRegular />}
            disabled={loading}
            onClick={() => { void load(true); }}
          >
            Refresh
          </Button>
        }
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && <Spinner label="Loading orchestrations" />}

      {!loading && !error && runs.length === 0 && (
        <div className={styles.emptyState}>
          <Title3>No orchestrations yet</Title3>
          <Text>Start an orchestration from the Board to coordinate a squad of agents.</Text>
        </div>
      )}

      {!loading && runs.length > 0 && (
        <div className={styles.list}>
          {runs.map((run) => {
            const runId = run.workflow_run_id ?? run.execution_id;
            const coordLabel = coordinatorStatusLabel(run.coordinator_status);
            const terminal = isRunTerminal(run.status);
            const busy = busyId === runId;
            return (
              <div key={runId} className={styles.row}>
                <Badge appearance="tint" color={badgeColor(coordLabel)}>
                  {coordLabel ?? run.status}
                </Badge>
                <div className={styles.rowMain}>
                  <Text className={styles.task}>{run.task ?? '(no task description)'}</Text>
                  <Text className={styles.meta}>{new Date(run.started_at).toLocaleString()}</Text>
                </div>
                <div className={styles.actions}>
                  <Link to={`/projects/${projectId}/orchestrations/${runId}`} style={{ textDecoration: 'none' }}>
                    <Button appearance="secondary">Open</Button>
                  </Link>
                  <Tooltip
                    content={terminal ? 'This orchestration has already finished' : 'Stop this orchestration'}
                    relationship="label"
                  >
                    <Button
                      appearance="subtle"
                      icon={<DismissCircleRegular />}
                      aria-label="Stop orchestration"
                      disabled={terminal || busy}
                      onClick={() => handleStop(run)}
                    >
                      Stop
                    </Button>
                  </Tooltip>
                  <Tooltip content="Delete this orchestration" relationship="label">
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label="Delete orchestration"
                      disabled={busy}
                      onClick={() => setDeleteTarget(run)}
                    >
                      Delete
                    </Button>
                  </Tooltip>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <Dialog open={deleteTarget !== null} onOpenChange={(_, data) => { if (!data.open) setDeleteTarget(null); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete orchestration</DialogTitle>
            <DialogContent>
              Delete this orchestration? This removes the run and its workspace.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setDeleteTarget(null)}>Cancel</Button>
              <Button
                appearance="primary"
                icon={<DeleteRegular />}
                disabled={deleteTarget !== null && busyId === (deleteTarget.workflow_run_id ?? deleteTarget.execution_id)}
                onClick={confirmDelete}
              >
                Delete
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
