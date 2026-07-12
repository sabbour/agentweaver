import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, ProjectMetricsDto, RunAgentTokenBreakdownDto } from '../../api/types';
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
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { AgentTokenBreakdown } from '../../components/runs/AgentTokenBreakdown';
import { AiCredits } from '../../components/AiCredits';
import {
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

function compactNumber(value: number): string {
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value);
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
      description="Token and AI credit usage aggregated by agent."
    >
      <PageSection
        title="Agent usage summary"
        description={"Usage dimensions grouped by agent identity. ${rangeLabel(range)}."}
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS }}>
            <Badge appearance="tint" color={summary.agents > 0 ? 'success' : 'warning'}>
              {summary.agents > 0 ? 'Agent dimensions available' : 'No agent dimensions'}
            </Badge>
            <Select
              aria-label="Agent observability time range"
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
          <StatTile label="Agents" value={String(summary.agents)} hint="Rows with usage" />
          <StatTile label="Invocations" value={compactNumber(summary.invocations)} hint="Agent turns" />
          <StatTile label="Tokens" value={compactNumber(summary.totalTokens)} hint="Input + output" />
          <StatTile label="AI credits" value={<AiCredits totalNanoAiu={summary.totalAiu} plain showZero />} hint="Usage" />
        </div>
      </PageSection>

      {loading && !metrics ? (
        <LoadingState label="Loading agent observability" />
      ) : (
        <PageSection title="Agent token breakdown" description="Per-agent token and credit breakdown for the selected time range.">
          <AgentTokenBreakdown
            data={breakdown}
            title="Agent token breakdown"
            subtitle="Aggregated AI credit and token usage across project runs."
          />
        </PageSection>
      )}
    </ObservabilityLayout>
  );
}