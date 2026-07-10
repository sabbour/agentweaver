import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, MessageBar, MessageBarActions, MessageBarBody, Select, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { AlertRegular, ArrowSyncRegular, BotRegular, BranchRegular, CheckmarkCircleRegular, ClockRegular, CodeRegular, ErrorCircleRegular, FolderRegular, InfoRegular, OpenRegular, PlayRegular, WarningRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { BoardDto, ModelUsageBreakdownDto, OverviewDto, Project, ProjectMetricsDto, RecentActivityDto } from '../api/types';
import { costChipLabel } from '../components/CostChip';
import { MetricCardHeader, MetricEmptyState, MetricSectionHeading } from '../components/MetricTypography';
import { PageHeader } from '../components/PageHeader';
import { AzurePage } from '../components/azure/AzureLayout';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';

const REFRESH_MS = 10000;
type TimeRange = '7d' | '30d' | '90d';
type ProjectStatus = 'active' | 'queued' | 'idle';

interface ProjectRollup { project: Project; activeCount: number; queuedCount: number; lastActivityUtc: string | null; agentCount: number | null; runCount: number | null; issueCount: number | null }
interface AttentionItem { key: string; severity: 'error' | 'warning'; title: string; subtitle: string; time?: string | null; action: string; to: string }
const modelColors = ['#0f6cbd', '#107c10', '#ca5010', '#5c2e91', '#8a8886'];

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXXL, '@media (max-width: 720px)': { gap: tokens.spacingVerticalXL } },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  headerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap', justifyContent: 'flex-end' },
  commandStrip: { display: 'grid', gridTemplateColumns: 'minmax(220px, .95fr) minmax(0, 2fr) auto', gap: tokens.spacingHorizontalL, alignItems: 'stretch', padding: tokens.spacingVerticalL, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusXLarge, backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow4, '@media (max-width: 980px)': { gridTemplateColumns: '1fr' }, '@media (max-width: 720px)': { gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalM } },
  healthBlock: { display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: tokens.spacingVerticalM, minWidth: 0 },
  healthTitle: { display: 'block', fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1, overflowWrap: 'anywhere' },
  healthCopy: { display: 'block', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300, lineHeight: tokens.lineHeightBase300, maxWidth: '56ch' },
  glanceGrid: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(96px, 1fr))', gap: tokens.spacingHorizontalM, minWidth: 0, '@media (max-width: 720px)': { gridTemplateColumns: '1fr', gap: tokens.spacingVerticalXS } },
  glanceItem: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0, padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`, borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke3}`, '@media (max-width: 720px)': { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` } },
  glanceValue: { display: 'block', fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums', '@media (max-width: 720px)': { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600 } },
  glanceLabel: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200, whiteSpace: 'nowrap' },
  liveBlock: { display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground2, '@media (max-width: 980px)': { justifyContent: 'flex-start' } },
  section: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, alignItems: 'flex-end', flexWrap: 'wrap' },
  sectionActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  link: { color: tokens.colorBrandForeground1, textDecorationLine: 'none', fontWeight: tokens.fontWeightSemibold, minHeight: '32px', display: 'inline-flex', alignItems: 'center', ':hover': { textDecorationLine: 'underline' }, ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' } },
  projectGrid: { display: 'grid', gridTemplateColumns: 'minmax(280px, 1.1fr) minmax(320px, 1.9fr)', gap: 0, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusXLarge, backgroundColor: tokens.colorNeutralBackground1, overflow: 'hidden', boxShadow: tokens.shadow4, '@media (max-width: 900px)': { gridTemplateColumns: '1fr' } },
  focusProject: { display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: tokens.spacingVerticalL, padding: tokens.spacingVerticalXL, backgroundColor: tokens.colorNeutralBackground2, minWidth: 0 },
  projectList: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  projectCard: { minHeight: '72px', display: 'grid', gridTemplateColumns: 'minmax(220px, 1fr) max-content minmax(320px, .95fr)', alignItems: 'center', columnGap: tokens.spacingHorizontalXL, rowGap: tokens.spacingVerticalS, padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`, backgroundColor: tokens.colorNeutralBackground1, borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, color: tokens.colorNeutralForeground1, textDecorationLine: 'none', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover }, ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-2px' }, '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr) max-content' }, '@media (max-width: 720px)': { gridTemplateColumns: '1fr', alignItems: 'start', padding: tokens.spacingVerticalM } },
  projectTop: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, alignItems: 'flex-start' },
  projectIdentity: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr)', gap: tokens.spacingHorizontalS, alignItems: 'start', minWidth: 0 },
  iconBubble: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorBrandForeground1, display: 'grid', placeItems: 'center' },
  projectTextStack: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 },
  projectName: { display: 'block', minWidth: 0, fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  projectSlugText: { display: 'block', minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  statusPill: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, whiteSpace: 'nowrap' },
  dot: { width: '8px', height: '8px', borderRadius: tokens.borderRadiusCircular, display: 'inline-block' },
  dotActive: { backgroundColor: tokens.colorPaletteGreenForeground1 }, dotIdle: { backgroundColor: tokens.colorNeutralForeground4 }, dotQueued: { backgroundColor: tokens.colorStatusWarningForeground1 }, dotError: { backgroundColor: tokens.colorStatusDangerForeground1 },
  lastActivity: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  activityLine: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, minWidth: 0 },
  successIcon: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 }, waitingIcon: { color: tokens.colorStatusWarningForeground1, flexShrink: 0 },
  statRow: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: tokens.spacingHorizontalS, paddingTop: tokens.spacingVerticalS, borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  projectStats: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(72px, max-content))', justifyContent: 'end', gap: tokens.spacingHorizontalM, minWidth: 0, '@media (max-width: 1180px)': { gridColumn: '1 / -1', justifyContent: 'start', paddingLeft: '40px' }, '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', paddingLeft: 0, width: '100%' } },
  stat: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, minWidth: 0, whiteSpace: 'nowrap' }, statDanger: { color: tokens.colorPaletteRedForeground1 },
  usageGrid: { display: 'grid', gridTemplateColumns: 'minmax(320px, 1.35fr) repeat(2, minmax(220px, .65fr))', gap: tokens.spacingHorizontalM, '@media (max-width: 1100px)': { gridTemplateColumns: 'repeat(2, minmax(260px, 1fr))' }, '@media (max-width: 720px)': { gridTemplateColumns: '1fr' } },
  usageTile: { minHeight: '190px', display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, minWidth: 0 },
  usagePrimary: { gridRow: 'span 2', '@media (max-width: 1100px)': { gridRow: 'auto', gridColumn: '1 / -1' }, '@media (max-width: 720px)': { gridColumn: 'auto' } },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  modelRow: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center' }, modelName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  barTrack: { height: '6px', marginTop: tokens.spacingVerticalXXS, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusCircular, overflow: 'hidden' }, bar: { height: '100%', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorBrandForeground1, transitionProperty: 'width', transitionDuration: '180ms', transitionTimingFunction: 'cubic-bezier(0.22, 1, 0.36, 1)', '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' } },
  donutWrap: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM }, donut: { width: '82px', height: '82px', borderRadius: '50%', flexShrink: 0 },
  legend: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 }, legendItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 }, swatch: { width: '9px', height: '9px', borderRadius: tokens.borderRadiusCircular, flexShrink: 0 },
  percentileGrid: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: tokens.spacingHorizontalS }, percentile: { padding: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2, textAlign: 'center' }, percentileValue: { display: 'block', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  bigNumber: { display: 'block', fontSize: tokens.fontSizeHero800, lineHeight: tokens.lineHeightHero800, fontWeight: tokens.fontWeightSemibold },
  mainGrid: { display: 'grid', gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)', gap: tokens.spacingHorizontalL, alignItems: 'start', '@media (max-width: 980px)': { gridTemplateColumns: '1fr' } },
  panel: { padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusXLarge, minWidth: 0 },
  timeline: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, margin: 0, padding: 0, listStyle: 'none' }, dayLabel: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  timelineItem: { display: 'grid', gridTemplateColumns: '32px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalM, alignItems: 'start', minWidth: 0, '@media (max-width: 560px)': { gridTemplateColumns: '32px minmax(0, 1fr)' } }, timelineIcon: { width: '32px', height: '32px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorBrandForeground1, display: 'grid', placeItems: 'center' }, timelineTitle: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' }, timelineBadges: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXXS },
  attentionList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS }, alertCard: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center', padding: tokens.spacingVerticalM, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1, minWidth: 0, '@media (max-width: 560px)': { gridTemplateColumns: '28px minmax(0, 1fr)' } }, alertError: { border: `1px solid ${tokens.colorStatusDangerBorder1}`, backgroundColor: tokens.colorStatusDangerBackground1 }, alertWarning: { border: `1px solid ${tokens.colorStatusWarningBorder1}`, backgroundColor: tokens.colorStatusWarningBackground1 }, alertErrorIcon: { color: tokens.colorStatusDangerForeground1, fontSize: '22px' }, alertWarningIcon: { color: tokens.colorStatusWarningForeground1, fontSize: '22px' },
  emptyBox: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: tokens.spacingVerticalS, color: tokens.colorNeutralForeground2, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, maxWidth: '72ch' },
  skeletonStack: { display: 'grid', gap: tokens.spacingVerticalM },
  skeleton: { height: '88px', borderRadius: tokens.borderRadiusLarge, backgroundImage: `linear-gradient(90deg, ${tokens.colorNeutralBackground3}, ${tokens.colorNeutralBackground1}, ${tokens.colorNeutralBackground3})`, backgroundSize: '220% 100%', animationName: { '0%': { backgroundPositionX: '100%' }, '100%': { backgroundPositionX: '-100%' } }, animationDuration: '1400ms', animationIterationCount: 'infinite', animationTimingFunction: 'cubic-bezier(0.22, 1, 0.36, 1)', '@media (prefers-reduced-motion: reduce)': { animationName: 'none' } },
  wideSkeleton: { height: '180px' },
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
function emptyState(title: string, styles: ReturnType<typeof useStyles>, body?: string) {
  return <div className={styles.emptyBox} role="status"><InfoRegular /><div><Text weight="semibold">{title}</Text>{body ? <><br /><Text>{body}</Text></> : null}</div></div>;
}
function LoadingOverview() {
  const styles = useStyles();
  return <div className={styles.skeletonStack} aria-label="Loading overview" role="status"><div className={styles.skeleton} /><div className={mergeClasses(styles.skeleton, styles.wideSkeleton)} /><div className={styles.skeleton} /></div>;
}
function GlanceItem({ label, value, styles }: { label: string; value: number; styles: ReturnType<typeof useStyles> }) {
  return <div className={styles.glanceItem}><Text className={styles.glanceValue}>{value}</Text><Text className={styles.glanceLabel}>{label}</Text></div>;
}
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
  <div className={mergeClasses(styles.usageTile, styles.usagePrimary)}>
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
  const focusRollup = rollups[0];
  const health = overview?.at_a_glance.health === 'healthy' ? 'healthy' : 'degraded';
  return <AzurePage className={styles.root}>
    <PageHeader title="Overview" subtitle="A live command center for projects, AI performance, activity, and work that needs attention." actions={<div className={styles.headerActions}>{lastUpdated && <RefreshCountdown className={styles.generated} intervalMs={REFRESH_MS} lastRefreshedAt={new Date(lastUpdated)} refreshing={refreshing} />}{refreshing && overview && <Spinner size="extra-tiny" aria-label="Refreshing overview" />}<Button appearance="secondary" icon={<ArrowSyncRegular />} disabled={loading} onClick={() => { setLoading(true); void load({ cancelled: false }); }}>Refresh</Button></div>} />
    {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody><MessageBarActions><Button appearance="transparent" onClick={() => { setLoading(true); void load({ cancelled: false }); }}>Try again</Button></MessageBarActions></MessageBar>}
    {loading && !overview && <LoadingOverview />}
    {overview ? <>
      <section className={styles.commandStrip} aria-labelledby="overview-command-center">
        <div className={styles.healthBlock}><div><Text as="h2" id="overview-command-center" className={styles.healthTitle}>Operations are {health}</Text><Text className={styles.healthCopy}>Live signals from active sessions, queued work, completed runs, and recent project telemetry.</Text></div><Badge appearance="tint" color={health === 'healthy' ? 'success' : 'danger'}><span className={styles.statusPill}><span className={`${styles.dot} ${health === 'healthy' ? styles.dotActive : styles.dotError}`} />{health === 'healthy' ? 'Healthy' : 'Needs review'}</span></Badge></div>
        <div className={styles.glanceGrid}><GlanceItem label="In flight" value={overview.at_a_glance.in_flight} styles={styles} /><GlanceItem label="Queued work" value={overview.at_a_glance.queued_work} styles={styles} /><GlanceItem label="Done today" value={overview.at_a_glance.done_today} styles={styles} /><GlanceItem label="Active projects" value={overview.at_a_glance.active_projects} styles={styles} /></div>
        <div className={styles.liveBlock}><ClockRegular /><Text className={styles.muted}>Generated {timeAgo(overview.generated_utc)}</Text></div>
      </section>
      <section className={styles.section}>
        <div className={styles.sectionHeader}><MetricSectionHeading title="Recent Projects" subtitle="Most recently active repositories with operational counts and queue pressure." /><Link className={styles.link} to="/projects">View all projects →</Link></div>
        {rollups.length === 0 ? emptyState(projects.length === 0 ? 'No projects yet.' : 'No recent projects to show.', styles, projects.length === 0 ? 'Create a project to start running agents in an isolated worktree.' : 'Recent project telemetry will appear after an agent run starts.') : <div className={styles.projectGrid}>
          <div className={styles.focusProject}><MetricCardHeader title="Current focus" subtitle={focusRollup ? 'Most recently active project' : 'No project selected'} />{focusRollup ? <div className={styles.lastActivity}><Text className={styles.projectName}>{focusRollup.project.name}</Text><Text className={styles.healthCopy}>{focusRollup.activeCount > 0 ? `${focusRollup.activeCount} active run${focusRollup.activeCount === 1 ? '' : 's'} need monitoring.` : focusRollup.queuedCount > 0 ? `${focusRollup.queuedCount} queued item${focusRollup.queuedCount === 1 ? '' : 's'} waiting for capacity.` : 'No active work; ready for the next orchestration.'}</Text><Link className={styles.link} to={`/projects/${focusRollup.project.project_id}`}>Open focus project →</Link></div> : null}</div>
          <div className={styles.projectList}>{rollups.map((rollup) => {
          const status = statusFor(rollup);
          return <Link key={rollup.project.project_id} to={`/projects/${rollup.project.project_id}`} className={styles.projectCard}>
            <div className={styles.projectIdentity}><span className={styles.iconBubble}>{rollup.project.origin === 'github' ? <BranchRegular /> : <FolderRegular />}</span><div className={styles.projectTextStack}><Text className={styles.projectName}>{rollup.project.name}</Text><Text className={mergeClasses(styles.muted, styles.projectSlugText)}>{projectSlug(rollup.project)}</Text></div></div>
            <Badge appearance="tint" color={status === 'active' ? 'success' : status === 'queued' ? 'warning' : 'subtle'}><span className={styles.statusPill}><span className={`${styles.dot} ${status === 'active' ? styles.dotActive : status === 'queued' ? styles.dotQueued : styles.dotIdle}`} />{status.charAt(0).toUpperCase() + status.slice(1)}</span></Badge>
            <div className={styles.projectStats}>
              <span className={styles.stat}><ClockRegular />{timeAgo(rollup.lastActivityUtc)}</span>
              <span className={styles.stat}><BotRegular />{rollup.agentCount ?? '—'} agents</span>
              <span className={styles.stat}><PlayRegular />{rollup.runCount ?? '—'} runs</span>
              <span className={`${styles.stat} ${(rollup.issueCount ?? 0) > 0 ? styles.statDanger : ''}`}><AlertRegular />{rollup.issueCount ?? '—'} issues</span>
            </div>
          </Link>;
        })}</div></div>}
      </section>
      <section className={styles.section}>
        <div className={styles.sectionHeader}><MetricSectionHeading title="AI Usage & Performance" subtitle="Aggregated from existing project observability metrics for the recent projects shown above." /><div className={styles.sectionActions}><Text>Range</Text><Select value={range} onChange={(_e, data) => setRange(data.value as TimeRange)} aria-label="AI usage time range" size="small" style={{ width: '120px' }}><option value="7d">Last 7 days</option><option value="30d">Last 30 days</option><option value="90d">Last 90 days</option></Select></div></div>
        <UsageTiles metrics={metrics} previousMetrics={previousMetrics} range={range} recentProjectId={rollups[0]?.project.project_id} />
      </section>
      <div className={styles.mainGrid}>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><MetricSectionHeading title="Activity Feed" /><Select aria-label="Activity filter" defaultValue="all" size="small" style={{ width: '132px' }}><option value="all">All activity</option></Select></div>
          {groupedActivity.length === 0 ? emptyState('No recent activity.', styles, 'New agent starts, completions, failures, and merge events will appear here.') : <div className={styles.timeline}>{groupedActivity.map(([day, entries]) => <div key={day} className={styles.list}><Text className={styles.dayLabel}>{day}</Text>{entries.map((entry, index) => <div key={`${entry.project_id}-${entry.timestamp_utc}-${index}`} className={styles.timelineItem}><span className={styles.timelineIcon}>{activityIcon(entry.kind)}</span><div><Text className={styles.timelineTitle}>{entry.project_name}</Text><Text> {entry.label}</Text><div className={styles.timelineBadges}><Badge appearance="tint" color={isFailure(entry.kind) ? 'danger' : entry.kind === 'completed' ? 'success' : 'informative'}>{humanizeKind(entry.kind)}</Badge></div></div><Text className={styles.muted}>{timeAgo(entry.timestamp_utc)}</Text></div>)}</div>)}</div>}
        </section>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><MetricSectionHeading title="Needs Attention" subtitle="Failures, degraded health, and queue pressure that deserve the next click." /></div>
          {attention.length === 0 ? emptyState('Nothing needs attention.', styles, 'No failures or queues need action right now. Keep the overview open while agents run.') : <div className={styles.attentionList}>{attention.map((item) => <div key={item.key} className={`${styles.alertCard} ${item.severity === 'error' ? styles.alertError : styles.alertWarning}`}>{item.severity === 'error' ? <ErrorCircleRegular className={styles.alertErrorIcon} /> : <WarningRegular className={styles.alertWarningIcon} />}<div><Text weight="semibold">{item.title}</Text><br /><Text className={styles.muted}>{item.subtitle}{item.time ? ` · ${timeAgo(item.time)}` : ''}</Text></div><Button as="a" href={item.to} appearance="secondary" size="small" icon={<OpenRegular />}>{item.action}</Button></div>)}</div>}
        </section>
      </div>
    </> : null}
  </AzurePage>;
}
