import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, ProjectMetricsDto } from '../../api/types';
import {
  Badge,
  BladeHeader,
  CommandBar,
  EmptyState,
  FilterBar,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  StatusIconText,
  Text,
  makeStyles,
  tokens,
  ArrowSyncRegular,
  Pulse24Regular,
} from '../../copilot-fluent-system';
import type { AzfTone } from '../../copilot-fluent-system';
import { ModelPerformancePanels } from '../../components/dashboard/ModelPerformancePanels';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';

type TimeRange = '7d' | '30d' | '90d';

const useStyles = makeStyles({
  commandSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  filterSelect: {
    width: '140px',
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
  loadingSurface: {
    minHeight: '180px',
    justifyContent: 'center',
    alignItems: 'center',
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
      subtitle="Azure Monitor-style telemetry for model performance, token usage, and invocation trends."
    >
      <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.commandSurface].join(' ')}>
        <CommandBar
          title="Monitor command band"
          description="Refresh the project telemetry blade without leaving the current resource."
          primaryActions={[{
            id: 'refresh-overview',
            label: 'Refresh',
            icon: <ArrowSyncRegular />,
            onClick: () => setReloadKey((value) => value + 1),
          }]}
        >
          <Badge appearance="tint" color={hasTelemetry(metrics) ? 'success' : 'warning'}>
            {hasTelemetry(metrics) ? 'Telemetry flowing' : 'Awaiting telemetry'}
          </Badge>
        </CommandBar>
        <FilterBar
          filters={[
            { id: 'scope', label: 'Scope', value: project?.name ?? projectId, selected: true },
            { id: 'signal', label: 'Signal', value: 'Model performance', selected: true },
            { id: 'range', label: 'Window', value: rangeLabel(range), selected: true },
          ]}
        >
          <Select
            aria-label="Observability time range"
            value={range}
            onChange={(_, data) => setRange(data.value as TimeRange)}
            size="small"
            className={styles.filterSelect}
          >
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="90d">Last 90 days</option>
          </Select>
        </FilterBar>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <section className={styles.dataSection} aria-label="Observability overview status">
        <BladeHeader
          size="compact"
          title="Resource health summary"
          subtitle="Fast Azure Monitor rollup for the selected project and time window."
          resourceIcon={<Pulse24Regular />}
          menuLabel={<Badge appearance="outline">{rangeLabel(range)}</Badge>}
          loading={loading}
        />
        <div className={styles.summaryGrid}>
          <SummaryTile label="Model calls" value={compactNumber(summary.totalCalls)} detail="Invocation trend" tone={summary.totalCalls > 0 ? 'success' : 'warning'} />
          <SummaryTile label="AI credits" value={formatAiu(summary.totalAiu)} detail="AppInsights usage" tone={summary.totalAiu > 0 ? 'success' : 'info'} />
          <SummaryTile label="Active models" value={String(summary.activeModels)} detail="Model mix" tone={summary.activeModels > 0 ? 'success' : 'warning'} />
          <SummaryTile label="Agent rows" value={String(summary.agentRows)} detail={`${summary.p95Models} latency baselines`} tone={summary.agentRows > 0 ? 'success' : 'info'} />
        </div>
      </section>

      {loading && !metrics ? (
        <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable azf-stack', styles.loadingSurface].join(' ')} aria-live="polite">
          <Spinner label="Loading observability overview" />
        </div>
      ) : (
        <section className={styles.dataSection} aria-label="Model performance data">
          <BladeHeader size="compact" title="Data sections" subtitle="Layered trend, model mix, and latency surfaces from the telemetry dataset." />
          <ModelPerformancePanels metrics={metrics} />
          {!metrics && <EmptyState title="No observability data yet" body="Run activity metrics will appear after project runs emit telemetry." />}
        </section>
      )}
    </ObservabilityLayout>
  );
}
