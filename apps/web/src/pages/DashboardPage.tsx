import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { AgentLeaderboardEntryDto, ProjectDashboardDto, ThroughputPointDto, TokenUsageSummary } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { TokenUsagePanel } from '../components/TokenUsagePanel';
import { formatAic } from '../components/CostChip';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';

// Dashboard — the project HOME (/projects/:projectId). Consumes the live
// GET /api/projects/{id}/dashboard endpoint (real data only; no cost). Renders
// overview counters, a 30-day throughput chart, and an agent leaderboard.
// Workflow-health is intentionally not rendered: the backend omits it because a
// run row carries no workflow-definition reference (see MetricsDtos.cs).

const REFRESH_MS = 30000;

type TimeRange = '7d' | '30d' | '90d';

type AgentCost = { totalNanoAiu: number; totalTokens: number };

function timeRangeDates(range: TimeRange): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to);
  if (range === '7d') from.setDate(from.getDate() - 7);
  else if (range === '30d') from.setDate(from.getDate() - 30);
  else from.setDate(from.getDate() - 90);
  return { from: from.toISOString(), to: to.toISOString() };
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
  cards: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  cardLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
  },
  cardValue: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  panel: {
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  legend: {
    display: 'flex',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
    marginBottom: tokens.spacingVerticalS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  swatch: {
    width: '12px',
    height: '12px',
    borderRadius: tokens.borderRadiusSmall,
    display: 'inline-block',
  },
  generated: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  leaderboardPanel: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflowX: 'auto',
  },
  sharedMetricsHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  filterGroup: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  leaderboardTable: {
    minWidth: '860px',
  },
  headerCell: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  agentCell: {
    fontWeight: tokens.fontWeightSemibold,
  },
  roleCell: {
    color: tokens.colorNeutralForeground2,
  },
  successCell: {
    display: 'flex',
    justifyContent: 'flex-start',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  successBasis: {
    minWidth: '34px',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  metricNote: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
});

function formatDuration(ms: number | null): string {
  if (ms == null) return '—';
  const seconds = ms / 1000;
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(1)}m`;
  const hours = minutes / 60;
  return `${hours.toFixed(1)}h`;
}

function successBadgeColor(rate: number): 'success' | 'warning' | 'danger' {
  if (rate >= 0.8) return 'success';
  if (rate >= 0.5) return 'warning';
  return 'danger';
}

function formatSuccessRate(row: AgentLeaderboardEntryDto): string {
  if (row.terminal_runs === 0) return '—';
  return `${Math.round(row.success_rate * 100)}%`;
}

// Lightweight dependency-free SVG line chart for the throughput series.
function ThroughputChart({ points }: { points: ThroughputPointDto[] }) {
  const W = 720;
  const H = 200;
  const pad = { top: 12, right: 12, bottom: 24, left: 28 };
  const innerW = W - pad.left - pad.right;
  const innerH = H - pad.top - pad.bottom;

  const maxVal = Math.max(1, ...points.map((p) => Math.max(p.created, p.done)));
  const n = points.length;
  const x = (i: number) => pad.left + (n <= 1 ? 0 : (i / (n - 1)) * innerW);
  const y = (v: number) => pad.top + innerH - (v / maxVal) * innerH;

  const toPath = (sel: (p: ThroughputPointDto) => number) =>
    points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(sel(p)).toFixed(1)}`).join(' ');

  const createdColor = tokens.colorBrandForeground1;
  const doneColor = tokens.colorPaletteGreenForeground1;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" height={H} role="img" aria-label="Throughput over the last 30 days">
      {/* baseline */}
      <line x1={pad.left} y1={pad.top + innerH} x2={pad.left + innerW} y2={pad.top + innerH}
        stroke={tokens.colorNeutralStroke2} strokeWidth={1} />
      <text x={pad.left} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3}>
        {points[0]?.date ?? ''}
      </text>
      <text x={pad.left + innerW} y={pad.top + innerH + 16} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {points[n - 1]?.date ?? ''}
      </text>
      <text x={pad.left - 6} y={pad.top + 8} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {maxVal}
      </text>
      <path d={toPath((p) => p.created)} fill="none" stroke={createdColor} strokeWidth={2} />
      <path d={toPath((p) => p.done)} fill="none" stroke={doneColor} strokeWidth={2} />
    </svg>
  );
}

export function DashboardPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [data, setData] = useState<ProjectDashboardDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [usageRange, setUsageRange] = useState<TimeRange>('30d');
  const [filteredUsage, setFilteredUsage] = useState<TokenUsageSummary | null>(null);
  const [usageLoading, setUsageLoading] = useState(false);
  const [agentCosts, setAgentCosts] = useState<Record<string, AgentCost>>({});

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    if (!projectId) return;
    try {
      const dto = await apiClient.getProjectDashboard(projectId);
      if (!signal.cancelled) {
        setData(dto);
        setError(null);
      }
    } catch (err) {
      if (!signal.cancelled) setError(formatError(err));
    } finally {
      if (!signal.cancelled) setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    if (!projectId) return;
    const signal = { cancelled: false };
    setLoading(true);
    void load(signal);
    const iv = setInterval(() => { void load(signal); }, REFRESH_MS);
    return () => {
      signal.cancelled = true;
      clearInterval(iv);
    };
  }, [projectId, load]);

  const loadProjectUsage = useCallback(async (range: TimeRange) => {
    if (!projectId) return;
    setUsageLoading(true);
    try {
      const { from, to } = timeRangeDates(range);
      const usage = await apiClient.getProjectUsage(projectId, from, to);
      setFilteredUsage(usage);

      const fromMs = new Date(from).getTime();
      const toMs = new Date(to).getTime();
      const runs = await apiClient.getProjectRuns(projectId, { includeChildren: true, limit: 500 });
      const scopedRuns = runs.filter((run) => {
        const started = new Date(run.started_at).getTime();
        return run.agent_name && !Number.isNaN(started) && started >= fromMs && started <= toMs;
      });
      const usageRows = await Promise.all(scopedRuns.map(async (run) => {
        try {
          return { agent: run.agent_name!, usage: await apiClient.getRunUsage(run.execution_id) };
        } catch {
          return null;
        }
      }));
      const nextCosts: Record<string, AgentCost> = {};
      for (const row of usageRows) {
        if (!row) continue;
        const prev = nextCosts[row.agent] ?? { totalNanoAiu: 0, totalTokens: 0 };
        prev.totalNanoAiu += row.usage.total_nano_aiu;
        prev.totalTokens += row.usage.total_tokens;
        nextCosts[row.agent] = prev;
      }
      setAgentCosts(nextCosts);
    } catch {
      // Usage section is supplementary — degrade gracefully.
      setFilteredUsage(null);
      setAgentCosts({});
    } finally {
      setUsageLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    void loadProjectUsage(usageRange);
  }, [usageRange, loadProjectUsage]);

  const cards = useMemo(() => {
    if (!data) return [];
    const s = data.summary;
    return [
      { label: 'Runs this week', value: s.runs_this_week },
      { label: 'Active agents', value: s.active_agents },
      { label: 'Active runs', value: s.active_runs },
      { label: 'Runs total', value: s.runs_total },
      { label: 'Tasks done (7d)', value: s.tasks_done_this_week },
    ];
  }, [data]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Dashboard"
        subtitle="Delivery metrics and the agent leaderboard."
        breadcrumb={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <span>{data?.project_name ?? projectId}</span>
          </div>
        }
        actions={
          <>
            {data && (
              <Text className={styles.generated}>
                Updated {new Date(data.generated_utc).toLocaleTimeString()}
              </Text>
            )}
            {data && (
              <RefreshCountdown
                className={styles.generated}
                intervalMs={REFRESH_MS}
                lastRefreshedAt={new Date(data.generated_utc)}
              />
            )}
            <Button
              appearance="secondary"
              icon={<ArrowSyncRegular />}
              disabled={loading}
              onClick={() => { setLoading(true); void load({ cancelled: false }); }}
            >
              Refresh
            </Button>
          </>
        }
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && !data && <Spinner label="Loading dashboard" />}

      {data && (
        <>
          <div className={styles.cards}>
            {cards.map((c) => (
              <div key={c.label} className={styles.card}>
                <Text className={styles.cardLabel}>{c.label}</Text>
                <Text className={styles.cardValue}>{c.value}</Text>
              </div>
            ))}
          </div>

          <div className={styles.section}>
            <Title3>Throughput (last 30 days)</Title3>
            <div className={styles.panel}>
              <div className={styles.legend}>
                <span className={styles.legendItem}>
                  <span className={styles.swatch} style={{ backgroundColor: tokens.colorBrandForeground1 }} />
                  Created
                </span>
                <span className={styles.legendItem}>
                  <span className={styles.swatch} style={{ backgroundColor: tokens.colorPaletteGreenForeground1 }} />
                  Done
                </span>
              </div>
              {data.throughput.length === 0 ? (
                <Text>No throughput data yet.</Text>
              ) : (
                <ThroughputChart points={data.throughput} />
              )}
            </div>
          </div>

          <div className={styles.sharedMetricsHeader}>
            <Title3>Agent and usage metrics</Title3>
            <div className={styles.filterGroup}>
              <Text className={styles.metricNote}>Range</Text>
              <Select
                value={usageRange}
                onChange={(_e, d) => setUsageRange(d.value as TimeRange)}
                aria-label="Time range"
                size="small"
                style={{ width: '120px' }}
              >
                <option value="7d">Last 7 days</option>
                <option value="30d">Last 30 days</option>
                <option value="90d">Last 90 days</option>
              </Select>
            </div>
          </div>

          <div className={styles.section}>
            <Title3>Agent leaderboard</Title3>
            <Text className={styles.metricNote}>
              Success rate = successful terminal runs / terminal runs (queued, waiting-review, and in-progress excluded).
            </Text>
            {data.agent_leaderboard.length === 0 ? (
              <Text>No agent activity yet.</Text>
            ) : (
              <div className={styles.leaderboardPanel}>
                <Table aria-label="Agent leaderboard" size="small" className={styles.leaderboardTable}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell className={styles.headerCell}>Agent</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Role</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Runs this week</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Runs total</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Success rate</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Avg duration</TableHeaderCell>
                      <TableHeaderCell className={styles.headerCell}>Cost</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.agent_leaderboard.map((row) => (
                      <TableRow key={row.agent}>
                        <TableCell>
                          <TableCellLayout className={styles.agentCell}>
                            <Link
                              to={`/projects/${projectId}/flow?agent=${encodeURIComponent(row.agent)}`}
                              className={styles.breadcrumbLink}
                            >
                              {row.agent}
                            </Link>
                          </TableCellLayout>
                        </TableCell>
                        <TableCell>
                          <TableCellLayout className={styles.roleCell}>{row.role_title ?? '—'}</TableCellLayout>
                        </TableCell>
                        <TableCell>{row.runs_this_week}</TableCell>
                        <TableCell>{row.runs_total}</TableCell>
                        <TableCell>
                          <div className={styles.successCell}>
                            <Badge
                              appearance="tint"
                              color={row.terminal_runs === 0 ? 'subtle' : successBadgeColor(row.success_rate)}
                            >
                              {formatSuccessRate(row)}
                            </Badge>
                            <Text className={styles.successBasis}>{row.successful_runs}/{row.terminal_runs}</Text>
                          </div>
                        </TableCell>
                        <TableCell>{formatDuration(row.avg_duration_ms)}</TableCell>
                        <TableCell>{agentCosts[row.agent]?.totalNanoAiu ? `${formatAic(agentCosts[row.agent].totalNanoAiu)} AIC` : '—'}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}
          </div>

          <div className={styles.section}>
            <Title3>Token and AIC usage</Title3>
            {usageLoading && <Spinner size="tiny" label="Loading usage" />}
            {!usageLoading && (filteredUsage ?? data.token_usage) && (
              <TokenUsagePanel usage={(filteredUsage ?? data.token_usage)!} title="" />
            )}
            {!usageLoading && !(filteredUsage ?? data.token_usage) && (
              <Text>No usage data available for this period.</Text>
            )}
          </div>
        </>
      )}
    </div>
  );
}
