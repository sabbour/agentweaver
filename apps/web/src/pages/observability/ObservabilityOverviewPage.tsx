import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, ProjectMetricsDto } from '../../api/types';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Select,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { ModelPerformancePanels } from '../../components/dashboard/ModelPerformancePanels';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import {
  EmptyState,
  LoadingState,
  PageSection,
  StatTile,
} from '../../components/ui';

type TimeRange = '7d' | '30d' | '90d';

const useStyles = makeStyles({
  tileGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 980px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  filterRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
});

function timeRangeDates(range: TimeRange): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to);
  if (range === '7d') from.setDate(from.getDate() - 6);
  else if (range === '30d') from.setDate(from.getDate() - 29);
  else from.setDate(from.getDate() - 89);
  from.setUTCHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}

function formatError(error: unknown): string {
  return error instanceof ApiError
    ? `API error ${error.status}: ${error.body}`
    : error instanceof Error
      ? error.message
      : String(error);
}

function rangeLabel(range: TimeRange): string {
  if (range === '7d') return 'Last 7 days';
  if (range === '30d') return 'Last 30 days';
  return 'Last 90 days';
}

function compactNumber(value: number): string {
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
}

function formatAiu(nanoAiu: number): string {
  return `${compactNumber(nanoAiu / 1_000_000_000)} AIC`;
}

function hasTelemetry(metrics: ProjectMetricsDto | null): boolean {
  if (!metrics) return false;
  return Boolean(
    metrics.aiCreditUsageTrend?.some((point) => point.totalNanoAiu > 0)
    || metrics.modelUsage?.some((row) => row.invocationCount > 0 || row.totalNanoAiu > 0)
    || metrics.responseDuration?.some((row) => row.p50Ms != null || row.p95Ms != null)
    || metrics.timeToFirstToken?.some((row) => row.p50Ms != null || row.p95Ms != null),
  );
}

export function ObservabilityOverviewPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const [project, setProject] = useState<Project | null>(null);
  const [metrics, setMetrics] = useState<ProjectMetricsDto | null>(null);
  const [range, setRange] = useState<TimeRange>('30d');
  const [reloadKey, setReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    const dates = timeRangeDates(range);
    void Promise.resolve().then(() => { if (!cancelled) setLoading(true); });
    Promise.all([
      apiClient.getProject(projectId).catch(() => null as Project | null),
      apiClient.getProjectMetrics(projectId, dates.from, dates.to),
    ])
      .then(([projectDto, metricsDto]) => {
        if (cancelled) return;
        setProject(projectDto);
        setMetrics(metricsDto);
        setError(null);
      })
      .catch((err) => { if (!cancelled) setError(formatError(err)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId, range, reloadKey]);

  const summary = useMemo(() => {
    const modelUsage = metrics?.modelUsage ?? [];
    const aiCreditUsageTrend = metrics?.aiCreditUsageTrend ?? [];
    const responseDuration = metrics?.responseDuration ?? [];
    const agentBreakdown = metrics?.agentBreakdown ?? [];
    return {
      totalCalls: modelUsage.reduce((sum, row) => sum + row.invocationCount, 0),
      totalAiu: aiCreditUsageTrend.reduce((sum, point) => sum + point.totalNanoAiu, 0),
      activeModels: modelUsage.filter((row) => row.invocationCount > 0 || row.totalNanoAiu > 0).length,
      p95Models: responseDuration.filter((row) => row.p95Ms != null).length,
      agentRows: agentBreakdown.length,
    };
  }, [metrics]);

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="overview"
      title="Observability"
      description="Telemetry for model performance, token usage, and invocation trends."
    >
      <PageSection
        title="Performance summary"
        description={`Summary for the selected time window. ${rangeLabel(range)}.`}
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
            <Badge appearance="tint" color={hasTelemetry(metrics) ? 'success' : 'warning'}>
              {hasTelemetry(metrics) ? 'Telemetry flowing' : 'Awaiting telemetry'}
            </Badge>
            <Select
              aria-label="Observability time range"
              value={range}
              onChange={(_, data) => setRange(data.value as TimeRange)}
              size="small"
              style={{ width: '140px' }}
            >
              <option value="7d">Last 7 days</option>
              <option value="30d">Last 30 days</option>
              <option value="90d">Last 90 days</option>
            </Select>
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
          <StatTile label="Model calls" value={compactNumber(summary.totalCalls)} hint="Invocation trend" />
          <StatTile label="AI credits" value={formatAiu(summary.totalAiu)} hint="Usage" />
          <StatTile label="Active models" value={String(summary.activeModels)} hint="Model mix" />
          <StatTile label="Agent rows" value={String(summary.agentRows)} hint={`${summary.p95Models} latency baselines`} />
        </div>
      </PageSection>

      {loading && !metrics ? (
        <LoadingState label="Loading observability overview" />
      ) : (
        <PageSection title="Performance data" description="Trend, model mix, and latency data for the selected time range.">
          <ModelPerformancePanels metrics={metrics} />
          {!metrics && (
            <EmptyState
              title="No observability data yet"
              description="Run activity metrics will appear after project runs emit telemetry."
            />
          )}
        </PageSection>
      )}
    </ObservabilityLayout>
  );
}