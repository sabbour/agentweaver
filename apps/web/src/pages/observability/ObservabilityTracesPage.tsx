import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Badge, Button, MessageBar, MessageBarBody, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { CoordinatorChildResponse, PersistedRunEvent, Project, WorkflowRunDto } from '../../api/types';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { TransactionTracePanel } from '../../components/runs/TransactionTracePanel';
import { isCoordinatorRun } from '../../utils/runKind';

const useStyles = makeStyles({
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  task: {
    fontWeight: tokens.fontWeightSemibold,
  },
  meta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  actionRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
});

function formatError(error: unknown): string {
  return error instanceof ApiError
    ? `API error ${error.status}: ${error.body}`
    : error instanceof Error
      ? error.message
      : String(error);
}

function badgeColor(status: string): 'success' | 'warning' | 'danger' | 'informative' {
  if (/(complete|merged)/i.test(status)) return 'success';
  if (/(failed|declined|blocked)/i.test(status)) return 'danger';
  if (/(review|assembly|awaiting)/i.test(status)) return 'warning';
  return 'informative';
}

function TracePreview({ runId }: { runId: string }) {
  const [events, setEvents] = useState<PersistedRunEvent[]>([]);
  const [children, setChildren] = useState<CoordinatorChildResponse[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([
      apiClient.getRunEvents(runId).catch(() => [] as PersistedRunEvent[]),
      apiClient.getCoordinatorChildren(runId).catch(() => [] as CoordinatorChildResponse[]),
    ])
      .then(([traceEvents, childRuns]) => {
        if (cancelled) return;
        setEvents(traceEvents);
        setChildren(childRuns);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  }, [runId]);

  if (loading) return <Spinner label="Loading trace preview" />;
  return <TransactionTracePanel runId={runId} events={events} children={children} subtitle="Recent trace preview. Click a bar to inspect the agent output." />;
}

export function ObservabilityTracesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [project, setProject] = useState<Project | null>(null);
  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [expandedRunId, setExpandedRunId] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);
    Promise.all([
      apiClient.getProject(projectId).catch(() => null as Project | null),
      apiClient.listProjectRuns(projectId),
    ])
      .then(([projectDto, runList]) => {
        setProject(projectDto);
        setRuns([...runList].reverse().filter(isCoordinatorRun).slice(0, 10));
        setError(null);
      })
      .catch((err) => setError(formatError(err)))
      .finally(() => setLoading(false));
  }, [projectId, reloadKey]);

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="traces"
      title="Observability"
      subtitle="Recent coordinator traces with links back to the live run view."
      actions={<Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={() => setReloadKey((value) => value + 1)}>Refresh</Button>}
    >
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {loading && !runs.length ? (
        <Spinner label="Loading traces" />
      ) : (
        <div className={styles.list}>
          {runs.map((run) => {
            const runId = run.workflow_run_id ?? run.execution_id;
            const status = run.coordinator_status ?? run.status;
            return (
              <div key={runId} className={styles.row}>
                <div className={styles.rowHead}>
                  <div>
                    <Text className={styles.task}>{run.task ?? '(no task description)'}</Text>
                    <Text className={styles.meta}>{new Date(run.started_at).toLocaleString()}</Text>
                  </div>
                  <Badge appearance="tint" color={badgeColor(status)}>{status}</Badge>
                </div>
                <div className={styles.actionRow}>
                  <Link to={`/projects/${projectId}/orchestrations/${runId}`} style={{ textDecoration: 'none' }}>
                    <Button appearance="secondary">Open run</Button>
                  </Link>
                  <Button appearance="primary" onClick={() => setExpandedRunId((current) => current === runId ? null : runId)}>
                    {expandedRunId === runId ? 'Hide trace' : 'Preview trace'}
                  </Button>
                </div>
                {expandedRunId === runId && <TracePreview runId={runId} />}
              </div>
            );
          })}
          {!loading && runs.length === 0 && <Text>No coordinator traces yet.</Text>}
        </div>
      )}
    </ObservabilityLayout>
  );
}
