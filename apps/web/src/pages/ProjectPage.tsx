import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { StartOrchestrationDialog } from '../components/StartOrchestrationDialog';
import { PageHeader } from '../components/PageHeader';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { isCoordinatorRun } from '../utils/runKind';
import type { Project, WorkflowRunDto } from '../api/types';

// Map a coordinator orchestration status (Feature 008) to a human label. Optional —
// the backend adds coordinator_status concurrently, so callers fall back to the bare
// RunStatus when it is absent.
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
  runList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  runRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  runTask: {
    flex: 1,
    fontSize: tokens.fontSizeBase300,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  runMeta: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

function RunRow({ run, projectId, onDeleted }: { run: WorkflowRunDto; projectId: string; onDeleted: (workflowRunId: string) => void }) {
  const styles = useStyles();
  const [acting, setActing] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isTerminal = ['completed', 'merged', 'failed', 'merge_failed', 'declined'].includes(run.status);
  const isAbandonable = !isTerminal;
  const isCoord = isCoordinatorRun(run);
  const coordLabel = isCoord ? coordinatorStatusLabel(run.coordinator_status) : undefined;
  const coordReason = run.coordinator_status_reason;

  const handleConfirmed = async () => {
    setConfirmOpen(false);
    setActing(true);
    setError(null);
    try {
      await apiClient.deleteRun(run.execution_id);
      onDeleted(run.workflow_run_id);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Action failed.');
      setActing(false);
    }
  };

  return (
    <div className={styles.runRow}>
      {coordLabel ? (
        <Badge appearance="tint" color={
          coordLabel === 'Complete' ? 'success' :
          coordLabel === 'Failed' || coordLabel === 'Blocked' || coordLabel === 'Declined' ? 'danger' :
          coordLabel === 'In review' || coordLabel === 'Awaiting assembly' ? 'warning' :
          'informative'
        }>
          {coordLabel === 'Failed' && coordReason ? `Failed: ${coordReason}` : coordLabel}
        </Badge>
      ) : (
      <Badge appearance="tint" color={
        run.status === 'merged' ? 'success' :
        run.status === 'completed' && run.result === 'no_changes' ? 'informative' :
        run.status === 'completed' ? 'success' :
        run.status === 'failed' || run.status === 'merge_failed' ? 'danger' :
        run.status === 'in_progress' ? 'informative' : 'subtle'
      }>
        {run.status === 'completed' && run.result === 'no_changes' ? 'No Changes' :
         run.status === 'completed' ? 'Completed' :
         run.status === 'merged' ? 'Merged' :
         run.status === 'failed' ? 'Failed' :
         run.status === 'merge_failed' ? 'Merge Failed' :
         run.status === 'declined' ? 'Declined' :
         run.status === 'in_progress' ? 'Running' :
         run.status === 'awaiting_review' ? 'Awaiting Review' :
         run.status === 'merging' ? 'Merging' :
         run.status}
      </Badge>
      )}
      <Text className={styles.runTask}>{run.task ?? '(no task description)'}</Text>
      <Text className={styles.runMeta}>{new Date(run.started_at).toLocaleString()}</Text>
      {isCoord ? (
        <Link to={`/projects/${projectId}/orchestrations/${run.workflow_run_id ?? run.execution_id}`} style={{ textDecoration: 'none' }}>
          <Button appearance="secondary">Topology</Button>
        </Link>
      ) : (
        <Link to={`/projects/${projectId}/runs/${run.workflow_run_id ?? run.execution_id}/workflow`} style={{ textDecoration: 'none' }}>
          <Button appearance="secondary">Workflow</Button>
        </Link>
      )}
      {isAbandonable && (
        <>
          <Button appearance="subtle" icon={<DismissCircleRegular />} disabled={acting} onClick={() => setConfirmOpen(true)} aria-label="Abandon run">
            Abandon
          </Button>
          <Dialog open={confirmOpen} onOpenChange={(_, d) => setConfirmOpen(d.open)}>
            <DialogSurface>
              <DialogBody>
                <DialogTitle>Abandon run?</DialogTitle>
                <DialogContent>
                  This will abandon the run and discard any pending changes. This cannot be undone.
                  {error && <Text style={{ color: 'red', display: 'block', marginTop: 8 }}>{error}</Text>}
                </DialogContent>
                <DialogActions>
                  <DialogTrigger disableButtonEnhancement>
                    <Button appearance="secondary">Cancel</Button>
                  </DialogTrigger>
                  <Button appearance="primary" onClick={() => void handleConfirmed()}>
                    Abandon
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </>
      )}
      {isTerminal && (
        <>
          <Button
            appearance="subtle"
            icon={<DeleteRegular />}
            disabled={acting}
            onClick={() => setConfirmOpen(true)}
            aria-label="Delete run"
          />
          <Dialog open={confirmOpen} onOpenChange={(_, d) => setConfirmOpen(d.open)}>
            <DialogSurface>
              <DialogBody>
                <DialogTitle>Delete run?</DialogTitle>
                <DialogContent>
                  This will permanently delete the run and cannot be undone.
                  {error && <Text style={{ color: 'red', display: 'block', marginTop: 8 }}>{error}</Text>}
                </DialogContent>
                <DialogActions>
                  <DialogTrigger disableButtonEnhancement>
                    <Button appearance="secondary">Cancel</Button>
                  </DialogTrigger>
                  <Button appearance="primary" onClick={() => void handleConfirmed()}>
                    Delete
                  </Button>
                </DialogActions>
              </DialogBody>
            </DialogSurface>
          </Dialog>
        </>
      )}
    </div>
  );
}

export function ProjectPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [project, setProject] = useState<Project | null>(null);
  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const handleRunDeleted = (workflowRunId: string) => {
    setRuns((prev) => prev.filter((r) => r.workflow_run_id !== workflowRunId));
  };

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;

    const TERMINAL = ['completed', 'merged', 'failed', 'merge_failed', 'declined'];

    const fetchRuns = async () => {
      try {
        const runList = await apiClient.listProjectRuns(projectId);
        if (!cancelled) setRuns([...runList].reverse());
        return runList;
      } catch {
        return null;
      }
    };

    Promise.all([
      apiClient.getProject(projectId),
      fetchRuns(),
    ])
      .then(([proj, runList]) => {
        if (!cancelled) {
          setProject(proj);
        }
        // Kick off polling while any run is non-terminal
        if (!runList) return;
        const hasLive = runList.some(r => !TERMINAL.includes(r.status));
        if (!hasLive) return;
        const iv = setInterval(() => {
          if (cancelled) { clearInterval(iv); return; }
          void fetchRuns().then(latest => {
            if (latest && latest.every(r => TERMINAL.includes(r.status))) {
              clearInterval(iv);
            }
          });
        }, 5000);
        // Store interval id via closure — cleaned up when cancelled
        return () => clearInterval(iv);
      })
      .catch((err) => {
        if (!cancelled) setError(
          err instanceof ApiError
            ? `API error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    return () => { cancelled = true; };
  }, [projectId]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      {loading && <Spinner label="Loading project" />}

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {project && !project.available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This project is unavailable. The working directory may have moved. Go to{' '}
            <Link to={`/projects/${projectId}/settings`}>settings</Link> to relink it.
          </MessageBarBody>
        </MessageBar>
      )}

      {project && (
        <>
          <PageHeader
            title={project.name}
            subtitle="Backlog, Ready, and in-flight work."
            breadcrumb={
              <div className={styles.breadcrumb}>
                <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
                <span>/</span>
                <span>{project.name}</span>
              </div>
            }
            actions={
              <>
                <StartOrchestrationDialog
                  projectId={projectId}
                  onStarted={(runId) => navigate(`/projects/${projectId}/orchestrations/${runId}`)}
                />
              </>
            }
          />

          <KanbanBoard projectId={projectId} />

          <Title3>Runs</Title3>
          {runs.length === 0 ? (
            <Text>No runs yet. Start one above.</Text>
          ) : (
            <div className={styles.runList}>
              {runs.map((r) => <RunRow key={r.workflow_run_id} run={r} projectId={projectId} onDeleted={handleRunDeleted} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
}
