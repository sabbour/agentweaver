import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  makeStyles,
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
  tokens,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
} from '@fluentui/react-icons';
import { AiCredits } from '../components/AiCredits';
import { AgentInvocationChart } from '../components/dashboard/AgentInvocationChart';
import { ModelPerformancePanels } from '../components/dashboard/ModelPerformancePanels';
import { MetricEmptyState,
  MetricSectionHeading } from '../components/MetricTypography';
import { PageHeader } from '../components/PageHeader';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import { ErrorState } from '../components/ui';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { AgentLeaderboardEntryDto, ProjectDashboardDto, ProjectMetricsDto, ThroughputPointDto } from '../api/types';
// Dashboard — the project HOME (/projects/:projectId). Consumes the live
// GET /api/projects/{id}/dashboard endpoint (real data only; no cost). Renders
// overview counters, a 30-day throughput chart, and an agent leaderboard.
// Workflow-health is intentionally not rendered: the backend omits it because a
// run row carries no workflow-definition reference (see MetricsDtos.cs).

const REFRESH_MS = 30000;

type TimeRange = '7d' | '30d' | '90d';
type HealthTone = 'steady' | 'active' | 'attention' | 'insufficient' | 'quiet';

function timeRangeDates(range: TimeRange): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to);
  if (range === '7d') from.setDate(from.getDate() - 6);
  else if (range === '30d') from.setDate(from.getDate() - 29);
  else from.setDate(from.getDate() - 89);
  from.setUTCHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}

function timeRangeLabel(range: TimeRange): string {
  switch (range) {
    case '7d':
      return 'last 7 days';
    case '30d':
      return 'last 30 days';
    case '90d':
      return 'last 90 days';
  }
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXXL,
    maxWidth: '1480px',
    margin: '0 auto',
    '@media (max-width: 720px)': { gap: tokens.spacingVerticalXL },
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
    minWidth: 0,
  },
  breadcrumbLink: {
    color: tokens.colorNeutralForeground1,
    textDecorationLine: 'none',
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { textDecorationLine: 'underline' },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  breadcrumbCurrent: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
  },
  commandStrip: {
    display: 'grid',
    gridTemplateColumns: 'minmax(280px, .75fr) minmax(0, 1.5fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    '@media (max-width: 1120px)': { gridTemplateColumns: '1fr' },
    '@media (max-width: 760px)': { padding: tokens.spacingVerticalM },
  },
  healthBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  statusLine: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  healthTitle: {
    display: 'block',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    overflowWrap: 'anywhere',
  },
  healthCopy: {
    display: 'block',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
    maxWidth: '60ch',
  },
  quickActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    marginTop: 'auto',
  },
  actionLink: {
    minHeight: '32px',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: `0 ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground1,
    textDecorationLine: 'none',
    fontWeight: tokens.fontWeightSemibold,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  primaryActionLink: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorBrandBackground,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover, textDecorationLine: 'none' },
  },
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(120px, 1fr))',
    gap: tokens.spacingHorizontalM,
    minWidth: 0,
    alignSelf: 'start',
    '@media (max-width: 720px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
    '@media (max-width: 420px)': { gridTemplateColumns: '1fr' },
  },
  summaryTile: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    minHeight: '112px',
    minWidth: 0,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusSmall,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  summaryValue: {
    display: 'block',
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    marginTop: tokens.spacingVerticalXS,
    '@media (max-width: 720px)': { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600 },
  },
  summaryLabel: {
    display: 'block',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  summaryMeta: {
    display: '-webkit-box',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    marginTop: 'auto',
    overflow: 'hidden',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
  },
  mainGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.35fr) minmax(320px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 980px)': { gridTemplateColumns: '1fr' },
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
    minWidth: 0,
  },
  panelHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  legend: {
    display: 'flex',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
    flexWrap: 'wrap',
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
  sideStack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    minWidth: 0,
  },
  signalPanel: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 620px)': { gridTemplateColumns: '1fr' },
  },
  signalItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
    minWidth: 0,
  },
  leaderboardPanel: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
    overflowX: 'auto',
    minWidth: 0,
  },
  sharedMetricsHeader: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  diagnosticsSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow2,
    minWidth: 0,
  },
  diagnosticsHeader: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  diagnosticsBody: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.15fr) minmax(360px, .85fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: '1fr' },
  },
  diagnosticsBrief: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
    paddingBlockEnd: tokens.spacingVerticalM,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  diagnosticsBriefCopy: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  diagnosticsColumn: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  columnTitle: {
    display: 'block',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  diagnosticsEmpty: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: tokens.spacingHorizontalL,
    alignItems: 'center',
    padding: tokens.spacingVerticalL,
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  evidenceRail: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
    marginTop: tokens.spacingVerticalM,
  },
  evidencePill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  leaderboardHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  leaderboardPending: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    paddingBlockStart: tokens.spacingVerticalS,
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
    maxWidth: '260px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
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
  loadingShell: {
    display: 'grid',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  loadingRows: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, 1fr)',
    gap: tokens.spacingHorizontalM,
    '@media (max-width: 720px)': { gridTemplateColumns: '1fr' },
  },
  loadingBlock: {
    minHeight: '84px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
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
  if (rate >= 80) return 'success';
  if (rate >= 50) return 'warning';
  return 'danger';
}

function formatSuccessRate(row: AgentLeaderboardEntryDto): string {
  if (row.runsTotal === 0) return '—';
  return `${Math.round(row.successRate)}%`;
}

function statusTone(
  summary: ProjectDashboardDto['summary'],
  leaderboard: AgentLeaderboardEntryDto[],
  evidence: { created: number; done: number; successBasis: number; hasModelTelemetry: boolean },
): HealthTone {
  const riskyAgent = leaderboard.some((row) => row.runsTotal > 0 && row.successRate < 50);
  if (riskyAgent) return 'attention';
  if (summary.active_runs > 0 || summary.active_agents > 0) return 'active';
  const hasActivity =
    summary.runs_this_week > 0 ||
    summary.tasks_done_this_week > 0 ||
    summary.runs_total > 0 ||
    evidence.created > 0 ||
    evidence.done > 0;
  const hasQualityEvidence = evidence.successBasis > 0 || evidence.hasModelTelemetry;
  if (hasActivity && hasQualityEvidence) return 'steady';
  if (hasActivity) return 'insufficient';
  return 'quiet';
}

function plural(value: number, singular: string, pluralLabel = `${singular}s`): string {
  return `${value} ${value === 1 ? singular : pluralLabel}`;
}

function hasSummaryActivity(summary: ProjectDashboardDto['summary']): boolean {
  return summary.runs_this_week > 0 || summary.tasks_done_this_week > 0 || summary.runs_total > 0;
}

function statusCopy(
  tone: HealthTone,
  summary: ProjectDashboardDto['summary'],
  hasQualityEvidence = true,
): { label: string; badge: 'success' | 'warning' | 'subtle'; title: string; body: string } {
  switch (tone) {
    case 'attention':
      return {
        label: 'Needs review',
        badge: 'warning',
        title: 'An agent has a low success rate.',
        body: 'At least one agent has a success rate below 50%. Open Flow to review that agent.',
      };
    case 'active':
      return {
        label: 'Active',
        badge: 'success',
        title: 'Agents are working on this project.',
        body: hasQualityEvidence
          ? `${plural(summary.active_runs, 'run')} and ${plural(summary.active_agents, 'agent')} are active.`
          : `${plural(summary.active_runs, 'run')} and ${plural(summary.active_agents, 'agent')} are active. Model and agent data is not available yet.`,
      };
    case 'steady':
      return {
        label: 'Idle',
        badge: 'success',
        title: 'No runs are active.',
        body: `${plural(summary.runs_this_week, 'run')} started and ${plural(summary.tasks_done_this_week, 'task')} completed this week.`,
      };
    case 'insufficient':
      return {
        label: 'Activity only',
        badge: 'subtle',
        title: 'Run activity is available.',
        body: hasSummaryActivity(summary)
          ? `${plural(summary.runs_this_week, 'run')} started and ${plural(summary.tasks_done_this_week, 'task')} completed this week.`
          : 'The selected range has run activity.',
      };
    case 'quiet':
      return {
        label: 'No activity',
        badge: 'subtle',
        title: 'This project has no run activity.',
        body: 'Start a task to create the first run.',
      };
  }
}

function hasModelTelemetry(metrics: ProjectMetricsDto | null): boolean {
  return Boolean(
    metrics?.modelUsage?.some((row) => row.invocationCount > 0 || row.totalNanoAiu > 0) ||
    metrics?.responseDuration?.some((row) => row.p50Ms != null || row.p95Ms != null) ||
    metrics?.timeToFirstToken?.some((row) => row.p50Ms != null || row.p95Ms != null) ||
    metrics?.aiCreditUsageTrend?.some((point) => point.totalNanoAiu > 0),
  );
}

function primaryActionFor(
  tone: HealthTone,
  summary: ProjectDashboardDto['summary'],
): { label: string; path: 'board' | 'flow' | 'orchestrations' } {
  switch (tone) {
    case 'attention':
      return { label: 'Open Flow', path: 'flow' };
    case 'active':
      return { label: 'Open Board', path: 'board' };
    case 'steady':
      return { label: 'Open Board', path: 'board' };
    case 'insufficient':
      if (hasSummaryActivity(summary)) return { label: 'Open Board', path: 'board' };
      return { label: 'Open Flow', path: 'flow' };
    case 'quiet':
      return { label: 'Start task', path: 'orchestrations' };
  }
}

function secondaryActionsFor(
  tone: HealthTone,
  summary: ProjectDashboardDto['summary'],
): Array<{ label: string; path: 'board' | 'flow' | 'orchestrations' }> {
  if (tone === 'attention') return [{ label: 'Open Board', path: 'board' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'active') return [{ label: 'Open Flow', path: 'flow' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'steady') return [{ label: 'Open Flow', path: 'flow' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'insufficient' && hasSummaryActivity(summary)) return [{ label: 'Open Orchestrations', path: 'orchestrations' }, { label: 'Open Flow', path: 'flow' }];
  return [{ label: 'Open Board', path: 'board' }, { label: 'Open Flow', path: 'flow' }];
}

function sumThroughput(points: ThroughputPointDto[], field: 'created' | 'done'): number {
  return points.reduce((sum, point) => sum + point[field], 0);
}

function averageSuccess(leaderboard: AgentLeaderboardEntryDto[]): { rate: number | null; basis: number } {
  let weighted = 0;
  let total = 0;
  for (const row of leaderboard) {
    if (row.runsTotal > 0) {
      weighted += row.successRate * row.runsTotal;
      total += row.runsTotal;
    }
  }
  return { rate: total ? weighted / total : null, basis: total };
}

function ThroughputChart({ points }: { points: ThroughputPointDto[] }) {
  const W = 720;
  const H = 220;
  const pad = { top: 16, right: 16, bottom: 28, left: 32 };
  const innerW = W - pad.left - pad.right;
  const innerH = H - pad.top - pad.bottom;

  const maxVal = Math.max(1, ...points.map((p) => Math.max(p.created, p.done)));
  const n = points.length;
  const x = (i: number) => pad.left + (n <= 1 ? 0 : (i / (n - 1)) * innerW);
  const y = (v: number) => pad.top + innerH - (v / maxVal) * innerH;

  const toPath = (sel: (p: ThroughputPointDto) => number) =>
    points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(sel(p)).toFixed(1)}`).join(' ');

  const createdColor = '#635c57';
  const doneColor = tokens.colorPaletteGreenForeground1;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" height={H} role="img" aria-label="Created and completed runs over the selected range">
      <line x1={pad.left} y1={pad.top + innerH} x2={pad.left + innerW} y2={pad.top + innerH}
        stroke={tokens.colorNeutralStroke2} strokeWidth={1} />
      <line x1={pad.left} y1={pad.top} x2={pad.left + innerW} y2={pad.top}
        stroke={tokens.colorNeutralStroke3} strokeWidth={1} />
      <text x={pad.left} y={pad.top + innerH + 18} fontSize={10} fill={tokens.colorNeutralForeground3}>
        {points[0]?.date ?? ''}
      </text>
      <text x={pad.left + innerW} y={pad.top + innerH + 18} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {points[n - 1]?.date ?? ''}
      </text>
      <text x={pad.left - 6} y={pad.top + 8} fontSize={10} fill={tokens.colorNeutralForeground3} textAnchor="end">
        {maxVal}
      </text>
      <path d={toPath((p) => p.created)} fill="none" stroke={createdColor} strokeWidth={2.5} />
      <path d={toPath((p) => p.done)} fill="none" stroke={doneColor} strokeWidth={2.5} />
    </svg>
  );
}

function LoadingDashboard() {
  const styles = useStyles();
  return (
    <div className={styles.loadingShell} role="status" aria-label="Loading dashboard data">
      <Spinner label="Loading dashboard data" />
      <div className={styles.loadingRows} aria-hidden="true">
        <div className={styles.loadingBlock} />
        <div className={styles.loadingBlock} />
        <div className={styles.loadingBlock} />
      </div>
    </div>
  );
}

export function DashboardPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [data, setData] = useState<ProjectDashboardDto | null>(null);
  const [metrics, setMetrics] = useState<ProjectMetricsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [selectedRange, setSelectedRange] = useState<TimeRange>('30d');
  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  const mountedRef = useRef(true);
  const inFlightRef = useRef<AbortController | null>(null);

  const load = useCallback(async (abortSignal?: AbortSignal) => {
    if (!projectId) return;
    const rangeDates = timeRangeDates(selectedRange);
    try {
      const [dashboardDto, metricsDto] = await Promise.all([
        // #208 point 4: this page already fetches the full metrics DTO below, so skip the
        // dashboard endpoint's own internal 8-way metrics fan-out (its Throughput/AgentLeaderboard
        // fields are unused here — this page reads those from `metrics` instead).
        apiClient.getProjectDashboard(projectId, { includeMetrics: false, signal: abortSignal }),
        apiClient.getProjectMetrics(projectId, rangeDates.from, rangeDates.to, abortSignal),
      ]);
      if (mountedRef.current) {
        setData(dashboardDto);
        setMetrics(metricsDto);
        setError(null);
      }
    } catch (err) {
      // #208 point 5: an aborted fetch (unmount/range change/overlapping-poll guard) is expected
      // control flow, not a user-facing error — don't surface it.
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (mountedRef.current) setError(formatError(err));
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, [projectId, selectedRange]);

  // #208 point 5: single shared entry point for the poll tick AND manual refresh/retry clicks —
  // aborts whatever request is still in flight before starting a new one, so overlapping fan-outs
  // (interval firing while a manual refresh is pending, or vice versa) can't stack.
  const runLoad = useCallback(() => {
    if (!projectId) return;
    inFlightRef.current?.abort();
    const controller = new AbortController();
    inFlightRef.current = controller;
    setLoading(true);
    void load(controller.signal);
  }, [projectId, load]);

  useEffect(() => {
    if (!projectId) return;
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
    return () => {
      mountedRef.current = false;
      inFlightRef.current?.abort();
      clearInterval(iv);
    };
  }, [projectId, load]);

  const dashboardModel = useMemo(() => {
    if (!data) return null;
    const leaderboard = metrics?.leaderboard ?? [];
    const throughput = metrics?.throughput ?? [];
    const created = sumThroughput(throughput, 'created');
    const done = sumThroughput(throughput, 'done');
    const success = averageSuccess(leaderboard);
    const modelTelemetry = hasModelTelemetry(metrics);
    const tone = statusTone(data.summary, leaderboard, {
      created,
      done,
      successBasis: success.basis,
      hasModelTelemetry: modelTelemetry,
    });
    const primaryAction = primaryActionFor(tone, data.summary);
    return {
      leaderboard,
      throughput,
      created,
      done,
      success,
      tone,
      copy: statusCopy(tone, data.summary, success.basis > 0 || modelTelemetry),
      modelTelemetry,
      primaryAction,
      secondaryActions: secondaryActionsFor(tone, data.summary),
    };
  }, [data, metrics]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Dashboard"
        subtitle="Active work, completed runs, and agent metrics for this project."
        breadcrumb={
          <nav className={styles.breadcrumb} aria-label="Breadcrumb">
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span aria-hidden="true">/</span>
            <span className={styles.breadcrumbCurrent}>{data?.project_name ?? projectId}</span>
          </nav>
        }
        actions={
          <div className={styles.headerActions}>
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
              onClick={() => { setLoading(true); runLoad(); }}
            >
              Refresh
            </Button>
          </div>
        }
      />

      {error && (
        <ErrorState
          title="Dashboard data did not load"
          message={error}
          onRetry={() => { setLoading(true); runLoad(); }}
        />
      )}

      {loading && !data && <LoadingDashboard />}


      {data && dashboardModel && (
        <>
          <section className={styles.commandStrip} aria-labelledby="project-status-title">
            <div className={styles.healthBlock}>
              <div className={styles.statusLine}>
                <Badge appearance="tint" color={dashboardModel.copy.badge}>{dashboardModel.copy.label}</Badge>
                {loading ? <Badge appearance="outline">Refreshing</Badge> : null}
              </div>
              <div>
                <Text as="h2" id="project-status-title" className={styles.healthTitle}>{dashboardModel.copy.title}</Text>
                <Text className={styles.healthCopy}>{dashboardModel.copy.body}</Text>
              </div>
              <div className={styles.quickActions} aria-label="Project actions">
                <Link
                  to={`/projects/${projectId}/${dashboardModel.primaryAction.path}`}
                  className={`${styles.actionLink} ${styles.primaryActionLink}`}
                >
                  {dashboardModel.primaryAction.label}
                </Link>
                {dashboardModel.secondaryActions.map((action) => (
                  <Link key={action.path} to={`/projects/${projectId}/${action.path}`} className={styles.actionLink}>
                    {action.label}
                  </Link>
                ))}
              </div>
            </div>

            <div className={styles.summaryGrid} aria-label="Project summary">
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Active runs</Text>
                <Text className={styles.summaryValue}>{data.summary.active_runs}</Text>
                <Text className={styles.summaryMeta}>{plural(data.summary.active_agents, 'active agent')}</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Runs this week</Text>
                <Text className={styles.summaryValue}>{data.summary.runs_this_week}</Text>
                <Text className={styles.summaryMeta}>{plural(data.summary.runs_total, 'total run')}</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Completed tasks</Text>
                <Text className={styles.summaryValue}>{data.summary.tasks_done_this_week}</Text>
                <Text className={styles.summaryMeta}>This week</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Run success</Text>
                <Text className={styles.summaryValue}>{dashboardModel.success.rate == null ? 'Pending' : `${Math.round(dashboardModel.success.rate)}%`}</Text>
                <Text className={styles.summaryMeta}>
                  {dashboardModel.success.basis
                    ? `${dashboardModel.success.basis} scored runs`
                    : 'No scored runs'}
                </Text>
              </div>
            </div>
          </section>

          <div className={styles.sharedMetricsHeader}>
            <MetricSectionHeading
              title="Activity"
              subtitle="Select a range to compare created and completed runs."
            />
            <div className={styles.filterGroup}>
              <Text>Range</Text>
              <Select
                value={selectedRange}
                onChange={(_e, d) => setSelectedRange(d.value as TimeRange)}
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

          <div className={styles.mainGrid}>
            <section className={styles.section}>
              <div className={styles.panel}>
                <div className={styles.panelHeader}>
                  <MetricSectionHeading
                    title="Runs over time"
                    subtitle={`Created and completed runs during the ${timeRangeLabel(selectedRange)}.`}
                  />
                  <div className={styles.legend} aria-label="Run activity legend">
                    <span className={styles.legendItem}>
                      <span className={styles.swatch} style={{ backgroundColor: '#635c57' }} />
                      Created
                    </span>
                    <span className={styles.legendItem}>
                      <span className={styles.swatch} style={{ backgroundColor: tokens.colorPaletteGreenForeground1 }} />
                      Done
                    </span>
                  </div>
                </div>
                {dashboardModel.throughput.length === 0 ? (
                  <MetricEmptyState>No run data for this range. Start or complete a run to add data.</MetricEmptyState>
                ) : (
                  <ThroughputChart points={dashboardModel.throughput} />
                )}
              </div>
            </section>

            <aside className={styles.sideStack} aria-label="Project activity summary">
              <div className={styles.panel}>
                <MetricSectionHeading title="Run totals" subtitle={`Run totals for the ${timeRangeLabel(selectedRange)}.`} />
                <div className={styles.signalPanel}>
                  <div className={styles.signalItem}>
                    <Text className={styles.summaryLabel}>Created</Text>
                    <Text className={styles.summaryValue}>{dashboardModel.created}</Text>
                  </div>
                  <div className={styles.signalItem}>
                    <Text className={styles.summaryLabel}>Completed</Text>
                    <Text className={styles.summaryValue}>{dashboardModel.done}</Text>
                  </div>
                </div>
              </div>
              <AgentInvocationChart
                points={metrics?.invocationTrend ?? []}
                subtitle={`Daily runs created during the ${timeRangeLabel(selectedRange)}.`}
                emptyLabel="No runs were created in this range. Start a task to add data."
              />
            </aside>
          </div>

          <section className={styles.diagnosticsSurface} aria-labelledby="diagnostics-title">
            <div className={styles.diagnosticsHeader}>
              <MetricSectionHeading
                title={<span id="diagnostics-title">Model and agent metrics</span>}
                subtitle="Review model usage, response times, costs, and agent success rates."
              />
            </div>
            {!dashboardModel.modelTelemetry && dashboardModel.leaderboard.length === 0 ? (
              <div className={styles.diagnosticsEmpty}>
                <div>
                  <Text as="h3" className={styles.columnTitle}>
                    {hasSummaryActivity(data.summary)
                      ? 'No model or agent metrics for this range.'
                      : 'No model or agent data.'}
                  </Text>
                  <MetricEmptyState>
                    {hasSummaryActivity(data.summary)
                      ? 'Runs have not reported model usage or agent scores for this range. Open Board or Orchestrations to review recent work.'
                      : 'Start a task. The dashboard adds model and agent data after a run reports it.'}
                  </MetricEmptyState>
                  <div className={styles.evidenceRail} aria-label="Available dashboard evidence">
                    <span className={styles.evidencePill}>{plural(data.summary.runs_this_week, 'run')} this week</span>
                    <span className={styles.evidencePill}>{plural(data.summary.tasks_done_this_week, 'task')} done this week</span>
                    <span className={styles.evidencePill}>{plural(data.summary.runs_total, 'total run')}</span>
                    <span className={styles.evidencePill}>{plural(data.summary.active_runs, 'active run')}</span>
                  </div>
                </div>
                <Link
                  to={`/projects/${projectId}/${dashboardModel.primaryAction.path}`}
                  className={`${styles.actionLink} ${styles.primaryActionLink}`}
                >
                  {dashboardModel.primaryAction.label}
                </Link>
              </div>
            ) : (
              <>
                <div className={styles.diagnosticsBrief}>
                  <div className={styles.diagnosticsBriefCopy}>
                    <Text as="h3" className={styles.columnTitle}>
                      {dashboardModel.modelTelemetry && dashboardModel.leaderboard.length > 0
                        ? 'Model and agent data is available.'
                        : 'Some model or agent data is missing.'}
                    </Text>
                  </div>
                  <div className={styles.evidenceRail} aria-label="Model and agent data status">
                    <span className={styles.evidencePill}>{dashboardModel.modelTelemetry ? 'Model data available' : 'No model data'}</span>
                    <span className={styles.evidencePill}>{dashboardModel.leaderboard.length > 0 ? 'Agent scores available' : 'No agent scores'}</span>
                  </div>
                </div>

                <div className={styles.diagnosticsBody}>
                  <div className={styles.diagnosticsColumn} aria-labelledby="model-performance-title">
                    <Text as="h3" id="model-performance-title" className={styles.columnTitle}>Model metrics</Text>
                    <ModelPerformancePanels metrics={metrics} />
                  </div>

                  <div className={styles.diagnosticsColumn} aria-labelledby="agent-leaderboard-title">
                    <div className={styles.leaderboardHeader}>
                      <Text as="h3" id="agent-leaderboard-title" className={styles.columnTitle}>Agent leaderboard</Text>
                      <Text className={styles.healthCopy}>Open an agent to review its runs and success rate.</Text>
                    </div>
                    {dashboardModel.leaderboard.length === 0 ? (
                      <div className={styles.leaderboardPending}>
                        <MetricEmptyState>
                          No agent scores are available for this range. Open Flow to review agent runs.
                        </MetricEmptyState>
                        <Link to={`/projects/${projectId}/flow`} className={styles.actionLink}>
                          Open Flow
                        </Link>
                      </div>
                    ) : (
                      <div className={styles.leaderboardPanel}>
                        <Table aria-label="Agent leaderboard" size="small" className={styles.leaderboardTable}>
                          <TableHeader>
                            <TableRow>
                              <TableHeaderCell className={styles.headerCell}>Agent</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Role</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Runs this week</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Total runs</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Success rate</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Average duration</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Cost</TableHeaderCell>
                            </TableRow>
                          </TableHeader>
                          <TableBody>
                            {dashboardModel.leaderboard.map((row) => (
                              <TableRow key={row.agentName}>
                                <TableCell>
                                  <TableCellLayout className={styles.agentCell}>
                                    <Link
                                      to={`/projects/${projectId}/flow?agent=${encodeURIComponent(row.agentName)}`}
                                      className={styles.breadcrumbLink}
                                    >
                                      {row.agentName}
                                    </Link>
                                  </TableCellLayout>
                                </TableCell>
                                <TableCell>
                                  <TableCellLayout className={styles.roleCell}>{row.role ?? '—'}</TableCellLayout>
                                </TableCell>
                                <TableCell>{row.runsThisWeek}</TableCell>
                                <TableCell>{row.runsTotal}</TableCell>
                                <TableCell>
                                  <div className={styles.successCell}>
                                    <Badge
                                      appearance="tint"
                                      color={row.runsTotal === 0 ? 'subtle' : successBadgeColor(row.successRate)}
                                    >
                                      {formatSuccessRate(row)}
                                    </Badge>
                                    <Text className={styles.successBasis}>{row.runsTotal > 0 ? `${row.successRate}%` : '—'}</Text>
                                  </div>
                                </TableCell>
                                <TableCell>{formatDuration(row.avgDurationMs)}</TableCell>
                                <TableCell>{row.costAic > 0 ? <AiCredits totalNanoAiu={Math.round(row.costAic * 1_000_000_000)} /> : '—'}</TableCell>
                              </TableRow>
                            ))}
                          </TableBody>
                        </Table>
                      </div>
                    )}
                  </div>
                </div>
              </>
            )}
          </section>
        </>
      )}
    </div>
  );
}
