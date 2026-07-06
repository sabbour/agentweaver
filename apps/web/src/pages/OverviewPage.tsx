import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, MessageBar, MessageBarBody, Select, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { AlertRegular, ArrowSyncRegular, BotRegular, BranchRegular, CheckmarkCircleRegular, ClockRegular, CodeRegular, ErrorCircleRegular, FolderRegular, InfoRegular, OpenRegular, PlayRegular, WarningRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { BoardDto, ModelUsageBreakdownDto, OverviewDto, Project, ProjectMetricsDto, RecentActivityDto } from '../api/types';
import { costChipLabel } from '../components/CostChip';
import { MetricCardHeader, MetricEmptyState, MetricSectionHeading } from '../components/MetricTypography';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';

const REFRESH_MS = 10000;
type TimeRange = '7d' | '30d' | '90d';
type ProjectStatus = 'active' | 'queued' | 'idle';

interface ProjectRollup { project: Project; activeCount: number; queuedCount: number; lastActivityUtc: string | null; agentCount: number | null; runCount: number | null; issueCount: number | null }
interface AttentionItem { key: string; severity: 'error' | 'warning'; title: string; subtitle: string; time?: string | null; action: string; to: string }
const modelColors = ['#0f6cbd', '#107c10', '#ca5010', '#5c2e91', '#8a8886'];

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXL },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  section: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, alignItems: 'center', flexWrap: 'wrap' },
  sectionActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  link: { color: tokens.colorBrandForeground1, textDecoration: 'none', fontWeight: tokens.fontWeightSemibold },
  projectGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: tokens.spacingHorizontalL },
  projectCard: { minHeight: '190px', display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, boxShadow: tokens.shadow4, color: tokens.colorNeutralForeground1, textDecoration: 'none' },
  projectTop: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, alignItems: 'flex-start' },
  projectIdentity: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr)', gap: tokens.spacingHorizontalS, alignItems: 'start' },
  iconBubble: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorBrandForeground1, display: 'grid', placeItems: 'center' },
  projectName: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  statusPill: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, whiteSpace: 'nowrap' },
  dot: { width: '8px', height: '8px', borderRadius: tokens.borderRadiusCircular, display: 'inline-block' },
  dotActive: { backgroundColor: tokens.colorPaletteGreenForeground1 }, dotIdle: { backgroundColor: tokens.colorNeutralForeground4 }, dotQueued: { backgroundColor: tokens.colorStatusWarningForeground1 },
  lastActivity: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  activityLine: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, minWidth: 0 },
  successIcon: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 }, waitingIcon: { color: tokens.colorStatusWarningForeground1, flexShrink: 0 },
  statRow: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: tokens.spacingHorizontalS, paddingTop: tokens.spacingVerticalS, borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  stat: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 }, statDanger: { color: tokens.colorPaletteRedForeground1 },
  usageGrid: { display: 'grid', gridTemplateColumns: 'repeat(5, minmax(180px, 1fr))', gap: tokens.spacingHorizontalM },
  usageTile: { minHeight: '210px', display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalM, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  modelRow: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center' }, modelName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  barTrack: { height: '6px', marginTop: tokens.spacingVerticalXXS, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusCircular, overflow: 'hidden' }, bar: { height: '100%', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorBrandForeground1 },
  donutWrap: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM }, donut: { width: '82px', height: '82px', borderRadius: '50%', flexShrink: 0 },
  legend: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 }, legendItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 }, swatch: { width: '9px', height: '9px', borderRadius: tokens.borderRadiusCircular, flexShrink: 0 },
  percentileGrid: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: tokens.spacingHorizontalS }, percentile: { padding: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2, textAlign: 'center' }, percentileValue: { display: 'block', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  bigNumber: { display: 'block', fontSize: tokens.fontSizeHero800, lineHeight: tokens.lineHeightHero800, fontWeight: tokens.fontWeightSemibold },
  mainGrid: { display: 'grid', gridTemplateColumns: 'minmax(0, 3fr) minmax(280px, 2fr)', gap: tokens.spacingHorizontalL, alignItems: 'start' },
  panel: { padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge },
  timeline: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, margin: 0, padding: 0, listStyle: 'none' }, dayLabel: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  timelineItem: { display: 'grid', gridTemplateColumns: '32px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalM, alignItems: 'start' }, timelineIcon: { width: '32px', height: '32px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorBrandForeground1, display: 'grid', placeItems: 'center' }, timelineTitle: { fontWeight: tokens.fontWeightSemibold }, timelineBadges: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXXS },
  attentionList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS }, alertCard: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center', padding: tokens.spacingVerticalM, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 }, alertError: { border: `1px solid ${tokens.colorStatusDangerBorder1}`, backgroundColor: tokens.colorStatusDangerBackground2 }, alertWarning: { border: `1px solid ${tokens.colorStatusWarningBorder1}`, backgroundColor: tokens.colorStatusWarningBackground2 }, alertErrorIcon: { color: tokens.colorStatusDangerForeground1, fontSize: '22px' }, alertWarningIcon: { color: tokens.colorStatusWarningForeground1, fontSize: '22px' },
  emptyBox: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground3, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
});
function timeAgo(iso?: string | null): string {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  const diff = Date.now() - then;
  if (Number.isNaN(diff)) return '';
  const s = Math.max(0, Math.round(diff / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}
function timeRangeDates(range: TimeRange, previous = false): { from: string; to: string } {
  const days = range === '7d' ? 7 : range === '30d' ? 30 : 90;
  const to = new Date();
  if (previous) to.setDate(to.getDate() - days);
  const from = new Date(to); from.setDate(from.getDate() - days + 1); from.setUTCHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}
function formatError(err: unknown): string { return err instanceof ApiError ? `API error ${err.status}: ${err.body}` : err instanceof Error ? err.message : String(err); }
function humanizeKind(kind: string): string { const spaced = (kind || 'activity').replace(/_/g, ' '); return spaced.charAt(0).toUpperCase() + spaced.slice(1); }
function projectSlug(project: Project): string {
  if (!project.source_repository) return 'Internal Project';
  const source = project.source_repository.replace(/\.git$/i, '');
  const match = source.match(/github\.com[:/](?<owner>[^/]+)\/(?<repo>[^/]+)$/i);
  if (match?.groups) return `${match.groups.owner}/${match.groups.repo}`;
  const parts = source.split(/[\\/]/).filter(Boolean);
  return parts.length >= 2 ? `${parts.at(-2)}/${parts.at(-1)}` : source;
}
function statusFor(rollup: ProjectRollup): ProjectStatus { if (rollup.activeCount > 0) return 'active'; if (rollup.queuedCount > 0) return 'queued'; return 'idle'; }
function isFailure(kind: string): boolean { return ['failed', 'merge_failed', 'declined', 'rai_flagged'].includes(kind); }
function activityIcon(kind: string) { if (kind.includes('merge') || kind.includes('pr')) return <BranchRegular />; if (kind.includes('deploy')) return <PlayRegular />; if (isFailure(kind)) return <ErrorCircleRegular />; if (kind === 'completed') return <CheckmarkCircleRegular />; return <CodeRegular />; }
function emptyState(message: string, styles: ReturnType<typeof useStyles>) { return <div className={styles.emptyBox}><InfoRegular /><Text>{message}</Text></div>; }
function boardIssueCount(board: BoardDto | null): number | null { return board ? board.columns.reduce((sum, column) => sum + column.cards.filter((card) => card.kind === 'task').length, 0) : null; }
function boardRunCount(board: BoardDto | null): number | null { return board ? board.columns.reduce((sum, column) => sum + column.cards.filter((card) => card.kind === 'run').length, 0) : null; }
function aggregateModelUsage(metrics: ProjectMetricsDto[]): ModelUsageBreakdownDto[] {
  const byModel = new Map<string, ModelUsageBreakdownDto>();
  for (const metric of metrics) for (const row of metric.modelUsage ?? []) { const existing = byModel.get(row.model) ?? { model: row.model, invocationCount: 0, totalNanoAiu: 0 }; existing.invocationCount += row.invocationCount; existing.totalNanoAiu += row.totalNanoAiu; byModel.set(row.model, existing); }
  return [...byModel.values()].sort((a, b) => b.totalNanoAiu - a.totalNanoAiu);
}
function aggregatePercentiles(metrics: ProjectMetricsDto[], selector: (metric: ProjectMetricsDto) => ProjectMetricsDto['responseDuration']): { p50: number | null; p95: number | null } | null {
  const rows = metrics.flatMap((metric) => selector(metric) ?? []);
  if (rows.length === 0) return null;
  const avg = (values: Array<number | null | undefined>) => { const present = values.filter((value): value is number => typeof value === 'number'); return present.length ? present.reduce((sum, value) => sum + value, 0) / present.length : null; };
  return { p50: avg(rows.map((row) => row.p50Ms)), p95: avg(rows.map((row) => row.p95Ms)) };
}
function formatMs(ms: number | null | undefined): string { if (ms == null) return '—'; return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)}s`; }
function aggregateSuccessRate(metrics: ProjectMetricsDto[]): { rate: number | null; basis: number } {
  let weighted = 0; let total = 0;
  for (const metric of metrics) for (const row of metric.leaderboard ?? []) if (row.runsTotal > 0) { weighted += row.successRate * row.runsTotal; total += row.runsTotal; }
  return { rate: total ? weighted / total : null, basis: total };
}
function aggregateTrend(metrics: ProjectMetricsDto[]): number[] {
  const byDate = new Map<string, number>();
  for (const metric of metrics) for (const point of metric.invocationTrend ?? []) byDate.set(point.date, (byDate.get(point.date) ?? 0) + point.count);
  return [...byDate.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([, count]) => count);
}
function Sparkline({ points }: { points: number[] }) {
  if (!points.length) return null;
  const width = 160; const height = 42; const max = Math.max(1, ...points);
  const path = points.map((point, index) => { const x = points.length <= 1 ? 0 : (index / (points.length - 1)) * width; const y = height - (point / max) * (height - 4) - 2; return `${index === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`; }).join(' ');
  return <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} aria-label="Success trend"><path d={path} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2} /></svg>;
}
function Donut({ rows }: { rows: ModelUsageBreakdownDto[] }) {
  const styles = useStyles(); const total = rows.reduce((sum, row) => sum + row.invocationCount, 0); if (total <= 0) return null;
  let start = 0; const segments = rows.slice(0, 5).map((row, index) => { const pct = (row.invocationCount / total) * 100; const segment = `${modelColors[index % modelColors.length]} ${start}% ${start + pct}%`; start += pct; return segment; });
  return <div style={{ background: `conic-gradient(${segments.join(', ')})` }} className={styles.donut} aria-label="Model usage distribution" />;
}
function UsageTiles({ metrics, previousMetrics, range, recentProjectId }: { metrics: ProjectMetricsDto[]; previousMetrics: ProjectMetricsDto[]; range: TimeRange; recentProjectId?: string }) {
  const styles = useStyles();
  const usage = aggregateModelUsage(metrics);
  const totalInvocations = usage.reduce((sum, row) => sum + row.invocationCount, 0);
  const response = aggregatePercentiles(metrics, (metric) => metric.responseDuration);
  const ttft = aggregatePercentiles(metrics, (metric) => metric.timeToFirstToken);
  const success = aggregateSuccessRate(metrics);
  const previous = aggregateSuccessRate(previousMetrics);
  const delta = success.rate != null && previous.rate != null ? success.rate - previous.rate : null;
  const detailsLink = recentProjectId ? `/projects/${recentProjectId}/observability` : '/projects';
  return <div className={styles.usageGrid}>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Token consumption by model" subtitle={`AIC in the selected ${range} range.`} />
      {usage.length === 0 ? <MetricEmptyState>No data yet.</MetricEmptyState> : <div className={styles.list}>{usage.slice(0, 4).map((row) => <div key={row.model}><div className={styles.modelRow}><Text className={styles.modelName}>{row.model}</Text><Text>{costChipLabel(row.totalNanoAiu, 0) ?? '—'}</Text></div><div className={styles.barTrack}><div className={styles.bar} style={{ width: `${Math.max(4, (row.totalNanoAiu / Math.max(1, usage[0].totalNanoAiu)) * 100)}%` }} /></div></div>)}</div>}
      <Link className={styles.link} to={detailsLink}>View details →</Link>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Model usage distribution" subtitle="Share by usage events." />
      {usage.length === 0 ? <MetricEmptyState>No data yet.</MetricEmptyState> : <div className={styles.donutWrap}><Donut rows={usage} /><div className={styles.legend}>{usage.slice(0, 5).map((row, index) => <span key={row.model} className={styles.legendItem}><span className={styles.swatch} style={{ backgroundColor: modelColors[index % modelColors.length] }} /><span className={styles.modelName}>{row.model}</span><span>{totalInvocations > 0 ? Math.round((row.invocationCount / totalInvocations) * 100) : 0}%</span></span>)}</div></div>}
      <Link className={styles.link} to={detailsLink}>View details →</Link>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Response duration" subtitle="Latency percentiles from telemetry." />
      {!response ? <MetricEmptyState><InfoRegular /> No data yet.</MetricEmptyState> : <div className={styles.percentileGrid}><div className={styles.percentile}><Text className={styles.percentileValue}>{formatMs(response.p50)}</Text><Text className={styles.muted}>P50</Text></div><div className={styles.percentile}><Text className={styles.percentileValue}>{formatMs(response.p95)}</Text><Text className={styles.muted}>P95</Text></div><div className={styles.percentile}><Text className={styles.percentileValue}>—</Text><Text className={styles.muted}>P99</Text></div></div>}
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Time to first token (TTFT)" subtitle="First-token latency percentiles." />
      {!ttft ? <MetricEmptyState><InfoRegular /> No data yet.</MetricEmptyState> : <div className={styles.percentileGrid}><div className={styles.percentile}><Text className={styles.percentileValue}>{formatMs(ttft.p50)}</Text><Text className={styles.muted}>P50</Text></div><div className={styles.percentile}><Text className={styles.percentileValue}>{formatMs(ttft.p95)}</Text><Text className={styles.muted}>P95</Text></div><div className={styles.percentile}><Text className={styles.percentileValue}>—</Text><Text className={styles.muted}>P99</Text></div></div>}
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Success rate" subtitle="Weighted by agent run totals." />
      {success.rate == null ? <MetricEmptyState>No data yet.</MetricEmptyState> : <><Text className={styles.bigNumber}>{Math.round(success.rate)}%</Text><Sparkline points={aggregateTrend(metrics)} /><Text className={styles.muted}>{delta == null ? 'Previous range unavailable' : `${delta >= 0 ? '+' : ''}${delta.toFixed(1)} pts vs previous range`} · {success.basis} runs</Text></>}
    </div>
  </div>;
}

export function OverviewPage() {
  const styles = useStyles();
  const [overview, setOverview] = useState<OverviewDto | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [rollups, setRollups] = useState<ProjectRollup[]>([]);
  const [metrics, setMetrics] = useState<ProjectMetricsDto[]>([]);
  const [previousMetrics, setPreviousMetrics] = useState<ProjectMetricsDto[]>([]);
  const [range, setRange] = useState<TimeRange>('30d');
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal: { cancelled: boolean }) => {
    if (!signal.cancelled) setRefreshing(true);
    try {
      const [overviewDto, projectList] = await Promise.all([apiClient.getOverview(), apiClient.listProjects()]);
      const activeById = new Map(overviewDto.active_projects.map((project) => [project.project_id, project]));
      const recent = [...projectList].sort((a, b) => new Date(activeById.get(b.project_id)?.last_activity_utc ?? b.updated_at).getTime() - new Date(activeById.get(a.project_id)?.last_activity_utc ?? a.updated_at).getTime()).slice(0, 4);
      const currentRange = timeRangeDates(range); const previousRange = timeRangeDates(range, true);
      const rollupResults = await Promise.all(recent.map(async (project): Promise<ProjectRollup> => {
        const [teamResult, runsResult, boardResult] = await Promise.allSettled([apiClient.getTeam(project.project_id), apiClient.getProjectRuns(project.project_id, { includeChildren: true, limit: 100 }), apiClient.getBoard(project.project_id)]);
        const board = boardResult.status === 'fulfilled' ? boardResult.value : null; const active = activeById.get(project.project_id);
        return { project, activeCount: active?.active_count ?? 0, queuedCount: active?.queued_count ?? 0, lastActivityUtc: active?.last_activity_utc ?? project.updated_at, agentCount: teamResult.status === 'fulfilled' ? teamResult.value.members.length : null, runCount: runsResult.status === 'fulfilled' ? Math.max(runsResult.value.length, boardRunCount(board) ?? 0) : boardRunCount(board), issueCount: boardIssueCount(board) };
      }));
      const [metricResults, previousMetricResults] = await Promise.all([Promise.allSettled(recent.map((project) => apiClient.getProjectMetrics(project.project_id, currentRange.from, currentRange.to))), Promise.allSettled(recent.map((project) => apiClient.getProjectMetrics(project.project_id, previousRange.from, previousRange.to)))]);
      if (!signal.cancelled) { setOverview(overviewDto); setProjects(projectList); setRollups(rollupResults); setMetrics(metricResults.flatMap((result) => result.status === 'fulfilled' ? [result.value] : [])); setPreviousMetrics(previousMetricResults.flatMap((result) => result.status === 'fulfilled' ? [result.value] : [])); setLastUpdated(new Date().toISOString()); setError(null); }
    } catch (err) { if (!signal.cancelled) setError(formatError(err)); }
    finally { if (!signal.cancelled) { setLoading(false); setRefreshing(false); } }
  }, [range]);

  useEffect(() => { const signal = { cancelled: false }; setLoading(true); void load(signal); const iv = setInterval(() => { void load(signal); }, REFRESH_MS); return () => { signal.cancelled = true; clearInterval(iv); }; }, [load]);

  const attention = useMemo<AttentionItem[]>(() => {
    const items: AttentionItem[] = [];
    if (overview?.at_a_glance.health && overview.at_a_glance.health !== 'healthy') items.push({ key: 'health', severity: 'error', title: 'System health degraded', subtitle: 'Overview health is reporting degraded.', action: 'View projects', to: '/projects' });
    for (const activity of overview?.recent_activity ?? []) if (isFailure(activity.kind)) items.push({ key: `${activity.project_id}-${activity.timestamp_utc}-${activity.kind}`, severity: 'error', title: activity.label, subtitle: activity.project_name, time: activity.timestamp_utc, action: 'View logs', to: `/projects/${activity.project_id}/orchestrations` });
    for (const rollup of rollups) if (rollup.queuedCount > 0) items.push({ key: `queued-${rollup.project.project_id}`, severity: 'warning', title: `${rollup.queuedCount} queued ${rollup.queuedCount === 1 ? 'item' : 'items'}`, subtitle: rollup.project.name, time: rollup.lastActivityUtc, action: 'Open project', to: `/projects/${rollup.project.project_id}` });
    return items.slice(0, 5);
  }, [overview, rollups]);
  const groupedActivity = useMemo(() => { const today = new Date().toDateString(); const groups = new Map<string, RecentActivityDto[]>(); for (const item of overview?.recent_activity ?? []) { const label = new Date(item.timestamp_utc).toDateString() === today ? 'Today' : new Date(item.timestamp_utc).toLocaleDateString(); groups.set(label, [...(groups.get(label) ?? []), item]); } return [...groups.entries()]; }, [overview]);
  return <div className={styles.root}>
    <PageHeader title="Overview" subtitle="A live command center for projects, AI performance, activity, and work that needs attention." actions={<>{lastUpdated && <RefreshCountdown className={styles.generated} intervalMs={REFRESH_MS} lastRefreshedAt={new Date(lastUpdated)} refreshing={refreshing} />}{refreshing && overview && <Spinner size="extra-tiny" aria-label="Refreshing" />}<Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={() => { setLoading(true); void load({ cancelled: false }); }}>Refresh</Button></>} />
    {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
    {loading && !overview && <Spinner label="Loading overview" />}
    {!loading || overview ? <>
      <section className={styles.section}>
        <div className={styles.sectionHeader}><MetricSectionHeading title="Recent Projects" /><Link className={styles.link} to="/projects">View all projects →</Link></div>
        {rollups.length === 0 ? emptyState(projects.length === 0 ? 'No projects yet.' : 'No recent projects to show.', styles) : <div className={styles.projectGrid}>{rollups.map((rollup) => {
          const status = statusFor(rollup); const lastDone = rollup.activeCount === 0 && rollup.queuedCount === 0;
          return <Link key={rollup.project.project_id} to={`/projects/${rollup.project.project_id}`} className={styles.projectCard}>
            <div className={styles.projectTop}><div className={styles.projectIdentity}><span className={styles.iconBubble}>{rollup.project.origin === 'github' ? <BranchRegular /> : <FolderRegular />}</span><div><Text className={styles.projectName}>{rollup.project.name}</Text><Text className={styles.muted}>{projectSlug(rollup.project)}</Text></div></div><Badge appearance="tint" color={status === 'active' ? 'success' : status === 'queued' ? 'warning' : 'subtle'}><span className={styles.statusPill}><span className={`${styles.dot} ${status === 'active' ? styles.dotActive : status === 'queued' ? styles.dotQueued : styles.dotIdle}`} />{status.charAt(0).toUpperCase() + status.slice(1)}</span></Badge></div>
            <div className={styles.lastActivity}><Text className={styles.muted}>Last activity</Text><div className={styles.activityLine}>{lastDone ? <CheckmarkCircleRegular className={styles.successIcon} /> : <ClockRegular className={styles.waitingIcon} />}<Text className={styles.projectName}>{lastDone ? 'No active work' : rollup.activeCount > 0 ? `${rollup.activeCount} active run${rollup.activeCount === 1 ? '' : 's'}` : `${rollup.queuedCount} queued item${rollup.queuedCount === 1 ? '' : 's'}`}</Text><Text className={styles.muted}>{timeAgo(rollup.lastActivityUtc)}</Text></div></div>
            <div className={styles.statRow}><span className={styles.stat}><BotRegular />{rollup.agentCount ?? '—'} agents</span><span className={styles.stat}><PlayRegular />{rollup.runCount ?? '—'} runs</span><span className={`${styles.stat} ${(rollup.issueCount ?? 0) > 0 ? styles.statDanger : ''}`}><AlertRegular />{rollup.issueCount ?? '—'} issues</span></div>
          </Link>;
        })}</div>}
      </section>
      <section className={styles.section}>
        <div className={styles.sectionHeader}><MetricSectionHeading title="AI Usage & Performance" subtitle="Aggregated from existing project observability metrics for the recent projects shown above." /><div className={styles.sectionActions}><Text>Range</Text><Select value={range} onChange={(_e, data) => setRange(data.value as TimeRange)} aria-label="AI usage time range" size="small" style={{ width: '120px' }}><option value="7d">Last 7 days</option><option value="30d">Last 30 days</option><option value="90d">Last 90 days</option></Select></div></div>
        <UsageTiles metrics={metrics} previousMetrics={previousMetrics} range={range} recentProjectId={rollups[0]?.project.project_id} />
      </section>
      <div className={styles.mainGrid}>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><MetricSectionHeading title="Activity Feed" /><Select aria-label="Activity filter" defaultValue="all" size="small" style={{ width: '132px' }}><option value="all">All activity</option></Select></div>
          {groupedActivity.length === 0 ? emptyState('No recent activity.', styles) : <div className={styles.timeline}>{groupedActivity.map(([day, entries]) => <div key={day} className={styles.list}><Text className={styles.dayLabel}>{day}</Text>{entries.map((entry, index) => <div key={`${entry.project_id}-${entry.timestamp_utc}-${index}`} className={styles.timelineItem}><span className={styles.timelineIcon}>{activityIcon(entry.kind)}</span><div><Text className={styles.timelineTitle}>{entry.project_name}</Text><Text> {entry.label}</Text><div className={styles.timelineBadges}><Badge appearance="tint" color={isFailure(entry.kind) ? 'danger' : entry.kind === 'completed' ? 'success' : 'informative'}>{humanizeKind(entry.kind)}</Badge></div></div><Text className={styles.muted}>{timeAgo(entry.timestamp_utc)}</Text></div>)}</div>)}</div>}
          <Link className={styles.link} to="/overview">View all activity →</Link>
        </section>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><MetricSectionHeading title="Needs Attention" /></div>
          {attention.length === 0 ? emptyState('Nothing needs attention.', styles) : <div className={styles.attentionList}>{attention.map((item) => <div key={item.key} className={`${styles.alertCard} ${item.severity === 'error' ? styles.alertError : styles.alertWarning}`}>{item.severity === 'error' ? <ErrorCircleRegular className={styles.alertErrorIcon} /> : <WarningRegular className={styles.alertWarningIcon} />}<div><Text weight="semibold">{item.title}</Text><br /><Text className={styles.muted}>{item.subtitle}{item.time ? ` · ${timeAgo(item.time)}` : ''}</Text></div><Button as="a" href={item.to} appearance="secondary" size="small" icon={<OpenRegular />}>{item.action}</Button></div>)}</div>}
        </section>
      </div>
    </> : null}
  </div>;
}
