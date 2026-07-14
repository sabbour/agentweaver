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
  DialogTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { KanbanBoard } from '../components/board/KanbanBoard';
import { PageHeader } from '../components/PageHeader';
import { StartOrchestrationDialog } from '../components/StartOrchestrationDialog';
import { isCoordinatorRun } from '../utils/runKind';
import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Caption1,
  makeStyles,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { ErrorState, MetricRow } from '../components/ui';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import type { Project, WorkflowRunDto } from '../api/types';
// Map a coordinator orchestration status (Feature 008) to a human label. Optional —
// the backend adds coordinator_status concurrently, so callers fall back to the bare
// RunStatus when it is absent.
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
const RUN_TERMINAL_STATUSES = new Set(['completed', 'merged', 'failed', 'merge_failed', 'declined']);

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
  if (RUN_TERMINAL_STATUSES.has(run.status?.toLowerCase() ?? '') && (!coordLabel || !TERMINAL_COORD_LABELS.has(coordLabel))) {
    return runStatusFallbackLabel(run);
  }
  return coordLabel;
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
    lineHeight: tokens.lineHeightBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    textDecoration: 'none',
  },
  commandSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalM,
  },
  summaryCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minHeight: '96px',
  },
  summaryLabel: {
    color: tokens.colorNeutralForeground3,
  },
  summaryValue: {
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  summaryDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  boardSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  resourceMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    color: tokens.colorNeutralForeground2,
  },
  runList: {
    maxHeight: '360px',
    overflow: 'auto',
    padding: `${tokens.spacingVerticalXS} 0 ${tokens.spacingVerticalS}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  runRow: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  runTask: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '60ch',
    overflowWrap: 'anywhere',
    wordBreak: 'normal',
  },
  runMeta: {
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
    fontVariantNumeric: 'tabular-nums',
  },
  runSection: {
    display: 'flex',
    flexDirection: 'column',
    paddingTop: tokens.spacingVerticalXL,
    paddingBottom: `calc(${tokens.spacingVerticalXXXL} + ${tokens.spacingVerticalXXXL})`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  runAccordion: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  runHeaderContent: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    width: '100%',
  },
  runHeaderTitleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    minWidth: 0,
  },
  runTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    overflowWrap: 'anywhere',
  },
  runDescription: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    maxWidth: '68ch',
  },
  runStatusStack: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    maxWidth: '26ch',
  },
  runCountBadge: {
    flexShrink: 0,
    fontVariantNumeric: 'tabular-nums',
  },
  runPanelContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    paddingTop: tokens.spacingVerticalXS,
  },
  runEmpty: {
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '68ch',
  },
  runTable: {
    minWidth: '720px',
  },
  runActionCell: {
    whiteSpace: 'nowrap',
  },
  errorText: {
    color: tokens.colorStatusDangerForeground1,
    display: 'block',
    marginTop: tokens.spacingVerticalS,
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
  const coordLabel = isCoord ? resolveRunStatusLabel(run) : undefined;
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
    <TableRow className={styles.runRow}>
      <TableCell>
        {coordLabel ? (
          <div className={styles.runStatusStack}>
            <Badge appearance="tint" size="small" color={
              coordLabel === 'Complete' ? 'success' :
              coordLabel === 'Failed' || coordLabel === 'Blocked' || coordLabel === 'Declined' || coordLabel === 'Merge failed' ? 'danger' :
              coordLabel === 'In review' ? 'warning' :
              'subtle'
            }>
              {coordLabel}
            </Badge>
            {coordLabel === 'Failed' && coordReason && <Caption1 className={styles.runMeta}>{coordReason}</Caption1>}
          </div>
        ) : (
          <Badge appearance="tint" size="small" color={
            run.status === 'merged' ? 'success' :
            run.status === 'completed' && run.result === 'no_changes' ? 'subtle' :
            run.status === 'completed' ? 'success' :
            run.status === 'failed' || run.status === 'merge_failed' ? 'danger' :
            run.status === 'in_progress' ? 'subtle' : 'subtle'
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
      </TableCell>
      <TableCell>
        <Text className={styles.runTask}>{run.task ?? '(no task description)'}</Text>
      </TableCell>
      <TableCell>
        <Text className={styles.runMeta}>{new Date(run.started_at).toLocaleString()}</Text>
      </TableCell>
      <TableCell className={styles.runActionCell}>
        {isCoord && (
          <Link to={`/projects/${projectId}/orchestrations/${run.workflow_run_id ?? run.execution_id}`} style={{ textDecoration: 'none' }}>
            <Button appearance="secondary" size="small">Topology</Button>
          </Link>
        )}
      </TableCell>
      <TableCell className={styles.runActionCell}>
        {isAbandonable && (
          <>
            <Button appearance="subtle" size="small" icon={<DismissCircleRegular />} disabled={acting} onClick={() => setConfirmOpen(true)} aria-label="Abandon run">
              Abandon
            </Button>
            <Dialog open={confirmOpen} onOpenChange={(_, d) => setConfirmOpen(d.open)}>
              <DialogSurface>
                <DialogBody>
                  <DialogTitle>Abandon run?</DialogTitle>
                  <DialogContent>
                    This will abandon the run and discard any pending changes. This cannot be undone.
                    {error && <Text className={styles.errorText}>{error}</Text>}
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
              size="small"
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
                    {error && <Text className={styles.errorText}>{error}</Text>}
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
      </TableCell>
    </TableRow>
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
  const liveRuns = runs.filter((run) => !['completed', 'merged', 'failed', 'merge_failed', 'declined'].includes(run.status));
  const completedRuns = runs.length - liveRuns.length;

  return (
    <div className={styles.root}>
      {loading && !project && <Spinner label="Loading project board" />}

      {error && (
        <ErrorState
          title="Couldn't load project board"
          message={error}
          onRetry={() => { window.location.reload(); }}
        />
      )}

      {project && !project.available && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This project is unavailable. The agent worktree may have moved or become inaccessible.
          </MessageBarBody>
        </MessageBar>
      )}

      {project && (
        <>
          <PageHeader
            title={project.name}
            subtitle="Orchestrate agent tasks from intake through execution, review, and recovery."
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

          <section className={styles.commandSurface} aria-label="Project status">
            <div className={styles.resourceMeta}>
              <Badge appearance="tint" color={project.available ? 'success' : 'warning'}>
                {project.available ? 'Available' : 'Unavailable'}
              </Badge>
              <Badge appearance="outline">{project.origin ?? 'project'}</Badge>
              <Badge appearance="outline">{project.default_branch ?? 'default branch'}</Badge>
            </div>
            <MetricRow
              items={[
                { label: 'Board state', value: 'Live', hint: 'Kanban lanes active' },
                { label: 'Active runs', value: liveRuns.length, hint: 'in-flight' },
                { label: 'Historical runs', value: completedRuns, hint: 'terminal' },
              ]}
            />
          </section>

          <section className={styles.boardSurface} aria-label="Work board">
            <KanbanBoard projectId={projectId} />
          </section>

          <section className={styles.runSection} aria-labelledby="project-runs-title">
            <Accordion className={styles.runAccordion} collapsible>
              <AccordionItem value="run-audit">
                <AccordionHeader expandIconPosition="end">
                  <div className={styles.runHeaderContent}>
                    <div className={styles.runHeaderTitleRow}>
                      <Text id="project-runs-title" className={styles.runTitle}>Run audit trail</Text>
                      <Badge className={styles.runCountBadge} appearance="tint" color={runs.length ? 'subtle' : 'subtle'} size="small">{runs.length} runs</Badge>
                    </div>
                    <Caption1 className={styles.runDescription}>
                      Collapsed by default. Open for historical navigation; use the board above for current recovery work.
                    </Caption1>
                  </div>
                </AccordionHeader>
                <AccordionPanel>
                  <div className={styles.runPanelContent}>
                    {runs.length === 0 ? (
                      <Text className={styles.runEmpty}>No run history yet. Runs started from orchestration tasks will appear here for audit.</Text>
                    ) : (
                      <div className={styles.runList}>
                        <Table className={styles.runTable} size="small" aria-label="Run audit trail history">
                          <TableHeader>
                            <TableRow>
                              <TableHeaderCell>Status</TableHeaderCell>
                              <TableHeaderCell>Task</TableHeaderCell>
                              <TableHeaderCell>Started</TableHeaderCell>
                              <TableHeaderCell>View</TableHeaderCell>
                              <TableHeaderCell>Actions</TableHeaderCell>
                            </TableRow>
                          </TableHeader>
                          <TableBody>
                            {runs.map((r) => <RunRow key={r.workflow_run_id} run={r} projectId={projectId} onDeleted={handleRunDeleted} />)}
                          </TableBody>
                        </Table>
                      </div>
                    )}
                  </div>
                </AccordionPanel>
              </AccordionItem>
            </Accordion>
          </section>
        </>
      )}
    </div>
  );
}
