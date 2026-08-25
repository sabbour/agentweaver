import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, WorkflowRunDto } from '../../api/types';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, OpenRegular } from '@fluentui/react-icons';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { TransactionTracePanel } from '../../components/runs/TransactionTracePanel';
import { isCoordinatorRun } from '../../utils/runKind';
import {
  AppCard,
  Body,
  EmptyState,
  Label,
  LoadingState,
  PageSection,
  StatTile,
} from '../../components/ui';

const useStyles = makeStyles({
  tileGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 980px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  rowHead: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    alignItems: 'flex-start',
    flexWrap: 'wrap',
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
});

function formatError(error: unknown): string {
  return error instanceof ApiError
    ? `API error ${error.status}: ${error.body}`
    : error instanceof Error
      ? error.message
      : String(error);
}

function badgeColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (/(complete|merged)/i.test(status)) return 'success';
  if (/(failed|declined|blocked)/i.test(status)) return 'danger';
  if (/(review|assembly|awaiting)/i.test(status)) return 'warning';
  return 'subtle';
}

function isActiveStatus(status: string): boolean {
  return !/(complete|merged|failed|declined|blocked)/i.test(status);
}

function TracePreview({ runId, roleByAgent }: { runId: string; roleByAgent: Record<string, string> }) {
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    void Promise.resolve().then(() => { if (!cancelled) setLoading(true); });
    const t = setTimeout(() => { if (!cancelled) setLoading(false); }, 300);
    return () => { cancelled = true; clearTimeout(t); };
  }, [runId]);

  if (loading) return <Spinner label="Loading trace preview" />;
  return (
    <TransactionTracePanel
      runId={runId}
      roleByAgent={roleByAgent}
      subtitle="Recent trace preview. Expand the tree and click a span to inspect its Generative AI properties."
    />
  );
}

export function ObservabilityTracesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [searchParams] = useSearchParams();
  // Supports deep-linking straight to a run's trace, e.g. via a "View trace" button on the
  // run detail page (`/projects/{id}/orchestrations/{runId}?...` -> `?run={runId}`).
  const focusRunId = searchParams.get('run');
  const [project, setProject] = useState<Project | null>(null);
  const [roleByAgent, setRoleByAgent] = useState<Record<string, string>>({});
  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [expandedRunId, setExpandedRunId] = useState<string | null>(focusRunId);
  const [reloadKey, setReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getTeam(projectId)
      .then((team) => {
        if (cancelled) return;
        const map: Record<string, string> = {};
        for (const m of team.members ?? []) {
          if (m.name && m.role_title) map[m.name] = m.role_title;
        }
        setRoleByAgent(map);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [projectId]);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    void Promise.resolve().then(() => { if (!cancelled) setLoading(true); });
    Promise.all([
      apiClient.getProject(projectId).catch(() => null as Project | null),
      apiClient.listProjectRuns(projectId, { pageSize: 100 }),
    ])
      .then(([projectDto, runPage]) => {
        if (cancelled) return;
        setProject(projectDto);
        // `/runs` already returns newest-first (deterministic OrderByDescending(StartedAt)) —
        // keep that order so "Recent coordinator runs" shows the most recent run first.
        setRuns(runPage.items.filter(isCoordinatorRun).slice(0, 10));
        setError(null);
      })
      .catch((err) => { if (!cancelled) setError(formatError(err)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, reloadKey]);

  const summary = useMemo(() => {
    const statuses = runs.map((run) => run.coordinator_status ?? run.status);
    const startedTimestamps = runs
      .map((run) => (run.started_at ? new Date(run.started_at).getTime() : NaN))
      .filter((time) => !Number.isNaN(time));
    const latestTimestamp = startedTimestamps.length > 0 ? Math.max(...startedTimestamps) : null;
    return {
      total: runs.length,
      active: statuses.filter(isActiveStatus).length,
      failed: statuses.filter((status) => /(failed|declined|blocked)/i.test(status)).length,
      latest: latestTimestamp !== null ? new Date(latestTimestamp).toLocaleDateString() : '—',
    };
  }, [runs]);

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="traces"
      title="Observability"
      description="Coordinator traces with links back to the live run view."
    >
      <PageSection
        title="Trace summary"
        description="Recent coordinator operations ready for distributed trace inspection."
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
            <Badge appearance="tint" color={summary.failed > 0 ? 'danger' : summary.total > 0 ? 'success' : 'warning'}>
              {summary.total > 0 ? `${summary.total} trace candidates` : 'No trace candidates'}
            </Badge>
            <Button
              appearance="secondary"
              icon={<ArrowSyncRegular />}
              onClick={() => setReloadKey((value) => value + 1)}
            >
              Refresh
            </Button>
          </div>
        }
      >
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        <div className={styles.tileGrid}>
          <StatTile label="Candidates" value={String(summary.total)} hint="Recent coordinator runs" />
          <StatTile label="Active" value={String(summary.active)} hint="Not terminal" />
          <StatTile label="Failed" value={String(summary.failed)} hint="Needs investigation" />
          <StatTile label="Latest" value={summary.latest} hint="Run start date" />
        </div>
      </PageSection>

      {focusRunId && !runs.some((run) => (run.workflow_run_id ?? run.execution_id) === focusRunId) && (
        <PageSection title="Focused trace" description="Opened directly from the run detail page.">
          <TracePreview runId={focusRunId} roleByAgent={roleByAgent} />
        </PageSection>
      )}

      {loading && !runs.length ? (
        <LoadingState label="Loading traces" />
      ) : (
        <PageSection title="Recent coordinator runs">
          <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
            {runs.map((run) => {
              const runId = run.workflow_run_id ?? run.execution_id;
              const status = run.coordinator_status ?? run.status;
              return (
                <AppCard key={runId}>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
                    <div className={styles.rowHead}>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS }}>
                        <Body as="span" style={{ fontWeight: tokens.fontWeightSemibold }}>
                          {run.task ?? '(no task description)'}
                        </Body>
                        <div className={styles.runMeta}>
                          <Label as="span" tone="quiet">Started {new Date(run.started_at).toLocaleString()}</Label>
                          <Label as="span" tone="quiet">Run {runId}</Label>
                        </div>
                      </div>
                      <Badge appearance="tint" color={badgeColor(status)}>{status}</Badge>
                    </div>
                    <div className={styles.actionRow}>
                      <Link to={`/projects/${projectId}/orchestrations/${runId}`} style={{ textDecoration: 'none' }}>
                        <Button appearance="secondary" icon={<OpenRegular />}>Open run</Button>
                      </Link>
                      <Button
                        appearance="primary"
                        onClick={() => setExpandedRunId((current) => current === runId ? null : runId)}
                      >
                        {expandedRunId === runId ? 'Hide trace' : 'Preview trace'}
                      </Button>
                    </div>
                    {expandedRunId === runId && <TracePreview runId={runId} roleByAgent={roleByAgent} />}
                  </div>
                </AppCard>
              );
            })}
            {!loading && runs.length === 0 && (
              <EmptyState
                title="No coordinator traces yet"
                description="Recent coordinator traces will appear after orchestrations emit telemetry."
              />
            )}
          </div>
        </PageSection>
      )}
    </ObservabilityLayout>
  );
}