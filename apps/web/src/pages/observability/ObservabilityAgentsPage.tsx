import { useEffect, useMemo, useState } from 'react';
import { Button, MessageBar, MessageBarBody, Select, Spinner } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { useParams } from 'react-router-dom';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project, ProjectMetricsDto, RunAgentTokenBreakdownDto } from '../../api/types';
import { ObservabilityLayout } from '../../components/observability/ObservabilityLayout';
import { AgentTokenBreakdown } from '../../components/runs/AgentTokenBreakdown';

type TimeRange = '7d' | '30d' | '90d';

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

export function ObservabilityAgentsPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const [project, setProject] = useState<Project | null>(null);
  const [metrics, setMetrics] = useState<ProjectMetricsDto | null>(null);
  const [range, setRange] = useState<TimeRange>('30d');
  const [reloadKey, setReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    const dates = timeRangeDates(range);
    setLoading(true);
    Promise.all([
      apiClient.getProject(projectId).catch(() => null as Project | null),
      apiClient.getProjectMetrics(projectId, dates.from, dates.to),
    ])
      .then(([projectDto, metricsDto]) => {
        setProject(projectDto);
        setMetrics(metricsDto);
        setError(null);
      })
      .catch((err) => setError(formatError(err)))
      .finally(() => setLoading(false));
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

  if (!projectId) return null;

  return (
    <ObservabilityLayout
      projectId={projectId}
      projectName={project?.name}
      activeTab="agents"
      title="Observability"
      subtitle="Cross-run token usage aggregated by agent."
      actions={(
        <>
          <Select
            aria-label="Agent observability time range"
            value={range}
            onChange={(_, data) => setRange(data.value as TimeRange)}
            size="small"
            style={{ width: '120px' }}
          >
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="90d">Last 90 days</option>
          </Select>
          <Button appearance="secondary" icon={<ArrowSyncRegular />} onClick={() => setReloadKey((value) => value + 1)}>
            Refresh
          </Button>
        </>
      )}
    >
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {loading && !metrics ? (
        <Spinner label="Loading agent observability" />
      ) : (
        <AgentTokenBreakdown
          data={breakdown}
          title="Agent token breakdown"
          subtitle="Aggregated AI credit and token usage across project runs."
        />
      )}
    </ObservabilityLayout>
  );
}
