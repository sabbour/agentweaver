import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, WorkflowRunDto } from '../../api/types';
import {
  Badge,
  BladeHeader,
  Button,
  CommandBar,
  EmptyState,
  FilterBar,
  MessageBar,
  MessageBarBody,
  Spinner,
  StatusIconText,
  Text,
  makeStyles,
  tokens,
  ArrowSyncRegular,
  Flowchart24Regular,
  OpenRegular,
} from '../../copilot-fluent-system';
import type { AzfTone } from '../../copilot-fluent-system';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { TransactionTracePanel } from '../../components/runs/TransactionTracePanel';
import { isCoordinatorRun } from '../../utils/runKind';

const useStyles = makeStyles({
  commandSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 980px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  summaryTile: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minHeight: '112px',
  },
  summaryLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  summaryValue: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  summaryFooter: {
    marginTop: 'auto',
  },
  dataSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    flexWrap: 'wrap',
  },
  task: {
    fontWeight: tokens.fontWeightSemibold,
  },
  meta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  runMeta: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  actionRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  loadingSurface: {
    minHeight: '180px',
    justifyContent: 'center',
    alignItems: 'center',
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

function statusTone(status: string): AzfTone {
  if (/(complete|merged)/i.test(status)) return 'success';
  if (/(failed|declined|blocked)/i.test(status)) return 'danger';
  if (/(review|assembly|awaiting)/i.test(status)) return 'warning';
  return 'info';
}

function isActiveStatus(status: string): boolean {
  return !/(complete|merged|failed|declined|blocked)/i.test(status);
}

function SummaryTile({ label, value, detail, tone }: { label: string; value: string; detail: string; tone: AzfTone }) {
  const styles = useStyles();
  return (
    <div className={['azf-surface azf-surface--subtle azf-surface--padding-comfortable', styles.summaryTile].join(' ')}>
      <Text className={styles.summaryLabel}>{label}</Text>
      <Text className={styles.summaryValue}>{value}</Text>
      <StatusIconText status={tone} className={styles.summaryFooter}>{detail}</StatusIconText>
    </div>
  );
}

function TracePreview({ runId }: { runId: string }) {
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    void Promise.resolve().then(() => { if (!cancelled) setLoading(true); });
    const t = setTimeout(() => { if (!cancelled) setLoading(false); }, 300);
    return () => { cancelled = true; clearTimeout(t); };
  }, [runId]);

  if (loading) return <Spinner label="Loading trace preview" />;
  return <TransactionTracePanel runId={runId} subtitle="Recent trace preview. Expand the tree and click a span to inspect its Generative AI properties." />;
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
    let cancelled = false;
    void Promise.resolve().then(() => { if (!cancelled) setLoading(true); });
    Promise.all([
      apiClient.getProject(projectId).catch(() => null as Project | null),
      apiClient.listProjectRuns(projectId),
    ])
      .then(([projectDto, runList]) => {
        if (cancelled) return;
        setProject(projectDto);
        setRuns([...runList].reverse().filter(isCoordinatorRun).slice(0, 10));
        setError(null);
      })
      .catch((err) => { if (!cancelled) setError(formatError(err)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, reloadKey]);

  const summary = useMemo(() => {
    const statuses = runs.map((run) => run.coordinator_status ?? run.status);
    return {
      total: runs.length,
      active: statuses.filter(isActiveStatus).length,
      failed: statuses.filter((status) => /(failed|declined|blocked)/i.test(status)).length,
      latest: runs[0]?.started_at ? new Date(runs[0].started_at).toLocaleDateString() : '—',
    };
  }, [runs]);

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="traces"
      title="Observability"
      subtitle="Azure Monitor-style coordinator traces with links back to the live run view."
    >
      <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.commandSurface].join(' ')}>
        <CommandBar
          title="Traces command band"
          description="Refresh distributed trace candidates while preserving the selected preview."
          primaryActions={[{
            id: 'refresh-traces',
            label: 'Refresh',
            icon: <ArrowSyncRegular />,
            onClick: () => setReloadKey((value) => value + 1),
          }]}
        >
          <Badge appearance="tint" color={summary.failed > 0 ? 'danger' : summary.total > 0 ? 'success' : 'warning'}>
            {summary.total > 0 ? `${summary.total} trace candidates` : 'No trace candidates'}
          </Badge>
        </CommandBar>
        <FilterBar
          filters={[
            { id: 'scope', label: 'Scope', value: project?.name ?? projectId, selected: true },
            { id: 'source', label: 'Source', value: 'AppInsights', selected: true },
            { id: 'kind', label: 'Kind', value: 'Coordinator runs', selected: true },
            { id: 'limit', label: 'Limit', value: 'Recent 10', selected: true },
          ]}
        />
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <section className={styles.dataSection} aria-label="Trace status summary">
        <BladeHeader
          size="compact"
          title="Trace resource summary"
          subtitle="Recent coordinator operations ready for distributed trace inspection."
          resourceIcon={<Flowchart24Regular />}
          menuLabel={<Badge appearance="outline">AppInsights</Badge>}
          loading={loading}
        />
        <div className={styles.summaryGrid}>
          <SummaryTile label="Candidates" value={String(summary.total)} detail="Recent coordinator runs" tone={summary.total > 0 ? 'success' : 'warning'} />
          <SummaryTile label="Active" value={String(summary.active)} detail="Not terminal" tone={summary.active > 0 ? 'info' : 'success'} />
          <SummaryTile label="Failed" value={String(summary.failed)} detail="Needs investigation" tone={summary.failed > 0 ? 'danger' : 'success'} />
          <SummaryTile label="Latest" value={summary.latest} detail="Run start date" tone={summary.total > 0 ? 'info' : 'warning'} />
        </div>
      </section>

      {loading && !runs.length ? (
        <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable azf-stack', styles.loadingSurface].join(' ')} aria-live="polite">
          <Spinner label="Loading traces" />
        </div>
      ) : (
        <section className={styles.dataSection} aria-label="Recent coordinator traces">
          <BladeHeader size="compact" title="Recent coordinator runs" subtitle="Open a run or expand an inline Azure Monitor trace preview." />
          <div className={styles.list}>
            {runs.map((run) => {
              const runId = run.workflow_run_id ?? run.execution_id;
              const status = run.coordinator_status ?? run.status;
              return (
                <div key={runId} className={['azf-surface azf-surface--panel azf-surface--padding-comfortable', styles.row].join(' ')}>
                  <div className={styles.rowHead}>
                    <div className="azf-stack azf-gap-xs">
                      <Text className={styles.task}>{run.task ?? '(no task description)'}</Text>
                      <div className={styles.runMeta}>
                        <Text className={styles.meta}>Started {new Date(run.started_at).toLocaleString()}</Text>
                        <Text className={styles.meta}>Run {runId}</Text>
                      </div>
                    </div>
                    <StatusIconText status={statusTone(status)}>
                      <Badge appearance="tint" color={badgeColor(status)}>{status}</Badge>
                    </StatusIconText>
                  </div>
                  <div className={styles.actionRow}>
                    <Link to={`/projects/${projectId}/orchestrations/${runId}`} style={{ textDecoration: 'none' }}>
                      <Button appearance="secondary" icon={<OpenRegular />}>Open run</Button>
                    </Link>
                    <Button appearance="primary" onClick={() => setExpandedRunId((current) => current === runId ? null : runId)}>
                      {expandedRunId === runId ? 'Hide trace' : 'Preview trace'}
                    </Button>
                  </div>
                  {expandedRunId === runId && <TracePreview runId={runId} />}
                </div>
              );
            })}
            {!loading && runs.length === 0 && (
              <EmptyState title="No coordinator traces yet" body="Recent coordinator traces will appear after orchestrations emit telemetry." />
            )}
          </div>
        </section>
      )}
    </ObservabilityLayout>
  );
}
