import { gatewayClient } from '../v1/api';
import { useAuth } from '../v1/AuthContext';
import { formatCost } from '../v1/projectMetrics';
import {
  Badge,
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  mergeClasses,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { MetricCardHeader, MetricEmptyState } from '../components/MetricTypography';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import {
  EmptyState,
  PageContainer,
  PageHeader,
  PageSection,
  TitleText,
} from '../components/ui';
import {
  AlertRegular,
  ArrowSyncRegular,
  BotRegular,
  BranchRegular,
  CheckmarkCircleRegular,
  ClockRegular,
  CodeRegular,
  ErrorCircleRegular,
  FolderRegular,
  InfoRegular,
  OpenRegular,
  PlayRegular,
  Pulse24Regular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import type { ProjectMetrics, ProjectSummary, ProjectUsage, ProjectUsageCost, UserNotification } from '../v1/contracts';
import type { RecentActivityDto } from '../api/types';
const REFRESH_MS = 10000;
/** Range keys used for both the current and comparison windows. */
type TimeRange = '7d' | '30d' | '90d';
/** Coarse state derived from the active and queued counts for one project. */
type ProjectStatus = 'active' | 'queued' | 'idle' | 'unknown';

/** Dashboard rollup assembled from project, configuration, run, backlog, and metric reads. */
interface ProjectRollup { project: ProjectSummary; repository: string | null; activeCount: number | null; blockedCount: number | null; lastActivityUtc: string | null; agentCount: number | null; runCount: string | null; taskCount: number | null; activityUnavailable: boolean; activity: RecentActivityDto[] }
/** Only the fields supplied by published v1 owners; no synthetic global health or event data. */
interface OverviewData { generated_utc: string; active_projects: number; recent_activity: RecentActivityDto[]; notifications: UserNotification[]; notificationsUnavailable: boolean; metricsUnavailable: boolean; previousMetricsUnavailable: boolean; usageUnavailable: boolean }
/** Per-model measured usage. A null count means the owner could not aggregate all records. */
interface ModelUsageRow { model: string; invocationCount: number | null; tokens: number | null; cost: ProjectUsageCost[] }
/** Actionable health or queue item shown in the overview attention list. */
interface AttentionItem { key: string; severity: 'error' | 'warning'; title: string; subtitle: string; time?: string | null; action: string; to: string }
// Warm monochrome ramp (no blue) — model segments separate by lightness, not hue.
const modelColors = ['#272320', '#635c57', '#8c837c', '#b8afa8', '#d8cfc8'];

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXXL, '@media (max-width: 720px)': { gap: tokens.spacingVerticalXL } },
  generated: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  headerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM, flexWrap: 'wrap', justifyContent: 'flex-end' },
  commandStrip: { display: 'grid', gridTemplateColumns: 'minmax(220px, .95fr) minmax(0, 2fr) auto', gap: tokens.spacingHorizontalL, alignItems: 'stretch', padding: tokens.spacingVerticalL, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, '@media (max-width: 980px)': { gridTemplateColumns: '1fr' }, '@media (max-width: 720px)': { gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalM } },
  opsCard: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalL, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, minWidth: 0 },
  cardHead: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, minWidth: 0 },
  cardHeadIcon: { display: 'inline-flex', alignItems: 'center', justifyContent: 'center', color: tokens.colorNeutralForeground2, fontSize: '20px' },
  cardHeadTitle: { fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1 },
  cardHeadMeta: { display: 'inline-flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, marginLeft: 'auto', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap' },
  healthBlock: { display: 'flex', flexDirection: 'column', justifyContent: 'space-between', gap: tokens.spacingVerticalM, minWidth: 0 },
  healthTitle: { display: 'block', fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground1, overflowWrap: 'anywhere' },
  healthCopy: { display: 'block', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300, lineHeight: tokens.lineHeightBase300, maxWidth: '56ch' },
  glanceGrid: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(96px, 1fr))', gap: tokens.spacingHorizontalM, minWidth: 0, '@media (max-width: 720px)': { gridTemplateColumns: '1fr', gap: tokens.spacingVerticalXS } },
  glanceItem: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0, padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`, borderRadius: tokens.borderRadiusSmall, backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke3}`, '@media (max-width: 720px)': { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}` } },
  glanceValue: { display: 'block', fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums', '@media (max-width: 720px)': { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600 } },
  glanceLabel: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, lineHeight: tokens.lineHeightBase200, whiteSpace: 'nowrap' },
  liveBlock: { display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: tokens.spacingHorizontalS, color: tokens.colorNeutralForeground2, '@media (max-width: 980px)': { justifyContent: 'flex-start' } },
  section: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  sectionHeader: { display: 'flex', justifyContent: 'space-between', gap: tokens.spacingHorizontalM, alignItems: 'flex-end', flexWrap: 'wrap' },
  sectionActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  link: { color: tokens.colorBrandForeground1, textDecorationLine: 'none', fontWeight: tokens.fontWeightSemibold, minHeight: '32px', display: 'inline-flex', alignItems: 'center', ':hover': { textDecorationLine: 'underline' }, ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' } },
  projectGrid: { display: 'grid', gridTemplateColumns: 'minmax(280px, 1.1fr) minmax(320px, 1.9fr)', gap: 0, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, overflow: 'hidden', '@media (max-width: 900px)': { gridTemplateColumns: '1fr' } },
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
  usageTile: { minHeight: '190px', display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, minWidth: 0 },
  usagePrimary: { gridRow: 'span 2', '@media (max-width: 1100px)': { gridRow: 'auto', gridColumn: '1 / -1' }, '@media (max-width: 720px)': { gridColumn: 'auto' } },
  list: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  modelRow: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center' }, modelName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  barTrack: { height: '6px', marginTop: tokens.spacingVerticalXXS, backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusCircular, overflow: 'hidden' }, bar: { height: '100%', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorBrandForeground1, transitionProperty: 'width', transitionDuration: '180ms', transitionTimingFunction: 'cubic-bezier(0.22, 1, 0.36, 1)', '@media (prefers-reduced-motion: reduce)': { transitionDuration: '0.01ms' } },
  donutWrap: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalM }, donut: { width: '82px', height: '82px', borderRadius: '50%', flexShrink: 0 },
  legend: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS, minWidth: 0 }, legendItem: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 }, swatch: { width: '9px', height: '9px', borderRadius: tokens.borderRadiusCircular, flexShrink: 0 },
  percentileGrid: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: tokens.spacingHorizontalS }, percentile: { padding: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2, textAlign: 'center' }, percentileValue: { display: 'block', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  bigNumber: { display: 'block', fontSize: tokens.fontSizeHero800, lineHeight: tokens.lineHeightHero800, fontWeight: tokens.fontWeightSemibold },
  mainGrid: { display: 'grid', gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)', gap: tokens.spacingHorizontalL, alignItems: 'start', '@media (max-width: 980px)': { gridTemplateColumns: '1fr' } },
  panel: { padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, minWidth: 0 },
  timeline: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM, margin: 0, padding: 0, listStyle: 'none' }, dayLabel: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  timelineItem: { display: 'grid', gridTemplateColumns: '32px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalM, alignItems: 'start', minWidth: 0, '@media (max-width: 560px)': { gridTemplateColumns: '32px minmax(0, 1fr)' } }, timelineIcon: { width: '32px', height: '32px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorBrandForeground1, display: 'grid', placeItems: 'center' }, timelineTitle: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' }, timelineBadges: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXXS },
  attentionList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS }, alertCard: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr) auto', gap: tokens.spacingHorizontalS, alignItems: 'center', padding: tokens.spacingVerticalM, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1, minWidth: 0, '@media (max-width: 560px)': { gridTemplateColumns: '28px minmax(0, 1fr)' } }, alertError: { border: `1px solid ${tokens.colorStatusDangerBorder1}`, backgroundColor: tokens.colorStatusDangerBackground1 }, alertWarning: { border: `1px solid ${tokens.colorStatusWarningBorder1}`, backgroundColor: tokens.colorStatusWarningBackground1 }, alertErrorIcon: { color: tokens.colorStatusDangerForeground1, fontSize: '22px' }, alertWarningIcon: { color: tokens.colorStatusWarningForeground1, fontSize: '22px' },
  emptyBox: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: tokens.spacingVerticalS, color: tokens.colorNeutralForeground2, padding: tokens.spacingVerticalL, backgroundColor: tokens.colorNeutralBackground1, border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, maxWidth: '72ch' },
  loadingShell: { display: 'grid', gap: tokens.spacingVerticalL },
  loadingTiles: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(120px, 1fr))', gap: tokens.spacingHorizontalM, '@media (max-width: 720px)': { gridTemplateColumns: '1fr' } },
  loadingTile: { minHeight: '96px', padding: tokens.spacingVerticalL, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
  skeletonStack: { display: 'grid', gap: tokens.spacingVerticalM },
  skeleton: { height: '88px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundImage: `linear-gradient(90deg, ${tokens.colorNeutralBackground3}, ${tokens.colorNeutralBackground1}, ${tokens.colorNeutralBackground3})`, backgroundSize: '220% 100%', animationName: { '0%': { backgroundPositionX: '100%' }, '100%': { backgroundPositionX: '-100%' } }, animationDuration: '1400ms', animationIterationCount: 'infinite', animationTimingFunction: 'cubic-bezier(0.22, 1, 0.36, 1)', '@media (prefers-reduced-motion: reduce)': { animationName: 'none' } },
  skeletonSmall: { height: '16px', maxWidth: '70%', marginTop: tokens.spacingVerticalS },
  wideSkeleton: { height: '180px' },
});
/** Formats a timestamp as an age and returns an empty label for missing/invalid input. */
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
/** Builds the current or immediately preceding UTC date range for overview metrics. */
function timeRangeDates(range: TimeRange, previous = false): { from: string; to: string } {
  const days = range === '7d' ? 7 : range === '30d' ? 30 : 90;
  const to = new Date();
  if (previous) to.setDate(to.getDate() - days);
  const from = new Date(to); from.setDate(from.getDate() - days + 1); from.setUTCHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}
/** Converts API and non-API failures into text for the overview alert. */
function formatError(err: unknown): string { return err instanceof Error ? err.message : String(err); }
/** Turns event-kind identifiers into readable labels for activity rows. */
function humanizeKind(kind: string): string { const spaced = (kind || 'activity').replace(/_/g, ' '); return spaced.charAt(0).toUpperCase() + spaced.slice(1); }
/** Displays a repository owner/name when available, otherwise identifies the project as internal. */
function projectSlug(repository: string | null): string {
  // The v1 project summary has no repository URL; use the saved configuration when present.
  if (!repository) return 'Repository unavailable';
  const source = repository.replace(/\.git$/i, '');
  const match = source.match(/github\.com[:/](?<owner>[^/]+)\/(?<repo>[^/]+)$/i);
  if (match?.groups) return `${match.groups.owner}/${match.groups.repo}`;
  const parts = source.split(/[\\/]/).filter(Boolean);
  return parts.length >= 2 ? `${parts.at(-2)}/${parts.at(-1)}` : source;
}
/** Gives active work priority over queued work when presenting project status. */
function statusFor(rollup: ProjectRollup): ProjectStatus {
  // An unreadable run list is not evidence that the project is idle.
  if (rollup.activeCount === null || rollup.blockedCount === null) return 'unknown';
  if (rollup.activeCount > 0) return 'active';
  if (rollup.blockedCount > 0) return 'queued';
  return 'idle';
}
/** Identifies activity kinds treated as failed or requiring attention. */
function isFailure(kind: string): boolean { return ['failed', 'merge_failed', 'declined', 'rai_flagged'].includes(kind); }
/** Selects the activity icon from the event kind without changing its meaning. */
function activityIcon(kind: string) { if (kind.includes('merge') || kind.includes('pr')) return <BranchRegular />; if (kind.includes('deploy')) return <PlayRegular />; if (isFailure(kind)) return <ErrorCircleRegular />; if (kind === 'completed') return <CheckmarkCircleRegular />; return <CodeRegular />; }
/** Shares the product empty-state treatment across overview sections. */
function emptyState(title: string, body?: string) {
  return <EmptyState title={title} description={body} icon={<InfoRegular />} />;
}
/** Loading skeleton for the overview's health, project, usage, and activity areas. */
function LoadingOverview() {
  const styles = useStyles();
  return <div className={styles.loadingShell} aria-label="Loading overview" role="status">
    <section className={styles.commandStrip} aria-label="Overview loading">
      <div className={styles.healthBlock}>
        <div>
          <Text className={styles.healthTitle}>Preparing overview</Text>
          <Text className={styles.healthCopy}>Loading projects, recent runs, usage, and notifications.</Text>
        </div>
        <Badge appearance="tint" color="subtle">Live</Badge>
      </div>
      <div className={styles.loadingTiles}>
        {['In flight', 'Queued work', 'Done today', 'Active projects'].map((label) => (
          <div className={styles.loadingTile} key={label}>
            <Text className={styles.glanceLabel}>{label}</Text>
            <div className={mergeClasses(styles.skeleton, styles.skeletonSmall)} />
          </div>
        ))}
      </div>
      <div className={styles.liveBlock}><ClockRegular /><Text className={styles.muted}>Refreshing signals…</Text></div>
    </section>
    <div className={styles.skeletonStack}>
      <div className={mergeClasses(styles.skeleton, styles.wideSkeleton)} />
      <div className={styles.usageGrid}>
        <div className={mergeClasses(styles.skeleton, styles.wideSkeleton)} />
        <div className={styles.skeleton} />
        <div className={styles.skeleton} />
      </div>
    </div>
  </div>;
}
/** One compact operational count in the overview's glance row. */
function GlanceItem({ label, value, styles }: { label: string; value: number | string; styles: ReturnType<typeof useStyles> }) {
  return <div className={styles.glanceItem}><Text className={styles.glanceValue}>{value}</Text><Text className={styles.glanceLabel}>{label}</Text></div>;
}
/** Adds only measured counts, preserving unknown totals instead of treating null as zero. */
function sumMeasured(a: number | null, b: number | null): number | null {
  return a === null || b === null ? null : a + b;
}
/** Groups owner-reported model requests and tokens; costs keep their own units and dispositions. */
function aggregateModelUsage(usages: ProjectUsage[]): ModelUsageRow[] {
  const byModel = new Map<string, ModelUsageRow>();
  for (const usage of usages) for (const row of usage.usageByModel) {
    const existing = byModel.get(row.id);
    const tokens = row.inputTokens != null && row.outputTokens != null
      ? row.inputTokens + row.outputTokens : null;
    byModel.set(row.id, {
      model: row.id,
      invocationCount: existing ? sumMeasured(existing.invocationCount, row.requestCount) : row.requestCount,
      tokens: existing ? sumMeasured(existing.tokens, tokens) : tokens,
      // Keep amounts per owner and unit; adding arbitrary currencies or unpriced amounts is invalid.
      cost: [...(existing?.cost ?? []), ...row.cost],
    });
  }
  return [...byModel.values()].sort((a, b) => (b.tokens ?? -1) - (a.tokens ?? -1));
}
/** Calculates project-run success from completed and succeeded run counts. */
function aggregateSuccessRate(metrics: ProjectMetrics[]): { rate: number | null; basis: number } {
  const basis = metrics.reduce((sum, item) => sum + item.completedRuns, 0);
  const succeeded = metrics.reduce((sum, item) => sum + item.succeededRuns, 0);
  return { rate: basis ? succeeded / basis * 100 : null, basis };
}
/** Draws a usage-share donut for the first five model rows. */
function Donut({ rows }: { rows: ModelUsageRow[] }) {
  const styles = useStyles(); const total = rows.reduce((sum, row) => sum + (row.invocationCount ?? 0), 0); if (total <= 0) return null;
  const segments = rows.slice(0, 5).reduce<{ start: number; segments: string[] }>((acc, row, index) => {
    const pct = ((row.invocationCount ?? 0) / total) * 100;
    acc.segments.push(`${modelColors[index % modelColors.length]} ${acc.start}% ${acc.start + pct}%`);
    return { start: acc.start + pct, segments: acc.segments };
  }, { start: 0, segments: [] }).segments;
  return <div style={{ background: `conic-gradient(${segments.join(', ')})` }} className={styles.donut} aria-label="Model usage distribution" />;
}
/** Renders aggregated model usage, latency, and weighted success evidence. */
function UsageTiles({ metrics, previousMetrics, usages, range, recentProjectId, metricsUnavailable, previousMetricsUnavailable, usageUnavailable }: { metrics: ProjectMetrics[]; previousMetrics: ProjectMetrics[]; usages: ProjectUsage[]; range: TimeRange; recentProjectId?: string; metricsUnavailable: boolean; previousMetricsUnavailable: boolean; usageUnavailable: boolean }) {
  const styles = useStyles();
  const usage = aggregateModelUsage(usages);
  const totalInvocations = usage.reduce((sum, row) => sum + (row.invocationCount ?? 0), 0);
  const success = aggregateSuccessRate(metrics);
  const previous = aggregateSuccessRate(previousMetrics);
  const delta = !previousMetricsUnavailable && success.rate != null && previous.rate != null ? success.rate - previous.rate : null;
  const detailsLink = recentProjectId ? `/projects/${recentProjectId}/observability` : '/projects';
  return <div className={styles.usageGrid}>
  <div className={mergeClasses(styles.usageTile, styles.usagePrimary)}>
      <MetricCardHeader title="Token consumption by model" subtitle={`Measured input and output tokens in the ${range} range.`} />
      {usageUnavailable ? <MetricEmptyState>Model usage is unavailable from the Gateway.</MetricEmptyState> : usage.length === 0 ? <MetricEmptyState>No usage reported yet.</MetricEmptyState> : <div className={styles.list}>{usage.slice(0, 4).map((row) => <div key={row.model}><div className={styles.modelRow}><Text className={styles.modelName}>{row.model}</Text><Text>{row.tokens == null ? 'Tokens unavailable' : `${row.tokens.toLocaleString()} tokens`}</Text></div><Text className={styles.muted}>{row.cost.length ? row.cost.map(formatCost).join(', ') : 'Cost unavailable'}</Text><div className={styles.barTrack}><div className={styles.bar} style={{ width: `${row.tokens == null ? 0 : Math.max(4, (row.tokens / Math.max(1, usage[0].tokens ?? 1)) * 100)}%` }} /></div></div>)}</div>}
      <Link className={styles.link} to={detailsLink}>View details →</Link>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Model usage distribution" subtitle="Share by usage events." />
      {usageUnavailable ? <MetricEmptyState>Model usage is unavailable from the Gateway.</MetricEmptyState> : totalInvocations === 0 ? <MetricEmptyState>Model request counts are unavailable.</MetricEmptyState> : <div className={styles.donutWrap}><Donut rows={usage} /><div className={styles.legend}>{usage.slice(0, 5).map((row, index) => <span key={row.model} className={styles.legendItem}><span className={styles.swatch} style={{ backgroundColor: modelColors[index % modelColors.length] }} /><span className={styles.modelName}>{row.model}</span><span>{row.invocationCount == null ? '—' : `${Math.round(row.invocationCount / totalInvocations * 100)}%`}</span></span>)}</div></div>}
      <Link className={styles.link} to={detailsLink}>View details →</Link>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Response duration" subtitle="Latency percentiles from recent runs." />
      <MetricEmptyState>Response duration percentiles are unavailable from the Gateway.</MetricEmptyState>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Time to first token (TTFT)" subtitle="First-token latency percentiles." />
      <MetricEmptyState>Time to first token is unavailable from the Gateway.</MetricEmptyState>
    </div>
    <div className={styles.usageTile}>
      <MetricCardHeader title="Success rate" subtitle="Weighted by agent run totals." />
    {metricsUnavailable ? <MetricEmptyState>Run metrics are unavailable from the Gateway.</MetricEmptyState> : success.rate == null ? <MetricEmptyState>No completed runs in this range.</MetricEmptyState> : <><Text className={styles.bigNumber}>{Math.round(success.rate)}%</Text><Text className={styles.muted}>{delta == null ? 'Previous range unavailable' : `${delta >= 0 ? '+' : ''}${delta.toFixed(1)} pts vs previous range`} · {success.basis} completed runs</Text><Text className={styles.muted}>Daily success trend is unavailable.</Text></>}
    </div>
  </div>;
}

export function OverviewPage() {
  const styles = useStyles();
  const { apiCall } = useAuth();
  // Keep the platform snapshot, recent-project rollups, and two metrics windows
  // separately so partial project metrics can be omitted without losing overview data.
  const [overview, setOverview] = useState<OverviewData | null>(null);
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [rollups, setRollups] = useState<ProjectRollup[]>([]);
  const [metrics, setMetrics] = useState<ProjectMetrics[]>([]);
  const [previousMetrics, setPreviousMetrics] = useState<ProjectMetrics[]>([]);
  const [usages, setUsages] = useState<ProjectUsage[]>([]);
  const [partialError, setPartialError] = useState<string | null>(null);
  const [range, setRange] = useState<TimeRange>('30d');
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [lastUpdated, setLastUpdated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const mountedRef = useRef(true);
  const inFlightRef = useRef<AbortController | null>(null);

  /**
   * Loads the published project and notification reads, then enriches recent projects.
   * Each supplementary owner read can fail independently without concealing the
   * other panels; a stale response never replaces a newer poll.
   */
  const load = useCallback(async (abortSignal?: AbortSignal) => {
    if (mountedRef.current) setRefreshing(true);
    try {
      // Caller notifications are optional: a temporary bell failure must not hide project panels.
      const [projectList, notificationResult] = await Promise.all([
        apiCall((token, tenant) => gatewayClient.listProjects(token, tenant), null),
        apiCall((token, tenant) => gatewayClient.listNotifications(token, { pageSize: 100 }, tenant), null)
          .then((value) => ({ value }), () => ({ value: null })),
      ]);
      const notificationPage = notificationResult.value;
      const recent = projectList.filter((project) => project.state === 'active')
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, 4);
      const currentRange = timeRangeDates(range); const previousRange = timeRangeDates(range, true);
      const rollupResults = await Promise.all(recent.map(async (project): Promise<ProjectRollup> => {
        const id = project.projectId;
        const [configResult, runsResult, backlogResult] = await Promise.allSettled([
          apiCall((token, tenant) => gatewayClient.getProjectConfiguration(token, id, tenant), null),
          apiCall((token, tenant) => gatewayClient.listProjectRuns(token, id, { pageSize: 100 }, tenant), null),
          apiCall((token, tenant) => gatewayClient.readProjectBacklog(token, id, tenant), null),
        ]);
        const config = configResult.status === 'fulfilled' ? configResult.value.configuration : null;
        const runs = runsResult.status === 'fulfilled' ? runsResult.value : null;
        const repository = config?.sourceControl?.repository;
        const activity: RecentActivityDto[] = (runs?.items ?? []).slice(0, 10).map((run) => ({
          project_id: id, project_name: project.name, label: run.title ?? run.goal ?? `Run ${run.runId.slice(0, 8)}`,
          kind: run.state, timestamp_utc: run.completedAt ?? run.updatedAt,
        }));
        return {
          project, repository: repository ? `${repository.owner}/${repository.name}` : null,
          activeCount: null,
          blockedCount: null,
          lastActivityUtc: runs?.items[0]?.updatedAt ?? project.updatedAt,
          agentCount: config?.agentCharters.length ?? null,
          runCount: runs ? `${runs.items.length}${runs.nextCursor ? '+' : ''}` : null,
          taskCount: backlogResult.status === 'fulfilled' ? backlogResult.value.tasks.filter((task) => !task.isArchived).length : null,
          activityUnavailable: runsResult.status === 'rejected',
          activity,
        };
      }));
      const [metricResults, previousMetricResults, usageResults] = await Promise.all([
        Promise.allSettled(recent.map((project) => apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, project.projectId, currentRange, tenant), null))),
        Promise.allSettled(recent.map((project) => apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, project.projectId, previousRange, tenant), null))),
        Promise.allSettled(recent.map((project) => apiCall((token, tenant) => gatewayClient.getProjectUsage(token, project.projectId, currentRange, tenant), null))),
      ]);
      const metricsUnavailable = metricResults.some((result) => result.status === 'rejected');
      const previousMetricsUnavailable = previousMetricResults.some((result) => result.status === 'rejected');
      const usageUnavailable = usageResults.some((result) => result.status === 'rejected');
      if (mountedRef.current && !abortSignal?.aborted) {
        // A run list gives recent run events, not the old cross-project event stream.
        const recentActivity = rollupResults.flatMap((rollup) => rollup.activity)
          .sort((a, b) => b.timestamp_utc.localeCompare(a.timestamp_utc)).slice(0, 20);
        // Metrics include project-wide active and blocked counts; run pages are capped and sorted by creation time.
        const measuredRollups = rollupResults.map((rollup, index) => {
          const result = metricResults[index];
          const metrics = result?.status === 'fulfilled' ? result.value : null;
          return { ...rollup, activeCount: metrics?.activeRuns ?? null, blockedCount: metrics?.blockedRuns ?? null };
        });
        setOverview({
          generated_utc: new Date().toISOString(),
          active_projects: projectList.filter((project) => project.state === 'active').length,
          recent_activity: recentActivity,
          notifications: notificationPage?.items ?? [],
          notificationsUnavailable: notificationPage === null,
          metricsUnavailable,
          previousMetricsUnavailable,
          usageUnavailable,
        });
        setProjects(projectList);
        setRollups(measuredRollups);
        setMetrics(metricResults.flatMap((result) => result.status === 'fulfilled' ? [result.value] : []));
        setPreviousMetrics(previousMetricResults.flatMap((result) => result.status === 'fulfilled' ? [result.value] : []));
        setUsages(usageResults.flatMap((result) => result.status === 'fulfilled' ? [result.value] : []));
        setPartialError(notificationPage === null || metricsUnavailable || previousMetricsUnavailable || usageUnavailable
          || measuredRollups.some((rollup) => rollup.runCount === null || rollup.agentCount === null || rollup.taskCount === null)
          ? 'Some project details could not load. Try refreshing.' : null);
        setLastUpdated(new Date().toISOString());
        setError(null);
      }
    } catch (err) {
      // #208 point 5: an aborted fetch (unmount/range change/overlapping-poll guard) is expected
      // control flow, not a user-facing error — don't surface it.
      if (abortSignal?.aborted || (err instanceof DOMException && err.name === 'AbortError')) return;
      if (mountedRef.current) setError(formatError(err));
    }
    finally { if (mountedRef.current) { setLoading(false); setRefreshing(false); } }
  }, [apiCall, range]);

  // #208 point 5: single shared entry point for the poll tick AND manual refresh/retry clicks —
  // aborts whatever request is still in flight before starting a new one, so overlapping fan-outs
  // (interval firing while a manual refresh is pending, or vice versa) can't stack.
  /** Replaces any active fan-out before a manual refresh or retry. */
  const runLoad = useCallback(() => {
    inFlightRef.current?.abort();
    const controller = new AbortController();
    inFlightRef.current = controller;
    setLoading(true);
    void load(controller.signal);
  }, [load]);

  // Poll while mounted and abort the active fan-out when the selected range changes.
  useEffect(() => {
    mountedRef.current = true;
    const runLoadLoop = async () => {
      inFlightRef.current?.abort();
      const controller = new AbortController();
      inFlightRef.current = controller;
      setLoading(true);
      await load(controller.signal);
    };
    void runLoadLoop();
    const iv = setInterval(() => { void runLoadLoop(); }, REFRESH_MS);
    return () => { mountedRef.current = false; inFlightRef.current?.abort(); clearInterval(iv); };
  }, [load]);

  // Show caller notifications first, followed by recent run failures and blocked work.
  const attention = useMemo<AttentionItem[]>(() => {
    const items: AttentionItem[] = [];
    for (const notification of overview?.notifications ?? []) items.push({
      key: notification.notificationId,
      severity: 'warning',
      title: notification.source === 'backlogPromoted' ? 'Backlog ready for review' : notification.prompt ?? 'A run needs your input',
      subtitle: projects.find((project) => project.projectId === notification.projectId)?.name ?? 'Project',
      time: notification.createdAt,
      action: 'Open',
      to: notification.source === 'backlogPromoted'
        ? `/projects/${encodeURIComponent(notification.projectId)}/board`
        : `/projects/${encodeURIComponent(notification.projectId)}/runs/${encodeURIComponent(notification.runId)}?view=approvals`,
    });
    for (const activity of overview?.recent_activity ?? []) if (isFailure(activity.kind)) items.push({ key: `${activity.project_id}-${activity.timestamp_utc}-${activity.kind}`, severity: 'error', title: activity.label, subtitle: activity.project_name, time: activity.timestamp_utc, action: 'View logs', to: `/projects/${activity.project_id}/orchestrations` });
    for (const rollup of rollups) if (rollup.blockedCount && rollup.blockedCount > 0) items.push({ key: `blocked-${rollup.project.projectId}`, severity: 'warning', title: `${rollup.blockedCount} ${rollup.blockedCount === 1 ? 'run needs' : 'runs need'} input`, subtitle: rollup.project.name, time: rollup.lastActivityUtc, action: 'Open project', to: `/projects/${rollup.project.projectId}` });
    return items.slice(0, 5);
  }, [overview, projects, rollups]);
  // Group recent events by local calendar day for the activity timeline.
  const groupedActivity = useMemo(() => { const today = new Date().toDateString(); const groups = new Map<string, RecentActivityDto[]>(); for (const item of overview?.recent_activity ?? []) { const label = new Date(item.timestamp_utc).toDateString() === today ? 'Today' : new Date(item.timestamp_utc).toLocaleDateString(); groups.set(label, [...(groups.get(label) ?? []), item]); } return [...groups.entries()]; }, [overview]);
  const focusRollup = rollups[0];
  return <PageContainer className={styles.root}>
    <PageHeader title="Overview" description="Run and supervise AI agent teams, workflows, and approvals across your projects." actions={<div className={styles.headerActions}>{lastUpdated && <RefreshCountdown className={styles.generated} intervalMs={REFRESH_MS} lastRefreshedAt={new Date(lastUpdated)} refreshing={refreshing} />}{refreshing && overview && <Spinner size="extra-tiny" aria-label="Refreshing overview" />}<Button appearance="subtle" icon={<ArrowSyncRegular />} disabled={loading} onClick={() => { setLoading(true); runLoad(); }}>Refresh</Button></div>} />
    {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody><MessageBarActions><Button appearance="transparent" onClick={() => { setLoading(true); runLoad(); }}>Try again</Button></MessageBarActions></MessageBar>}
    {partialError && <MessageBar intent="warning"><MessageBarBody>{partialError}</MessageBarBody></MessageBar>}
    {loading && !overview && <LoadingOverview />}
    {overview ? <>
      <section className={styles.opsCard} aria-labelledby="overview-operations">
        <div className={styles.cardHead}>
          <span className={styles.cardHeadIcon}><Pulse24Regular /></span>
          <Text as="h2" id="overview-operations" className={styles.cardHeadTitle}>Operations</Text>
          <Badge appearance="tint" color="subtle">Health unavailable</Badge>
          <span className={styles.cardHeadMeta}><ClockRegular /> Generated {timeAgo(overview.generated_utc)}</span>
        </div>
        <Text className={styles.muted}>Cross-project in-flight, queued, and completed counts are unavailable from the Gateway.</Text>
        <div className={styles.glanceGrid}><GlanceItem label="In flight" value="—" styles={styles} /><GlanceItem label="Queued work" value="—" styles={styles} /><GlanceItem label="Done today" value="—" styles={styles} /><GlanceItem label="Active projects" value={overview.active_projects} styles={styles} /></div>
      </section>
      <PageSection title="Recent projects" description="Most recently active repositories with operational counts and queue pressure." actions={<Link className={styles.link} to="/projects">View all projects →</Link>}>
        {rollups.length === 0 ? emptyState(projects.length === 0 ? 'No projects yet.' : 'No recent projects to show.', projects.length === 0 ? 'Create a project, describe its work, and generate or choose the team and workflow.' : 'Recent project activity will appear after an agent run starts.') : <div className={styles.projectGrid}>
          <div className={styles.focusProject}><MetricCardHeader title="Current focus" subtitle={focusRollup ? 'Most recently updated project' : 'No project selected'} />{focusRollup ? <div className={styles.lastActivity}><Text className={styles.projectName}>{focusRollup.project.name}</Text><Text className={styles.healthCopy}>{focusRollup.activeCount === null ? 'Recent run status is unavailable.' : focusRollup.activeCount > 0 ? `${focusRollup.activeCount} active run${focusRollup.activeCount === 1 ? '' : 's'} need monitoring.` : focusRollup.blockedCount && focusRollup.blockedCount > 0 ? `${focusRollup.blockedCount} ${focusRollup.blockedCount === 1 ? 'run needs' : 'runs need'} input.` : 'No active work in the recent runs.'}</Text><Link className={styles.link} to={`/projects/${focusRollup.project.projectId}`}>Open focus project →</Link></div> : null}</div>
          <div className={styles.projectList}>{rollups.map((rollup) => {
          const status = statusFor(rollup);
          return <Link key={rollup.project.projectId} to={`/projects/${encodeURIComponent(rollup.project.projectId)}`} className={styles.projectCard}>
            <div className={styles.projectIdentity}><span className={styles.iconBubble}>{rollup.repository ? <BranchRegular /> : <FolderRegular />}</span><div className={styles.projectTextStack}><Text className={styles.projectName}>{rollup.project.name}</Text><Text className={mergeClasses(styles.muted, styles.projectSlugText)}>{projectSlug(rollup.repository)}</Text></div></div>
            <Badge appearance="tint" color={status === 'active' ? 'success' : status === 'queued' ? 'warning' : 'subtle'}><span className={styles.statusPill}><span className={`${styles.dot} ${status === 'active' ? styles.dotActive : status === 'queued' ? styles.dotQueued : styles.dotIdle}`} />{status === 'queued' ? 'Needs input' : status === 'unknown' ? 'Unavailable' : status.charAt(0).toUpperCase() + status.slice(1)}</span></Badge>
            <div className={styles.projectStats}>
              <span className={styles.stat}><ClockRegular />{timeAgo(rollup.lastActivityUtc)}</span>
              <span className={styles.stat}><BotRegular />{rollup.agentCount ?? '—'} agents</span>
              <span className={styles.stat}><PlayRegular />{rollup.runCount ?? '—'} runs</span>
              <span className={`${styles.stat} ${(rollup.taskCount ?? 0) > 0 ? styles.statDanger : ''}`}><AlertRegular />{rollup.taskCount ?? '—'} tasks</span>
            </div>
          </Link>;
        })}</div></div>}
      </PageSection>
      <PageSection title="AI usage & performance" description="Aggregated from existing project observability metrics for the recent projects shown above." actions={<div className={styles.sectionActions}><Text>Range</Text><Select value={range} onChange={(_e, data) => setRange(data.value as TimeRange)} aria-label="AI usage time range" size="small" style={{ width: '120px' }}><option value="7d">Last 7 days</option><option value="30d">Last 30 days</option><option value="90d">Last 90 days</option></Select></div>}>
        <UsageTiles metrics={metrics} previousMetrics={previousMetrics} usages={usages} range={range} recentProjectId={rollups[0]?.project.projectId} metricsUnavailable={overview.metricsUnavailable} previousMetricsUnavailable={overview.previousMetricsUnavailable} usageUnavailable={overview.usageUnavailable} />
      </PageSection>
      <div className={styles.mainGrid}>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><TitleText>Activity feed</TitleText></div>
          <Text className={styles.muted}>Recent run states are shown. Other platform events are unavailable from the Gateway.</Text>
          {rollups.some((rollup) => rollup.activityUnavailable) && <Text className={styles.muted}>Recent run activity is unavailable for some projects.</Text>}
          {groupedActivity.length === 0 ? rollups.some((rollup) => rollup.activityUnavailable)
            ? <MetricEmptyState>Run activity is unavailable from the Gateway.</MetricEmptyState>
            : emptyState('No recent runs.', 'Run activity will appear here after a run starts.')
            : <div className={styles.timeline}>{groupedActivity.map(([day, entries]) => <div key={day} className={styles.list}><Text className={styles.dayLabel}>{day}</Text>{entries.map((entry, index) => <div key={`${entry.project_id}-${entry.timestamp_utc}-${index}`} className={styles.timelineItem}><span className={styles.timelineIcon}>{activityIcon(entry.kind)}</span><div><Text className={styles.timelineTitle}>{entry.project_name}</Text><Text> {entry.label}</Text><div className={styles.timelineBadges}><Badge appearance="tint" color={isFailure(entry.kind) ? 'danger' : entry.kind === 'completed' ? 'success' : 'subtle'}>{humanizeKind(entry.kind)}</Badge></div></div><Text className={styles.muted}>{timeAgo(entry.timestamp_utc)}</Text></div>)}</div>)}</div>}
        </section>
        <section className={`${styles.section} ${styles.panel}`}><div className={styles.sectionHeader}><TitleText>Needs attention</TitleText></div>
          {overview.notificationsUnavailable && <MetricEmptyState>Notifications are unavailable from the Gateway.</MetricEmptyState>}
          {attention.length === 0 ? overview.notificationsUnavailable ? null : emptyState('Nothing needs attention.', 'No failures or queues need action right now. Keep the overview open while agents run.') : <div className={styles.attentionList}>{attention.map((item) => <div key={item.key} className={`${styles.alertCard} ${item.severity === 'error' ? styles.alertError : styles.alertWarning}`}>{item.severity === 'error' ? <ErrorCircleRegular className={styles.alertErrorIcon} /> : <WarningRegular className={styles.alertWarningIcon} />}<div><Text weight="semibold">{item.title}</Text><br /><Text className={styles.muted}>{item.subtitle}{item.time ? ` · ${timeAgo(item.time)}` : ''}</Text></div><Button as="a" href={item.to} appearance="secondary" size="small" icon={<OpenRegular />}>{item.action}</Button></div>)}</div>}
        </section>
      </div>
    </> : null}
  </PageContainer>;
}
