import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, ProjectMetricsDto, RunAgentTokenBreakdownDto } from '../../api/types';
import {
  Badge,
  BladeHeader,
  CommandBar,
  FilterBar,
  Select,
  Spinner,
  StatusIconText,
  Text,
  makeStyles,
  tokens,
  ArrowSyncRegular,
  Bot24Regular,
  MessageBar,
  MessageBarBody,
} from '../../copilot-fluent-system';
import type { AzfTone } from '../../copilot-fluent-system';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { AgentTokenBreakdown } from '../../components/runs/AgentTokenBreakdown';

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

export function ObservabilityAgentsPage() {
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

  const breakdown = useMemo<RunAgentTokenBreakdownDto | null>(() => {
    if (!metrics) return null;
    const rows = metrics.agentBreakdown ?? [];
    return {
      runId: projectId ?? 'observability',
      source: 'app_insights',
      hasAgentData: rows.length > 0,
      totalTokens: rows.reduce((sum, row) => sum + row.totalTokens, 0),
      totalNanoAiu: rows.reduce((sum, row) => sum + row.totalNanoAiu, 0),
      breakdown: rows,
    };
  }, [metrics, projectId]);

  const summary = useMemo(() => {
    const rows = breakdown?.breakdown ?? [];
    return {
      agents: rows.length,
      invocations: rows.reduce((sum, row) => sum + row.invocationCount, 0),
      totalTokens: breakdown?.totalTokens ?? 0,
      totalAiu: breakdown?.totalNanoAiu ?? 0,
    };
  }, [breakdown]);

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="agents"
      title="Observability"
      subtitle="Azure Monitor-style token and AI credit usage aggregated by agent."
    >
      <div className={['azf-surface azf-surface--panel azf-surface--padding-compact', styles.commandSurface].join(' ')}>
        <CommandBar
          title="Agents command band"
          description="Slice cross-run usage by project scope and telemetry window."
          primaryActions={[{
            id: 'refresh-agents',
            label: 'Refresh',
            icon: <ArrowSyncRegular />,
            onClick: () => setReloadKey((value) => value + 1),
          }]}
        >
          <Badge appearance="tint" color={summary.agents > 0 ? 'success' : 'warning'}>
            {summary.agents > 0 ? 'Agent dimensions available' : 'No agent dimensions'}
          </Badge>
        </CommandBar>
        <FilterBar
          filters={[
            { id: 'scope', label: 'Scope', value: project?.name ?? projectId, selected: true },
            { id: 'metric', label: 'Metric', value: 'Tokens + AI credits', selected: true },
            { id: 'range', label: 'Window', value: rangeLabel(range), selected: true },
          ]}
        >
          <Select
            aria-label="Agent observability time range"
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

      <section className={styles.dataSection} aria-label="Agent observability status">
        <BladeHeader
          size="compact"
          title="Agent resource summary"
          subtitle="Usage dimensions grouped by agent identity for the selected telemetry range."
          resourceIcon={<Bot24Regular />}
          menuLabel={<Badge appearance="outline">{rangeLabel(range)}</Badge>}
          loading={loading}
        />
        <div className={styles.summaryGrid}>
          <SummaryTile label="Agents" value={String(summary.agents)} detail="Rows with usage" tone={summary.agents > 0 ? 'success' : 'warning'} />
          <SummaryTile label="Invocations" value={compactNumber(summary.invocations)} detail="Agent turns" tone={summary.invocations > 0 ? 'success' : 'info'} />
          <SummaryTile label="Tokens" value={compactNumber(summary.totalTokens)} detail="Input + output" tone={summary.totalTokens > 0 ? 'success' : 'warning'} />
          <SummaryTile label="AI credits" value={formatAiu(summary.totalAiu)} detail="AppInsights cost" tone={summary.totalAiu > 0 ? 'success' : 'info'} />
        </div>
      </section>

      {loading && !metrics ? (
        <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable azf-stack', styles.loadingSurface].join(' ')} aria-live="polite">
          <Spinner label="Loading agent observability" />
        </div>
      ) : (
        <section className={styles.dataSection} aria-label="Agent token breakdown data">
          <BladeHeader size="compact" title="Data sections" subtitle="Per-agent progress bars use Azure Fluent layered surfaces and status copy." />
          <AgentTokenBreakdown
            data={breakdown}
            title="Agent token breakdown"
            subtitle="Aggregated AI credit and token usage across project runs."
          />
        </section>
      )}
    </ObservabilityLayout>
  );
}
