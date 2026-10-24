import { gatewayClient } from '../v1/api';
import { useAuth } from '../v1/AuthContext';
import { formatCost, sortUsage } from '../v1/projectMetrics';
import type { EffectiveProjectRole, ProjectMetrics, ProjectSummary, ProjectUsage } from '../v1/contracts';
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
import { MetricEmptyState,
  MetricSectionHeading } from '../components/MetricTypography';
import { PageHeader } from '../components/PageHeader';
import { ProjectRoleBadge } from '../v1/pageParts';
import { useProjectConfiguration } from '../v1/projectData';
import { projectReadiness } from '../v1/projectReadiness';
import { RunList } from '../v1/RunList';
import { runListErrorMessage, useProjectRunList } from '../v1/runListData';
import { RefreshCountdown } from '../hooks/useRefreshCountdown';
import { ErrorState } from '../components/ui';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { AgentLeaderboardEntryDto, ThroughputPointDto } from '../api/types';

/** Summary fields the v1 owners can prove; the two 0.x-only totals stay unavailable. */
type ProjectSummaryData = {
  runs_this_week: number | null;
  weekly_runs_error: string | null;
  runs_total: number;
  active_runs: number;
  active_agents: null;
  tasks_done_this_week: null;
};
/** View model for the retained dashboard without requesting its unpublished 0.x endpoint. */
type DashboardData = {
  project_id: string;
  project_name: string;
  project_state: ProjectSummary['state'];
  effective_roles: EffectiveProjectRole[];
  generated_utc: string;
  summary: ProjectSummaryData;
};
type RangedSnapshot<T> = { projectId: string; range: TimeRange; value: T };
type UsageSnapshot = RangedSnapshot<ProjectUsage | null> & { error: string | null };

/**
 * Project-home dashboard. It combines a summary response with range-specific
 * metrics, derives an evidence-based health state, and links into work views.
 */
const REFRESH_MS = 30000;

type TimeRange = '7d' | '30d' | '90d';
type HealthTone = 'steady' | 'active' | 'attention' | 'insufficient' | 'quiet';

/** Builds inclusive-looking UTC range boundaries used by the metrics API. */
function timeRangeDates(range: TimeRange): { from: string; to: string } {
  const to = new Date();
  const from = new Date(to);
  if (range === '7d') from.setDate(from.getDate() - 6);
  else if (range === '30d') from.setDate(from.getDate() - 29);
  else from.setDate(from.getDate() - 89);
  from.setUTCHours(0, 0, 0, 0);
  return { from: from.toISOString(), to: to.toISOString() };
}

/** Returns the user-facing label matching the selected range key. */
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

function formatDashboardError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
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
  supportGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: tokens.spacingHorizontalL,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr' },
  },
  checkList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    margin: 0,
    padding: 0,
    listStyleType: 'none',
  },
  checkRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  teamList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    margin: 0,
    padding: 0,
    listStyleType: 'none',
  },
  teamMember: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'baseline',
    gap: tokens.spacingHorizontalM,
    paddingBlockEnd: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
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

/** Formats a nullable duration using seconds, minutes, or hours for the table. */
function formatDuration(ms: number | null): string {
  if (ms == null) return '—';
  const seconds = ms / 1000;
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(1)}m`;
  const hours = minutes / 60;
  return `${hours.toFixed(1)}h`;
}

/** Assigns a severity color to the dashboard's success-rate threshold bands. */
function successBadgeColor(rate: number): 'success' | 'warning' | 'danger' {
  if (rate >= 80) return 'success';
  if (rate >= 50) return 'warning';
  return 'danger';
}

/** Avoids implying a measured percentage for agents with no scored runs. */
function formatSuccessRate(row: AgentLeaderboardEntryDto): string {
  if (row.runsTotal === 0) return '—';
  return `${Math.round(row.successRate)}%`;
}

/**
 * Derives the dashboard's project tone from live activity and available quality
 * evidence; missing telemetry is not treated as evidence of poor health.
 */
function statusTone(
  summary: ProjectSummaryData,
  leaderboard: AgentLeaderboardEntryDto[],
  evidence: { created: number; done: number; successBasis: number; hasModelTelemetry: boolean },
): HealthTone {
  const riskyAgent = leaderboard.some((row) => row.runsTotal > 0 && row.successRate < 50);
  if (riskyAgent) return 'attention';
  if (summary.active_runs > 0) return 'active';
  const hasActivity =
    (summary.runs_this_week ?? 0) > 0 ||
    summary.runs_total > 0 ||
    evidence.created > 0 ||
    evidence.done > 0;
  const hasQualityEvidence = evidence.successBasis > 0 || evidence.hasModelTelemetry;
  if (hasActivity && hasQualityEvidence) return 'steady';
  if (hasActivity) return 'insufficient';
  return 'quiet';
}

/** Formats a count with the correct singular or plural label. */
function plural(value: number, singular: string, pluralLabel = `${singular}s`): string {
  return `${value} ${value === 1 ? singular : pluralLabel}`;
}

/** Reports whether the summary contains any run or task activity. */
function hasSummaryActivity(summary: ProjectSummaryData): boolean {
  // Only count measured run activity; v1 has no task completion timestamp.
  return (summary.runs_this_week ?? 0) > 0 || summary.runs_total > 0;
}

/** Supplies the copy and badge tone associated with a derived health state. */
function statusCopy(
  tone: HealthTone,
  summary: ProjectSummaryData,
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
        title: 'Runs are active in this project.',
        body: hasQualityEvidence
          ? `${plural(summary.active_runs, 'run')} are active. Active agent count is unavailable.`
          : `${plural(summary.active_runs, 'run')} are active. Agent counts and model data are unavailable.`,
      };
    case 'steady':
      return {
        label: 'Idle',
        badge: 'subtle',
        title: 'No runs are active.',
        body: summary.runs_this_week == null
          ? 'The 7-day run count is unavailable. Task completions are unavailable.'
          : `${plural(summary.runs_this_week, 'run')} started this week. Task completions are unavailable.`,
      };
    case 'insufficient':
      return {
        label: 'Activity only',
        badge: 'subtle',
        title: 'Run activity is available.',
        body: hasSummaryActivity(summary)
          ? summary.runs_this_week == null
            ? 'The 7-day run count is unavailable. Task completions are unavailable.'
            : `${plural(summary.runs_this_week, 'run')} started this week. Task completions are unavailable.`
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

/** Detects non-empty usage or latency data rather than merely present arrays. */
function hasModelTelemetry(usage: ProjectUsage | null): boolean {
  // A measured request or ledger subtotal is evidence; an empty response is not.
  return Boolean(usage?.usageByModel.some((row) => row.requestCount != null || row.cost.length > 0));
}

/** Selects the main navigation action appropriate to current project evidence. */
function primaryActionFor(
  tone: HealthTone,
  summary: ProjectSummaryData,
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
      return { label: 'Open Board', path: 'board' };
  }
}

/** Selects supporting navigation actions without repeating the primary action. */
function secondaryActionsFor(
  tone: HealthTone,
  summary: ProjectSummaryData,
): Array<{ label: string; path: 'board' | 'flow' | 'orchestrations' }> {
  if (tone === 'attention') return [{ label: 'Open Board', path: 'board' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'active') return [{ label: 'Open Flow', path: 'flow' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'steady') return [{ label: 'Open Flow', path: 'flow' }, { label: 'Open Orchestrations', path: 'orchestrations' }];
  if (tone === 'insufficient' && hasSummaryActivity(summary)) return [{ label: 'Open Orchestrations', path: 'orchestrations' }, { label: 'Open Flow', path: 'flow' }];
  return [{ label: 'Open Board', path: 'board' }, { label: 'Open Flow', path: 'flow' }];
}

/** Totals a single throughput series across the selected time window. */
function sumThroughput(points: ThroughputPointDto[], field: 'created' | 'done'): number {
  return points.reduce((sum, point) => sum + point[field], 0);
}

/** Renders created and completed run counts as two lines over the same date axis. */
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

/** Dashboard skeleton shown before the first summary and metrics response arrive. */
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
  const { apiCall } = useAuth();
  const configuration = useProjectConfiguration(projectId ?? '');
  const recentRuns = useProjectRunList(projectId ?? '', { pageSize: 5 });

  // Dashboard summary and analytics are separate API responses; retain their own
  // state so the range selector can update metrics without conflating summary data.
  const [data, setData] = useState<DashboardData | null>(null);
  const [metrics, setMetrics] = useState<RangedSnapshot<ProjectMetrics> | null>(null);
  const [usage, setUsage] = useState<UsageSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [selectedRange, setSelectedRange] = useState<TimeRange>('30d');

  const mountedRef = useRef(true);
  const inFlightRef = useRef<AbortController | null>(null);
  const reloadConfiguration = configuration.reload;
  const reloadRecentRuns = recentRuns.reload;

  /**
   * Composes the 0.x dashboard panels from the project's published v1 owner reads.
   * Abort prevents an older poll from updating the page, even though Gateway reads
   * do not accept an AbortSignal.
   */
  const load = useCallback(async (abortSignal?: AbortSignal) => {
    if (!projectId) return;
    const rangeDates = timeRangeDates(selectedRange);
    try {
      const weekMetricsRequest = selectedRange === '7d'
        ? Promise.resolve({ value: null, error: null })
        : apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, projectId, timeRangeDates('7d'), tenant), null)
          .then((value) => ({ value, error: null }), (reason: unknown) => ({ value: null, error: formatDashboardError(reason) }));
      const [project, metricsDto, weekMetricsResult, usageResult] = await Promise.all([
        apiCall((token, tenant) => gatewayClient.getProject(token, projectId, tenant), null),
        apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, projectId, rangeDates, tenant), null),
        // Keep the weekly tile independent so its failure cannot discard range metrics or usage.
        weekMetricsRequest,
        // Usage can be unavailable independently of the run metrics.
        apiCall((token, tenant) => gatewayClient.getProjectUsage(token, projectId, rangeDates, tenant), null)
          .then((value) => ({ value, error: null }), (reason: unknown) => ({ value: null, error: formatDashboardError(reason) })),
      ]);
      if (mountedRef.current && !abortSignal?.aborted) {
        setData({
          project_id: project.projectId,
          project_name: project.name,
          project_state: project.state,
          effective_roles: project.effectiveRoles,
          generated_utc: new Date().toISOString(),
          summary: {
            runs_this_week: selectedRange === '7d'
              ? metricsDto.runsInRange
              : weekMetricsResult.value?.runsInRange ?? null,
            weekly_runs_error: weekMetricsResult.error,
            runs_total: metricsDto.runsTotal,
            active_runs: metricsDto.activeRuns,
            active_agents: null,
            tasks_done_this_week: null,
          },
        });
        setMetrics({ projectId: project.projectId, range: selectedRange, value: metricsDto });
        setUsage({
          projectId: project.projectId,
          range: selectedRange,
          value: usageResult.value,
          error: usageResult.error,
        });
        setError(null);
      }
    } catch (err) {
      // An aborted fetch is expected control flow, not a user-facing error.
      if (abortSignal?.aborted || (err instanceof DOMException && err.name === 'AbortError')) return;
      if (mountedRef.current) setError(formatDashboardError(err));
    } finally {
      // An aborted request must not clear loading for the replacement project or refresh.
      if (mountedRef.current && !abortSignal?.aborted) setLoading(false);
    }
  }, [apiCall, projectId, selectedRange]);

  // Keep a single request active so manual refreshes and interval polls cannot stack.
  /** Aborts any previous poll or manual request before starting a fresh load. */
  const runLoad = useCallback(() => {
    if (!projectId) return;
    inFlightRef.current?.abort();
    const controller = new AbortController();
    inFlightRef.current = controller;
    setLoading(true);
    void load(controller.signal);
  }, [projectId, load]);

  // Start the poll loop for this project and abort the active request on range
  // change or unmount so results for the old range cannot overwrite new metrics.
  useEffect(() => {
    if (!projectId) return;
    mountedRef.current = true;
    const runLoadLoop = async (refreshSupport = false) => {
      inFlightRef.current?.abort();
      const controller = new AbortController();
      inFlightRef.current = controller;
      setLoading(true);
      if (refreshSupport) {
        reloadConfiguration();
        reloadRecentRuns();
      }
      await load(controller.signal);
    };
    void runLoadLoop();
    const iv = setInterval(() => { void runLoadLoop(true); }, REFRESH_MS);
    return () => {
      mountedRef.current = false;
      inFlightRef.current?.abort();
      clearInterval(iv);
    };
  }, [projectId, load, reloadConfiguration, reloadRecentRuns]);

  // A project route can change while this dashboard stays mounted. Never render
  // the previous project's owner snapshots while the new project is loading.
  const currentData = data?.project_id === projectId ? data : null;
  // Hide snapshots until they match both the route and selected range.
  const currentMetrics = metrics && metrics.projectId === projectId && metrics.range === selectedRange ? metrics.value : null;
  const currentUsageSnapshot = usage && usage.projectId === projectId && usage.range === selectedRange ? usage : null;
  const currentUsage = currentUsageSnapshot?.value ?? null;
  const currentUsageError = currentUsageSnapshot?.error ?? null;

  // Keep health copy, totals, evidence availability, and navigation decisions derived
  // from the same loaded snapshot rather than recalculating them in the markup.
  const readiness = useMemo(() => {
    if (!currentData || !configuration.data) return null;
    return projectReadiness(configuration.data.configuration, currentData.project_state === 'archived');
  }, [configuration.data, currentData]);
  const agentNames = useMemo(
    () => new Map((configuration.data?.configuration.agentCharters ?? []).map((agent) => [agent.agentId, agent.name])),
    [configuration.data],
  );

  const dashboardModel = useMemo(() => {
    if (!currentData) return null;
    // Orchestrator reports project success, but no agent-level leaderboard.
    const leaderboard: AgentLeaderboardEntryDto[] = [];
    const throughput = currentMetrics?.series.map((point) => ({ date: point.date, created: point.created, done: point.completed })) ?? [];
    const created = currentMetrics ? sumThroughput(throughput, 'created') : null;
    const done = currentMetrics ? sumThroughput(throughput, 'done') : null;
    const success = currentMetrics?.completedRuns
      ? { rate: currentMetrics.succeededRuns / currentMetrics.completedRuns * 100, basis: currentMetrics.completedRuns }
      : { rate: null, basis: 0 };
    const modelTelemetry = hasModelTelemetry(currentUsage);
    const archived = currentData.project_state === 'archived';
    const tone = statusTone(currentData.summary, leaderboard, {
      created: created ?? 0,
      done: done ?? 0,
      successBasis: success.basis,
      hasModelTelemetry: modelTelemetry,
    });
    const primaryAction = archived
      ? { label: 'Open Project settings', path: 'settings?section=danger' }
      : primaryActionFor(tone, currentData.summary);
    return {
      leaderboard,
      hasAgentUsage: Boolean(currentUsage?.usageByAgent.length),
      throughput,
      created,
      done,
      success,
      tone,
      copy: archived
        ? {
          label: 'Archived',
          badge: 'subtle' as const,
          title: 'This project is archived',
          body: 'Restore the project in Project settings before starting new runs.',
        }
        : statusCopy(tone, currentData.summary, success.basis > 0 || modelTelemetry),
      modelTelemetry,
      primaryAction,
      secondaryActions: archived ? [] : secondaryActionsFor(tone, currentData.summary),
      archived,
    };
  }, [currentData, currentMetrics, currentUsage]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Dashboard"
        subtitle="Active work, completed runs, and agent metrics for this project."
        breadcrumb={
          <nav className={styles.breadcrumb} aria-label="Breadcrumb">
            <Link to="/projects" className={styles.breadcrumbLink}>Projects</Link>
            <span aria-hidden="true">/</span>
            <span className={styles.breadcrumbCurrent}>{currentData?.project_name ?? projectId}</span>
          </nav>
        }
        actions={
          <div className={styles.headerActions}>
            {currentData && <ProjectRoleBadge roles={currentData.effective_roles} />}
            {currentData && (
              <Text className={styles.generated}>
                Updated {new Date(currentData.generated_utc).toLocaleTimeString()}
              </Text>
            )}
            {currentData && (
              <RefreshCountdown
                className={styles.generated}
                intervalMs={REFRESH_MS}
                lastRefreshedAt={new Date(currentData.generated_utc)}
              />
            )}
            <Button
              appearance="secondary"
              icon={<ArrowSyncRegular />}
              disabled={loading}
              onClick={() => {
                setLoading(true);
                runLoad();
                configuration.reload();
                recentRuns.reload();
              }}
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

      {loading && !currentData && <LoadingDashboard />}


      {currentData && dashboardModel && (
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
                {!dashboardModel.archived && <Text className={styles.healthCopy}>Live project health is unavailable. This status uses run counts only.</Text>}
              </div>
              <div className={styles.quickActions} aria-label="Project actions">
                <Link
                  to={`/projects/${encodeURIComponent(projectId)}/${dashboardModel.primaryAction.path}`}
                  className={`${styles.actionLink} ${styles.primaryActionLink}`}
                >
                  {dashboardModel.primaryAction.label}
                </Link>
                {dashboardModel.secondaryActions.map((action) => (
                  <Link key={action.path} to={`/projects/${encodeURIComponent(projectId)}/${action.path}`} className={styles.actionLink}>
                    {action.label}
                  </Link>
                ))}
              </div>
            </div>

            <div className={styles.summaryGrid} aria-label="Project summary">
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Active runs</Text>
                <Text className={styles.summaryValue}>{currentData.summary.active_runs}</Text>
                <Text className={styles.summaryMeta}>Active agent count unavailable</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Runs, last 7 days</Text>
                <Text className={styles.summaryValue}>{currentData.summary.runs_this_week ?? '—'}</Text>
                <Text className={styles.summaryMeta}>
                  {currentData.summary.runs_this_week == null
                    ? currentData.summary.weekly_runs_error
                      ? `Weekly run count unavailable: ${currentData.summary.weekly_runs_error}`
                      : '7-day run count unavailable'
                    : plural(currentData.summary.runs_total, 'total run')}
                </Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Completed tasks</Text>
                <Text className={styles.summaryValue}>—</Text>
                <Text className={styles.summaryMeta}>Task completion count unavailable</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Run success</Text>
                <Text className={styles.summaryValue}>{dashboardModel.success.rate == null ? 'Pending' : `${Math.round(dashboardModel.success.rate)}%`}</Text>
                <Text className={styles.summaryMeta}>
                  {dashboardModel.success.basis
                    ? `${dashboardModel.success.basis} completed runs`
                    : 'No completed runs'}
                </Text>
              </div>
            </div>
          </section>

          <div className={styles.supportGrid}>
            <section className={styles.panel} aria-labelledby="project-setup-title">
              <div className={styles.panelHeader}>
                <MetricSectionHeading
                  title={<span id="project-setup-title">Setup checks</span>}
                  subtitle="Saved project settings and the next setup action."
                />
                {readiness && <Badge appearance="tint" color={readiness.badge.color}>{readiness.badge.label}</Badge>}
              </div>
              {configuration.error != null && (
                <ErrorState
                  title="Project setup could not refresh"
                  message={formatDashboardError(configuration.error)}
                  onRetry={configuration.reload}
                />
              )}
              {configuration.loading && !configuration.data && <Spinner label="Loading project setup" />}
              {readiness && (
                <>
                  <Text>{readiness.body}</Text>
                  <ul className={styles.checkList}>
                    {readiness.checks.map((check) => (
                      <li key={check.key} className={styles.checkRow}>
                        <Badge appearance="tint" color={check.done ? 'success' : 'warning'}>
                          {check.done ? 'Ready' : 'Needs setup'}
                        </Badge>
                        <Link
                          to={`/projects/${encodeURIComponent(projectId)}/${check.path}`}
                          className={styles.breadcrumbLink}
                        >
                          {check.label}
                        </Link>
                      </li>
                    ))}
                  </ul>
                  <div className={styles.quickActions}>
                    <Link
                      to={`/projects/${encodeURIComponent(projectId)}/${readiness.primaryAction.path}`}
                      className={`${styles.actionLink} ${styles.primaryActionLink}`}
                    >
                      {readiness.primaryAction.label}
                    </Link>
                  </div>
                </>
              )}
            </section>

            <section className={styles.panel} aria-labelledby="team-title">
              <div className={styles.panelHeader}>
                <MetricSectionHeading
                  title={<span id="team-title">Team</span>}
                  subtitle="Agents saved in this project's configuration."
                />
                {configuration.data && (
                  <Badge appearance="outline">
                    {plural(configuration.data.configuration.agentCharters.length, 'agent')}
                  </Badge>
                )}
              </div>
              {configuration.data?.configuration.agentCharters.length ? (
                <ul className={styles.teamList}>
                  {configuration.data.configuration.agentCharters.map((agent) => (
                    <li key={agent.agentId} className={styles.teamMember}>
                      <Text>{agent.name}</Text>
                      <Text className={styles.summaryMeta}>{agent.customRoleTitle?.trim() || agent.role}</Text>
                    </li>
                  ))}
                </ul>
              ) : configuration.loading ? (
                <Spinner label="Loading project team" />
              ) : (
                <MetricEmptyState>
                  {configuration.error != null ? 'The saved team could not be read.' : 'No agents are assigned to this project.'}
                </MetricEmptyState>
              )}
              <div className={styles.quickActions}>
                <Link to={`/projects/${encodeURIComponent(projectId)}/team`} className={styles.actionLink}>
                  Open Team
                </Link>
              </div>
            </section>
          </div>

          <section className={styles.section} aria-label="Recent project runs">
            {recentRuns.error != null && (
              <ErrorState
                title="Recent runs could not refresh"
                message={runListErrorMessage(recentRuns.error)}
                onRetry={recentRuns.reload}
              />
            )}
            <RunList
              title="Recent runs"
              description="Latest runs in this project."
              data={recentRuns}
              emptyText={recentRuns.loading ? 'Loading recent runs…' : recentRuns.error != null ? 'Recent runs could not load.' : 'No runs yet.'}
              limit={5}
            />
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
                points={currentMetrics?.series.map((point) => ({ date: point.date, count: point.created })) ?? []}
                subtitle={`Daily runs created during the ${timeRangeLabel(selectedRange)}.`}
                emptyLabel="No runs were created in this range. Start a task to add data."
              />
            </aside>
          </div>

          <section className={styles.diagnosticsSurface} aria-labelledby="diagnostics-title">
            <div className={styles.diagnosticsHeader}>
              <MetricSectionHeading
                title={<span id="diagnostics-title">Model and agent metrics</span>}
                subtitle="Review reported model usage and project run outcomes."
              />
            </div>
            <Text className={styles.healthCopy}>Agent leaderboard and latency percentiles are unavailable. The Gateway does not report them yet.</Text>
            {!dashboardModel.modelTelemetry && !dashboardModel.hasAgentUsage && dashboardModel.leaderboard.length === 0 ? (
              <div className={styles.diagnosticsEmpty}>
                <div>
                  <Text as="h3" className={styles.columnTitle}>
                    {hasSummaryActivity(currentData.summary)
                      ? 'No model or agent usage for this range.'
                      : 'No model or agent data.'}
                  </Text>
                  <MetricEmptyState>
                    {currentUsageError ? `Model usage could not load: ${currentUsageError}` : hasSummaryActivity(currentData.summary)
                      ? 'No model or agent usage was reported for this range. Agent scores are unavailable. Open Board or Orchestrations to review recent work.'
                      : 'Start a task. The dashboard adds model and agent data after a run reports it.'}
                  </MetricEmptyState>
                  <Text as="h3" className={styles.columnTitle}>Model metrics</Text>
                  <MetricEmptyState>{currentUsageError ? 'Model usage could not load.' : 'No model usage has been recorded for this range.'}</MetricEmptyState>
                  <Text as="h3" className={styles.columnTitle}>Agent usage</Text>
                  <MetricEmptyState>{currentUsageError ? 'Agent usage could not load.' : 'No agent usage has been recorded for this range.'}</MetricEmptyState>
                  <Text as="h3" className={styles.columnTitle}>Agent leaderboard</Text>
                  <MetricEmptyState>Agent scores are unavailable from the Gateway. Open Flow to review agent runs.</MetricEmptyState>
                  <div className={styles.evidenceRail} aria-label="Available dashboard evidence">
                    <span className={styles.evidencePill}>
                      {currentData.summary.runs_this_week == null
                        ? '7-day run count unavailable'
                        : `${plural(currentData.summary.runs_this_week, 'run')} in the last 7 days`}
                    </span>
                    <span className={styles.evidencePill}>Task completions unavailable</span>
                    <span className={styles.evidencePill}>{plural(currentData.summary.runs_total, 'total run')}</span>
                    <span className={styles.evidencePill}>{plural(currentData.summary.active_runs, 'active run')}</span>
                  </div>
                </div>
                <Link
                  to={`/projects/${encodeURIComponent(projectId)}/${dashboardModel.primaryAction.path}`}
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
                      {dashboardModel.modelTelemetry || dashboardModel.hasAgentUsage || dashboardModel.leaderboard.length > 0
                        ? 'Model and agent data is available.'
                        : 'Some model or agent data is missing.'}
                    </Text>
                  </div>
                  <div className={styles.evidenceRail} aria-label="Model and agent data status">
                    <span className={styles.evidencePill}>{dashboardModel.modelTelemetry ? 'Model data available' : 'No model data'}</span>
                    <span className={styles.evidencePill}>{dashboardModel.hasAgentUsage ? 'Agent usage available' : 'No agent usage'}</span>
                    <span className={styles.evidencePill}>{dashboardModel.leaderboard.length > 0 ? 'Agent scores available' : 'No agent scores'}</span>
                  </div>
                </div>

                <div className={styles.diagnosticsBody}>
                  <div className={styles.diagnosticsColumn} aria-labelledby="model-performance-title">
                    <Text as="h3" id="model-performance-title" className={styles.columnTitle}>Model metrics</Text>
                    {currentUsageError ? <MetricEmptyState>Model usage could not load: {currentUsageError}</MetricEmptyState>
                      : currentUsage?.usageByModel.length ? (
                        <Table aria-label="Model usage" size="small">
                          <TableHeader><TableRow><TableHeaderCell>Model</TableHeaderCell><TableHeaderCell>Requests</TableHeaderCell><TableHeaderCell>Tokens</TableHeaderCell><TableHeaderCell>Cost</TableHeaderCell></TableRow></TableHeader>
                          <TableBody>{currentUsage.usageByModel.map((row) => (
                            <TableRow key={row.id}>
                              <TableCell>{row.id}</TableCell>
                              <TableCell>{row.requestCount ?? '—'}</TableCell>
                              <TableCell>{row.inputTokens != null && row.outputTokens != null ? row.inputTokens + row.outputTokens : '—'}</TableCell>
                              <TableCell>{row.cost.length ? row.cost.map(formatCost).join(', ') : '—'}</TableCell>
                            </TableRow>
                          ))}</TableBody>
                        </Table>
                      ) : <MetricEmptyState>No model usage has been recorded for this range.</MetricEmptyState>}
                  </div>

                  <div className={styles.diagnosticsColumn} aria-labelledby="agent-leaderboard-title">
                    <Text as="h3" id="agent-usage-title" className={styles.columnTitle}>Agent usage</Text>
                    {currentUsageError ? <MetricEmptyState>Agent usage could not load: {currentUsageError}</MetricEmptyState>
                      : currentUsage?.usageByAgent.length ? (
                        <Table aria-label="Agent usage" size="small">
                          <TableHeader>
                            <TableRow>
                              <TableHeaderCell>Agent</TableHeaderCell>
                              <TableHeaderCell>Runs</TableHeaderCell>
                              <TableHeaderCell>Requests</TableHeaderCell>
                              <TableHeaderCell>Tokens</TableHeaderCell>
                              <TableHeaderCell>Average duration</TableHeaderCell>
                              <TableHeaderCell>Cost</TableHeaderCell>
                            </TableRow>
                          </TableHeader>
                          <TableBody>{sortUsage(currentUsage.usageByAgent).map((row) => (
                            <TableRow key={row.id}>
                              <TableCell>{agentNames.get(row.id) ?? row.id}</TableCell>
                              <TableCell>{row.runs}</TableCell>
                              <TableCell>{row.requestCount ?? '—'}</TableCell>
                              <TableCell>
                                {row.inputTokens != null && row.outputTokens != null
                                  ? row.inputTokens + row.outputTokens
                                  : '—'}
                              </TableCell>
                              <TableCell>{formatDuration(row.avgDurationMs)}</TableCell>
                              <TableCell>{row.cost.length ? row.cost.map(formatCost).join(', ') : '—'}</TableCell>
                            </TableRow>
                          ))}</TableBody>
                        </Table>
                      ) : <MetricEmptyState>No agent usage has been recorded for this range.</MetricEmptyState>}
                    <div className={styles.leaderboardHeader}>
                      <Text as="h3" id="agent-leaderboard-title" className={styles.columnTitle}>Agent leaderboard</Text>
                      <Text className={styles.healthCopy}>Agent scores are unavailable. Open Flow to review agent runs.</Text>
                    </div>
                    {dashboardModel.leaderboard.length === 0 ? (
                      <div className={styles.leaderboardPending}>
                        <MetricEmptyState>
                          No agent scores are available for this range. Open Flow to review agent runs.
                        </MetricEmptyState>
                        <Link to={`/projects/${encodeURIComponent(projectId)}/flow`} className={styles.actionLink}>
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
                                      to={`/projects/${encodeURIComponent(projectId)}/flow?agent=${encodeURIComponent(row.agentName)}`}
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
