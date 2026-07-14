import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Spinner,
  Text,
  Title3,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowSyncRegular, DeleteRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { PageHeader } from '../components/PageHeader';
import { isCoordinatorRun } from '../utils/runKind';
import { ErrorState, MetricRow } from '../components/ui';
import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { Project, WorkflowRunDto } from '../api/types';
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
  if (k.includes('awaitingassembly')) return 'Preparing assembly';
  if (k.includes('assembling')) return 'Assembling';
  if (k.includes('inreview')) return 'In review';
  if (k.includes('dispatch')) return 'Dispatching';
  if (k.includes('complete')) return 'Complete';
  if (k.includes('declin')) return 'Declined';
  if (k.includes('block')) return 'Blocked';
  if (k.includes('fail')) return 'Failed';
  return status;
}

// Terminal labels coordinatorStatusLabel can legitimately produce. If a run is terminal
// at the run-status level but coordinator_status never advanced to one of these (e.g. it
// was cancelled/abandoned mid-'dispatching'), the badge must fall back to the run-level
// status instead of showing a stale in-flight label (#304).
const TERMINAL_COORD_LABELS = new Set(['Complete', 'Failed', 'Blocked', 'Declined']);

function runStatusFallbackLabel(run: WorkflowRunDto): string {
  const result = run.result?.toLowerCase() ?? '';
  if (result.includes('abandon')) return 'Cancelled';
  const status = run.status?.toLowerCase() ?? '';
  if (status === 'declined') return 'Declined';
  if (status === 'merge_failed') return 'Merge failed';
  if (status === 'merged' || status === 'completed') return 'Complete';
  if (status === 'failed') return 'Failed';
  return run.status ?? 'Unknown';
}

// Resolves the label to display for a run's status badge, correcting for a stale
// coordinator_status when the run has already terminated (#304).
function resolveRunStatusLabel(run: WorkflowRunDto): string | undefined {
  const coordLabel = coordinatorStatusLabel(run.coordinator_status);
  if (isRunTerminal(run.status) && (!coordLabel || !TERMINAL_COORD_LABELS.has(coordLabel))) {
    return runStatusFallbackLabel(run);
  }
  return coordLabel;
}

function badgeColor(label: string | undefined): 'success' | 'danger' | 'warning' | 'subtle' {
  if (label === 'Complete') return 'success';
  if (label === 'Failed' || label === 'Blocked' || label === 'Declined' || label === 'Merge failed') return 'danger';
  if (label === 'In review') return 'warning';
  if (label === 'Cancelled') return 'subtle';
  return 'subtle';
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXL,
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    textDecoration: 'none',
  },
  statusSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow2,
  },
  statusCopy: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  statusTitle: {
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  statusHelp: {
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase300,
  },
  statusPills: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXXS,
    minHeight: '28px',
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    fontVariantNumeric: 'tabular-nums',
  },
  statusPillValue: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  resourceGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  resourceCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minHeight: '88px',
  },
  resourceLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  resourceValue: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    flexWrap: 'wrap',
  },
  sectionTitleGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  sectionTitle: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  sectionDescription: {
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '70ch',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    overflow: 'hidden',
  },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(150px, 0.6fr) minmax(0, 1.4fr) auto',
    alignItems: 'center',
    gap: tokens.spacingHorizontalL,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    ':not(:first-child)': {
      borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    },
    '@media (max-width: 760px)': {
      gridTemplateColumns: '1fr',
      alignItems: 'start',
    },
  },
  rowStatus: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
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
  emptyActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
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
  const [stopTarget, setStopTarget] = useState<WorkflowRunDto | null>(null);

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
  const activeRuns = runs.filter((run) => !isRunTerminal(run.status));
  const recentRuns = runs.filter((run) => isRunTerminal(run.status));
  const statusCounts = runs.reduce<Record<string, number>>((acc, run) => {
    const label = coordinatorStatusLabel(run.coordinator_status) ?? run.status ?? 'Unknown';
    acc[label] = (acc[label] ?? 0) + 1;
    return acc;
  }, {});
  const runSections = [
    {
      title: 'Active',
      description: 'Running, blocked, review, and assembly work that may still need operator attention.',
      items: activeRuns,
    },
    {
      title: 'Recent',
      description: 'Completed or stopped coordinator runs kept for inspection and cleanup.',
      items: recentRuns,
    },
  ].filter((section) => section.items.length > 0);

  const handleStop = (run: WorkflowRunDto) => {
    setStopTarget(run);
  };

  const confirmStop = () => {
    if (!stopTarget) return;
    const runId = runIdOf(stopTarget);
    setBusyId(runId);
    setStopTarget(null);
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
        subtitle="Coordinator runs for this project."
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
        <ErrorState
          title="Couldn't load orchestrations"
          message={error}
          onRetry={() => { void load(true); }}
        />
      )}

      {runs.length > 0 && (
        <div className={styles.statusSurface} aria-label="Orchestration status summary">
          <div className={styles.statusPills}>
            <span className={styles.statusPill}>
              <span className={styles.statusPillValue}>{activeRuns.length}</span> active
            </span>
            <span className={styles.statusPill}>
              <span className={styles.statusPillValue}>{recentRuns.length}</span> recent
            </span>
            {Object.entries(statusCounts).map(([label, count]) => (
              <span key={label} className={styles.statusPill}>
                <span className={styles.statusPillValue}>{count}</span> {label}
              </span>
            ))}
          </div>
          <MetricRow
            items={[
              { label: 'Project', value: project?.name ?? projectId },
              { label: 'Active', value: activeRuns.length, hint: 'in-flight' },
              { label: 'Retained', value: recentRuns.length, hint: 'terminal' },
            ]}
          />
        </div>
      )}

      {loading && (
        <div className={styles.loadingState}>
          <Spinner size="extra-tiny" />
          <Text>Loading orchestrations</Text>
        </div>
      )}

      {!loading && !error && runs.length === 0 && (
        <div className={styles.emptyState}>
          <Title3>No orchestrations yet</Title3>
          <Text>Start an orchestration from the Board to coordinate a squad of agents.</Text>
          <div className={styles.emptyActions}>
            <Link to={`/projects/${projectId}/board`} style={{ textDecoration: 'none' }}>
              <Button appearance="secondary">Open Board</Button>
            </Link>
          </div>
        </div>
      )}

      {!loading && runs.length > 0 && (
        <>
          {runSections.map((section) => (
            <section key={section.title} className={styles.section} aria-label={`${section.title} orchestrations`}>
              <div className={styles.sectionHeader}>
                <div className={styles.sectionTitleGroup}>
                  <Text className={styles.sectionTitle}>{section.title}</Text>
                  <Text className={styles.sectionDescription}>{section.description}</Text>
                </div>
                <Badge appearance="outline">{section.items.length} runs</Badge>
              </div>
              <div className={styles.list}>
                {section.items.map((run) => {
                  const runId = run.workflow_run_id ?? run.execution_id;
                  const coordLabel = resolveRunStatusLabel(run);
                  const terminal = isRunTerminal(run.status);
                  const busy = busyId === runId;
                  return (
                    <div key={runId} className={styles.row}>
                      <div className={styles.rowStatus}>
                        <Badge appearance="tint" color={badgeColor(coordLabel)}>
                          {coordLabel ?? run.status}
                        </Badge>
                        <Text className={styles.meta}>{terminal ? 'Finished' : 'Live orchestration'}</Text>
                      </div>
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
            </section>
          ))}
        </>
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

      <Dialog open={stopTarget !== null} onOpenChange={(_, data) => { if (!data.open) setStopTarget(null); }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Stop orchestration?</DialogTitle>
            <DialogContent>
              The running work will be cancelled, but the run is kept so you can inspect it.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setStopTarget(null)}>Cancel</Button>
              <Button
                appearance="primary"
                icon={<DismissCircleRegular />}
                disabled={stopTarget !== null && busyId === (stopTarget.workflow_run_id ?? stopTarget.execution_id)}
                onClick={confirmStop}
              >
                Stop
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
