import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import { safeTerminalFailureMessage, type Project, type RunTerminalDiagnostic, type WorkflowRunDto } from '../../api/types';
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

function FailureDiagnosticPanel({
  projectId,
  runId,
}: {
  projectId: string;
  runId: string;
}) {
  const [diagnostic, setDiagnostic] = useState<RunTerminalDiagnostic | null>(null);
  const [availability, setAvailability] = useState<'loading' | 'unavailable' | 'expired' | 'unauthorized'>('loading');

  useEffect(() => {
    let cancelled = false;
    apiClient.getRunTerminalDiagnostic(runId)
      .then((value) => {
        if (cancelled) return;
        setDiagnostic({
          ...value,
          correlation_ids: Object.fromEntries(
            Object.entries(value.correlation_ids).filter(([, id]) => /^[a-f0-9]{32}$/.test(id)),
          ),
          cause_chain: value.cause_chain.filter((cause) => [
            'HttpRequestException',
            'IOException',
            'OperationCanceledException',
            'SocketException',
            'TaskCanceledException',
            'TimeoutException',
          ].includes(cause)),
        });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setAvailability(
          error instanceof ApiError && error.status === 401 ? 'expired'
            : error instanceof ApiError && error.status === 403 ? 'unauthorized'
              : 'unavailable',
        );
      });
    return () => { cancelled = true; };
  }, [runId]);

  if (diagnostic) {
    return (
      <MessageBar intent="error" data-testid={`trace-terminal-diagnostic-${runId}`}>
        <MessageBarBody>
          <strong>Terminal failure · {diagnostic.component}</strong><br />
          {safeTerminalFailureMessage(diagnostic.message, diagnostic.code, diagnostic.retryable)} Code: {diagnostic.code}.
          {diagnostic.retryable === true ? ' This failure may be retried.' : ''}
          {diagnostic.cause_chain.length > 0 && <> Cause types: {diagnostic.cause_chain.join(' → ')}.</>}
          {Object.entries(diagnostic.correlation_ids).map(([name, value]) => (
            <span key={name}>
              {' '}<Link to={`/projects/${projectId}/observability/traces?run=${encodeURIComponent(runId)}&correlation=${encodeURIComponent(value)}`}>
                {name}: {value}
              </Link>
            </span>
          ))}
        </MessageBarBody>
      </MessageBar>
    );
  }

  if (availability === 'loading') return <Spinner size="extra-tiny" label="Loading terminal diagnostic" />;
  return (
    <MessageBar intent={availability === 'expired' || availability === 'unauthorized' ? 'warning' : 'info'}>
      <MessageBarBody>
        {availability === 'expired'
          ? 'Your session expired before the terminal diagnostic could be read. Sign in again and refresh.'
          : availability === 'unauthorized'
            ? 'You do not have access to this terminal diagnostic.'
            : 'No persisted terminal diagnostic is available for this failed run.'}
      </MessageBarBody>
    </MessageBar>
  );
}

export function ObservabilityTracesPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [searchParams] = useSearchParams();
  // Supports deep-linking straight to a run's trace, e.g. via a "View trace" button on the
  // run detail page (`/projects/{id}/orchestrations/{runId}?...` -> `?run={runId}`).
  const focusRunId = searchParams.get('run');
  const focusCorrelation = searchParams.get('correlation');
  const [project, setProject] = useState<Project | null>(null);
  const [roleByAgent, setRoleByAgent] = useState<Record<string, string>>({});
  const [runs, setRuns] = useState<WorkflowRunDto[]>([]);
  const [expandedRunId, setExpandedRunId] = useState<string | null>(focusRunId);
  const [reloadKey, setReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [failedOnly, setFailedOnly] = useState(false);

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
  const visibleRuns = useMemo(
    () => failedOnly
      ? runs.filter((run) => /(failed|declined|blocked)/i.test(run.coordinator_status ?? run.status))
      : runs,
    [failedOnly, runs],
  );

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="traces"
      title="Observability"
      description="Coordinator traces, terminal failure diagnostics, and links back to the live run view."
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
            <Button
              appearance={failedOnly ? 'primary' : 'secondary'}
              onClick={() => setFailedOnly((value) => !value)}
              data-testid="trace-failure-filter"
            >
              {failedOnly ? 'Show all traces' : 'Show failed only'}
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
        <PageSection title="Focused trace" description={focusCorrelation
          ? `Opened for correlation ${focusCorrelation}.`
          : 'Opened directly from the run detail page.'}>
          <FailureDiagnosticPanel key={focusRunId} projectId={projectId} runId={focusRunId} />
          <TracePreview runId={focusRunId} roleByAgent={roleByAgent} />
        </PageSection>
      )}

      {loading && !runs.length ? (
        <LoadingState label="Loading traces" />
      ) : (
        <PageSection title="Recent coordinator runs">
          <div style={{ display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM }}>
            {visibleRuns.map((run) => {
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
                    {/(failed|declined|blocked)/i.test(status) && (
                      <FailureDiagnosticPanel key={runId} projectId={projectId} runId={runId} />
                    )}
                    {expandedRunId === runId && <TracePreview runId={runId} roleByAgent={roleByAgent} />}
                  </div>
                </AppCard>
              );
            })}
            {!loading && visibleRuns.length === 0 && (
              <EmptyState
                title={failedOnly ? 'No failed coordinator traces' : 'No coordinator traces yet'}
                description={failedOnly
                  ? 'No recent failed runs match this filter.'
                  : 'Recent coordinator traces will appear after orchestrations emit telemetry.'}
              />
            )}
          </div>
        </PageSection>
      )}
    </ObservabilityLayout>
  );
}