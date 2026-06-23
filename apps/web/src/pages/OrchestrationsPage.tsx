import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { isCoordinatorRun } from '../utils/runKind';
import type { Project, WorkflowRunDto } from '../api/types';

// Orchestrations — a project-level list of coordinator orchestration runs. Each
// row opens the existing coordinator topology view. Data comes from the project's
// runs API (real data); coordinator runs are detected via isCoordinatorRun.

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
  pageHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
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

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <div className={styles.breadcrumb}>
        <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
        <span>/</span>
        <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>
          {project?.name ?? projectId}
        </Link>
        <span>/</span>
        <span>Orchestrations</span>
      </div>

      <div className={styles.pageHeader}>
        <Title2>Orchestrations</Title2>
        <div className={styles.actions}>
          <Button
            appearance="secondary"
            icon={<ArrowSyncRegular />}
            disabled={loading}
            onClick={() => { void load(true); }}
          >
            Refresh
          </Button>
        </div>
      </div>

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
            return (
              <div key={runId} className={styles.row}>
                <Badge appearance="tint" color={badgeColor(coordLabel)}>
                  {coordLabel ?? run.status}
                </Badge>
                <div className={styles.rowMain}>
                  <Text className={styles.task}>{run.task ?? '(no task description)'}</Text>
                  <Text className={styles.meta}>{new Date(run.started_at).toLocaleString()}</Text>
                </div>
                <Link to={`/projects/${projectId}/orchestrations/${runId}`} style={{ textDecoration: 'none' }}>
                  <Button appearance="secondary">Open</Button>
                </Link>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
