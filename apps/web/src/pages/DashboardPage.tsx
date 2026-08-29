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
  BotRegular,
  CheckmarkCircleRegular,
  ClockRegular,
  FlowRegular,
  WarningRegular,
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
    gridTemplateColumns: 'minmax(280px, .9fr) minmax(0, 1.45fr) minmax(260px, .75fr)',
    gap: tokens.spacingHorizontalL,
    alignItems: 'stretch',
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    '@media (max-width: 1120px)': { gridTemplateColumns: '1fr 1fr' },
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr', padding: tokens.spacingVerticalM },
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
    '@media (max-width: 1120px)': { gridColumn: '1 / -1' },
    '@media (max-width: 720px)': { gridTemplateColumns: '1fr' },
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
  interventionPanel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    minWidth: 0,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 1120px)': { gridColumn: '2' },
    '@media (max-width: 760px)': { gridColumn: 'auto' },
  },
  interventionHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  interventionList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    margin: 0,
    padding: 0,
    listStyleType: 'none',
  },
  interventionItem: {
    display: 'grid',
    gridTemplateColumns: '24px minmax(0, 1fr)',
    gap: tokens.spacingHorizontalS,
    alignItems: 'start',
    color: tokens.colorNeutralForeground2,
  },
  decisionLead: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  iconHealthy: { color: tokens.colorPaletteGreenForeground1, flexShrink: 0 },
  iconWarning: { color: tokens.colorStatusWarningForeground1, flexShrink: 0 },
  iconMuted: { color: tokens.colorNeutralForeground3, flexShrink: 0 },
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
        label: 'Intervention likely',
        badge: 'warning',
        title: 'Review agent quality before trusting throughput.',
        body: 'At least one contributor is below the success threshold. Start with the leaderboard, then inspect that agent in flow.',
      };
    case 'active':
      return {
        label: 'In motion',
        badge: 'success',
        title: 'Agents are producing in this project.',
        body: hasQualityEvidence
          ? `${plural(summary.active_runs, 'active run')} and ${plural(summary.active_agents, 'active agent')} are live. Watch the board for ownership, or use flow when you need execution detail.`
          : `${plural(summary.active_runs, 'active run')} and ${plural(summary.active_agents, 'active agent')} are live. This is activity evidence; quality telemetry is still pending until scored agent or model signals arrive.`,
      };
    case 'steady':
      return {
        label: 'No live pressure',
        badge: 'success',
        title: 'No live pressure detected.',
        body: `${plural(summary.runs_this_week, 'run')} and ${plural(summary.tasks_done_this_week, 'task')} done this week are backed by quality evidence. No action is needed unless you want to review completed work.`,
      };
    case 'insufficient':
      return {
        label: 'Recent activity',
        badge: 'subtle',
        title: 'Recent activity, telemetry pending.',
        body: hasSummaryActivity(summary)
          ? `Summary shows ${plural(summary.runs_this_week, 'run')} this week, ${plural(summary.tasks_done_this_week, 'task')} done this week, and ${plural(summary.runs_total, 'total run')}. Quality and model telemetry are not ready yet.`
          : 'Throughput exists in the selected range, but quality and model telemetry are not ready yet.',
      };
    case 'quiet':
      return {
        label: 'Quiet',
        badge: 'subtle',
        title: 'No current project activity.',
        body: 'There is no recent run pressure. Open the board or orchestrations when you are ready to queue more work.',
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
      return { label: 'Review flow', path: 'flow' };
    case 'active':
      return { label: 'Open board', path: 'board' };
    case 'steady':
      return { label: 'Review board', path: 'board' };
    case 'insufficient':
      if (hasSummaryActivity(summary)) return { label: 'Review board', path: 'board' };
      return { label: 'Review flow', path: 'flow' };
    case 'quiet':
      return { label: 'Start task', path: 'orchestrations' };
  }
}

function secondaryActionsFor(
  tone: HealthTone,
  summary: ProjectDashboardDto['summary'],
): Array<{ label: string; path: 'board' | 'flow' | 'orchestrations' }> {
  if (tone === 'attention') return [{ label: 'Board', path: 'board' }, { label: 'Orchestrations', path: 'orchestrations' }];
  if (tone === 'active') return [{ label: 'Flow', path: 'flow' }, { label: 'Orchestrations', path: 'orchestrations' }];
  if (tone === 'steady') return [{ label: 'Flow', path: 'flow' }, { label: 'Orchestrations', path: 'orchestrations' }];
  if (tone === 'insufficient' && hasSummaryActivity(summary)) return [{ label: 'Orchestrations', path: 'orchestrations' }, { label: 'Flow', path: 'flow' }];
  return [{ label: 'Board', path: 'board' }, { label: 'Flow', path: 'flow' }];
}

function decisionLeadFor(tone: HealthTone, summary: ProjectDashboardDto['summary']): string {
  switch (tone) {
    case 'attention':
      return 'Next click: inspect Flow for the agent below the quality threshold.';
    case 'active':
      return summary.active_runs > 0
        ? 'Next click: open Board to see ownership and active run status.'
        : 'Next click: open Board to inspect current agent work.';
    case 'steady':
      return 'No action required. Open Board only if you want to review completed work.';
    case 'insufficient':
      return hasSummaryActivity(summary)
        ? 'Next click: review Board or Orchestrations; summary activity exists while telemetry catches up.'
        : 'Next click: review Flow or wait for more telemetry before judging quality.';
    case 'quiet':
      return 'Next click: start a task when you are ready to create project activity.';
  }
}

function primaryRationale(
  actionLabel: string,
  tone: HealthTone,
  summary: ProjectDashboardDto['summary'],
): string {
  if (tone === 'active') {
    return `${actionLabel} is primary because live pressure shows ${plural(summary.active_runs, 'active run')} and ${plural(summary.active_agents, 'active agent')}.`;
  }
  if (tone === 'insufficient' && hasSummaryActivity(summary)) {
    return `${actionLabel} is primary because summary evidence shows ${plural(summary.runs_this_week, 'run')} this week and ${plural(summary.tasks_done_this_week, 'task')} done this week, even though telemetry is pending.`;
  }
  if (tone === 'quiet') {
    return `${actionLabel} is primary because no live or recent summary activity is visible.`;
  }
  return `${actionLabel} is primary because this project is ${statusCopy(tone, summary).label.toLowerCase()}.`;
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
    <div className={styles.loadingShell} role="status" aria-label="Loading dashboard">
      <Spinner label="Loading dashboard" />
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
    const topAgent = leaderboard[0] ?? null;
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
      topAgent,
      tone,
      copy: statusCopy(tone, data.summary, success.basis > 0 || modelTelemetry),
      balance: done - created,
      modelTelemetry,
      primaryAction,
      secondaryActions: secondaryActionsFor(tone, data.summary),
      decisionLead: decisionLeadFor(tone, data.summary),
      primaryRationale: primaryRationale(primaryAction.label, tone, data.summary),
    };
  }, [data, metrics]);

  if (!projectId) return null;

  return (
    <div className={styles.root}>
      <PageHeader
        title="Dashboard"
        subtitle="Live work, agent output, and throughput quality for this project."
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
          title="Couldn't load dashboard"
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
                <Text className={styles.summaryLabel}>Live pressure</Text>
                <Text className={styles.summaryValue}>{data.summary.active_runs}</Text>
                <Text className={styles.summaryMeta}>{plural(data.summary.active_agents, 'active agent')}</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Recent runs</Text>
                <Text className={styles.summaryValue}>{data.summary.runs_this_week}</Text>
                <Text className={styles.summaryMeta}>{plural(data.summary.runs_total, 'total run')}</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Tasks done</Text>
                <Text className={styles.summaryValue}>{data.summary.tasks_done_this_week}</Text>
                <Text className={styles.summaryMeta}>done this week</Text>
              </div>
              <div className={styles.summaryTile}>
                <Text className={styles.summaryLabel}>Quality evidence</Text>
                <Text className={styles.summaryValue}>{dashboardModel.success.rate == null ? 'Pending' : `${Math.round(dashboardModel.success.rate)}%`}</Text>
                <Text className={styles.summaryMeta}>
                  {dashboardModel.success.basis
                    ? `${dashboardModel.success.basis} scored runs`
                    : dashboardModel.modelTelemetry
                      ? 'Model telemetry present'
                      : 'Waiting for scored runs'}
                </Text>
              </div>
            </div>

            <aside className={styles.interventionPanel} aria-labelledby="intervention-title">
              <div className={styles.interventionHeader}>
                {dashboardModel.tone === 'attention'
                  ? <WarningRegular className={styles.iconWarning} aria-hidden="true" />
                  : dashboardModel.tone === 'quiet' || dashboardModel.tone === 'insufficient'
                    ? <ClockRegular className={styles.iconMuted} aria-hidden="true" />
                    : <CheckmarkCircleRegular className={styles.iconHealthy} aria-hidden="true" />}
                <Text as="h2" id="intervention-title" weight="semibold">Decision guide</Text>
              </div>
              <ul className={styles.interventionList}>
                <li className={styles.decisionLead}>
                  <Text weight="semibold">{dashboardModel.decisionLead}</Text>
                  <Text className={styles.healthCopy}>
                    {dashboardModel.primaryRationale}
                  </Text>
                </li>
                <li className={styles.interventionItem}>
                  <ClockRegular className={styles.iconMuted} aria-hidden="true" />
                  <Text>
                    {data.summary.active_runs > 0
                      ? `Inspect the board when ${plural(data.summary.active_runs, 'active run')} ${data.summary.active_runs === 1 ? 'needs' : 'need'} ownership.`
                      : 'No active run pressure is visible.'}
                  </Text>
                </li>
                <li className={styles.interventionItem}>
                  <BotRegular className={styles.iconMuted} aria-hidden="true" />
                  <Text>
                    {dashboardModel.topAgent
                      ? `${dashboardModel.topAgent.agentName} is the current highest-output agent.`
                      : `Summary still shows ${plural(data.summary.runs_this_week, 'run')} this week and ${plural(data.summary.tasks_done_this_week, 'task')} done this week.`}
                  </Text>
                </li>
                <li className={styles.interventionItem}>
                  <FlowRegular className={styles.iconMuted} aria-hidden="true" />
                  <Text>
                    {dashboardModel.created + dashboardModel.done === 0
                      ? 'No throughput trend is available for this range.'
                      : dashboardModel.balance < 0
                        ? 'Created work is ahead of completions; review flow or orchestration queues.'
                        : 'Completed throughput is keeping pace with created work.'}
                  </Text>
                </li>
              </ul>
            </aside>
          </section>

          <div className={styles.sharedMetricsHeader}>
            <MetricSectionHeading
              title="Operational signals"
              subtitle="Use the range control to compare throughput and decide whether work needs attention."
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
                    title="Throughput"
                    subtitle={`Created versus completed runs across the ${timeRangeLabel(selectedRange)}.`}
                  />
                  <div className={styles.legend} aria-label="Throughput legend">
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
                  <MetricEmptyState>No throughput data yet. Start or complete runs to populate this chart.</MetricEmptyState>
                ) : (
                  <ThroughputChart points={dashboardModel.throughput} />
                )}
              </div>
            </section>

            <aside className={styles.sideStack} aria-label="Project signal summary">
              <div className={styles.panel}>
                <MetricSectionHeading title="Run creation summary" subtitle={`Created and completed run totals for the ${timeRangeLabel(selectedRange)}.`} />
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
                subtitle={`Daily project run creations across the ${timeRangeLabel(selectedRange)}.`}
                emptyLabel="No run-creation telemetry for this range. Start a task to populate this trend."
              />
            </aside>
          </div>

          <section className={styles.diagnosticsSurface} aria-labelledby="diagnostics-title">
            <div className={styles.diagnosticsHeader}>
              <MetricSectionHeading
                title={<span id="diagnostics-title">Diagnostics and quality</span>}
                subtitle="Model telemetry and agent reliability share one desktop work area so missing signals and next actions stay connected."
              />
            </div>
            {!dashboardModel.modelTelemetry && dashboardModel.leaderboard.length === 0 ? (
              <div className={styles.diagnosticsEmpty}>
                <div>
                  <Text as="h3" className={styles.columnTitle}>
                    {hasSummaryActivity(data.summary)
                      ? 'Activity exists; diagnostics are still catching up.'
                      : 'Diagnostics are waiting for the first run.'}
                  </Text>
                  <MetricEmptyState>
                    {hasSummaryActivity(data.summary)
                      ? 'Summary evidence is present, but this range has no model telemetry or agent leaderboard rows yet. Review Board or Orchestrations for the latest work, then let a run complete to populate quality, latency, and cost diagnostics.'
                      : 'Start an agent task to populate model telemetry, quality, latency, cost, and leaderboard diagnostics.'}
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
                        ? 'Quality evidence is ready to inspect.'
                        : 'Quality evidence is partial.'}
                    </Text>
                    <Text className={styles.healthCopy}>
                      Run creation is activity telemetry and stays with Operational signals above. This area only counts model, cost, latency, and scored-agent evidence when those signals are present.
                    </Text>
                  </div>
                  <div className={styles.evidenceRail} aria-label="Diagnostics evidence state">
                    <span className={styles.evidencePill}>{dashboardModel.modelTelemetry ? 'Model signals present' : 'Model signals pending'}</span>
                    <span className={styles.evidencePill}>{dashboardModel.leaderboard.length > 0 ? 'Agent scoring present' : 'Agent scoring pending'}</span>
                  </div>
                </div>

                <div className={styles.diagnosticsBody}>
                  <div className={styles.diagnosticsColumn} aria-labelledby="model-performance-title">
                    <Text as="h3" id="model-performance-title" className={styles.columnTitle}>Model performance</Text>
                    <ModelPerformancePanels metrics={metrics} />
                  </div>

                  <div className={styles.diagnosticsColumn} aria-labelledby="agent-leaderboard-title">
                    <div className={styles.leaderboardHeader}>
                      <Text as="h3" id="agent-leaderboard-title" className={styles.columnTitle}>Agent leaderboard</Text>
                      <Text className={styles.healthCopy}>Drill into an agent when output volume or quality needs review.</Text>
                    </div>
                    {dashboardModel.leaderboard.length === 0 ? (
                      <div className={styles.leaderboardPending}>
                        <MetricEmptyState>
                          No scored agent rows in this range yet. Model diagnostics can be reviewed now; leaderboard links appear after completed runs emit quality results.
                        </MetricEmptyState>
                        <Link to={`/projects/${projectId}/flow`} className={styles.actionLink}>
                          Review flow
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
                              <TableHeaderCell className={styles.headerCell}>Runs total</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Success rate</TableHeaderCell>
                              <TableHeaderCell className={styles.headerCell}>Avg duration</TableHeaderCell>
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
