/**
 * Data and formatting for one project's run metrics and model and agent usage.
 * The Dashboard and the Overview both use these. The service returns metrics
 * per project only, for a half-open UTC range of at most 92 days.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { GatewayError, gatewayClient } from './api';
import { useAuth } from './AuthContext';
import type { ProjectMetrics, ProjectUsage, ProjectUsageCost, ProjectUsageItem } from './contracts';
import { errorMessage } from './errors';

/** Range choices from the 0.x range selector. */
export type MetricsRangeKey = '7d' | '30d' | '90d';

/** Range choices in display order, with their length in days. */
export const METRICS_RANGES: ReadonlyArray<{ key: MetricsRangeKey; label: string; days: number }> = [
  { key: '7d', label: 'Last 7 days', days: 7 },
  { key: '30d', label: 'Last 30 days', days: 30 },
  { key: '90d', label: 'Last 90 days', days: 90 },
];

/** Text shown for a measurement that the service did not record. */
export const NOT_MEASURED = '—';

const DAY_MS = 24 * 60 * 60 * 1000;

/** Returns the lower-case range label used in sentences, for example "last 30 days". */
export function metricsRangeLabel(key: MetricsRangeKey): string {
  return (METRICS_RANGES.find((range) => range.key === key) ?? METRICS_RANGES[1]).label.toLowerCase();
}

/**
 * Builds the `from` and `to` query values for a range. `from` is UTC midnight
 * at the start of the first day, so the range covers whole UTC days plus today
 * so far. `to` is now. The longest choice is 90 days, under the 92-day limit.
 */
export function metricsRange(key: MetricsRangeKey, now: Date = new Date()): { from: string; to: string } {
  const days = (METRICS_RANGES.find((range) => range.key === key) ?? METRICS_RANGES[1]).days;
  const todayStart = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  // Today counts as the first day, so step back one day less than the range length.
  const from = new Date(todayStart - (days - 1) * DAY_MS);
  return { from: from.toISOString(), to: now.toISOString() };
}

/**
 * Returns succeeded runs divided by completed runs, from 0 to 1. Returns null
 * when no run completed in the range, because a rate of 0 would be wrong.
 */
export function successRate(metrics: Pick<ProjectMetrics, 'completedRuns' | 'succeededRuns'>): number | null {
  return metrics.completedRuns > 0 ? metrics.succeededRuns / metrics.completedRuns : null;
}

/** Formats a count. Null means "not measured" and shows a dash, never 0. */
export function formatCount(value: number | null | undefined): string {
  return value == null ? NOT_MEASURED : value.toLocaleString();
}

/** Formats a duration in milliseconds as ms, s, m, or h. Null shows a dash. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return NOT_MEASURED;
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(1)} m`;
  return `${(minutes / 60).toFixed(1)} h`;
}

/** Plain labels for cost dispositions. */
const DISPOSITION_LABELS: Record<ProjectUsageCost['disposition'], string> = {
  estimate: 'Estimate',
  reconciled: 'Reconciled',
  unpriced: 'Unpriced',
};

/**
 * Formats one cost subtotal as reported, for example "1.25 USD · Estimate".
 * Subtotals are never added together or converted: each unit and disposition
 * stays on its own line. A null amount or unit shows a dash.
 */
export function formatCost(cost: ProjectUsageCost): string {
  const amount = cost.amount == null ? NOT_MEASURED : cost.amount.toLocaleString(undefined, { maximumFractionDigits: 6 });
  const unit = cost.priceUnit ?? NOT_MEASURED;
  return `${amount} ${unit} · ${DISPOSITION_LABELS[cost.disposition] ?? cost.disposition}`;
}

/**
 * Sorts usage rows by request count, highest first. Rows with no measured
 * count go last. Ties sort by ID so the order is stable.
 */
export function sortUsage(items: readonly ProjectUsageItem[]): ProjectUsageItem[] {
  return [...items].sort((a, b) => {
    if (a.requestCount == null && b.requestCount != null) return 1;
    if (b.requestCount == null && a.requestCount != null) return -1;
    const diff = (b.requestCount ?? 0) - (a.requestCount ?? 0);
    return diff !== 0 ? diff : a.id.localeCompare(b.id);
  });
}

/** One summary tile: a label, a main value, and a caption. */
export interface MetricTile {
  key: 'active' | 'blocked' | 'range' | 'success';
  label: string;
  value: string;
  meta: string;
}

/**
 * Builds the four 0.x summary tiles from run metrics: active runs, runs that
 * need input, runs in the range, and success. Without metrics every value is a
 * dash and the caption says whether data is loading or not available.
 */
export function metricTiles(metrics: ProjectMetrics | null, range: MetricsRangeKey, loading: boolean): MetricTile[] {
  const label = metricsRangeLabel(range);
  if (!metrics) {
    const meta = loading ? 'Loading…' : 'Not available';
    return [
      { key: 'active', label: 'Active runs', value: NOT_MEASURED, meta },
      { key: 'blocked', label: 'Needs input', value: NOT_MEASURED, meta },
      { key: 'range', label: 'Runs in range', value: NOT_MEASURED, meta },
      { key: 'success', label: 'Success', value: NOT_MEASURED, meta },
    ];
  }
  const rate = successRate(metrics);
  return [
    { key: 'active', label: 'Active runs', value: formatCount(metrics.activeRuns), meta: 'Working or waiting now' },
    { key: 'blocked', label: 'Needs input', value: formatCount(metrics.blockedRuns), meta: 'Blocked on a decision or input' },
    { key: 'range', label: 'Runs in range', value: formatCount(metrics.runsInRange), meta: `In the ${label} · ${formatCount(metrics.runsTotal)} total` },
    {
      key: 'success',
      label: 'Success',
      // Success is succeeded runs out of completed runs. With no completed runs there is no rate.
      value: rate == null ? NOT_MEASURED : `${Math.round(rate * 100)}%`,
      meta: metrics.completedRuns > 0
        ? `${formatCount(metrics.succeededRuns)} of ${formatCount(metrics.completedRuns)} completed runs succeeded`
        : 'No completed runs in this range',
    },
  ];
}

/** Formats a metrics or usage failure. A 403 gets a plain permission message. */
export function metricsErrorMessage(error: unknown, subject: 'run metrics' | 'usage'): string {
  if (error instanceof GatewayError && error.status === 403) return `You do not have permission to read ${subject} for this project.`;
  return errorMessage(error);
}

/** Metrics and usage for one project and range, with request state. */
export interface ProjectMetricsData {
  /** Last loaded run metrics, or null before the first load or after a failure. */
  metrics: ProjectMetrics | null;
  /** Last loaded usage, or null before the first load or after a failure. */
  usage: ProjectUsage | null;
  metricsError: unknown;
  usageError: unknown;
  /** True while a request is running. */
  loading: boolean;
  loadedAt: Date | null;
  reload: () => void;
}

/**
 * Loads run metrics and usage for one project and range. Pass null to load
 * nothing. Both calls use the unbound project token. Each call fails on its
 * own, so usage can show when metrics fail and the other way round.
 */
export function useProjectMetrics(projectId: string | null, range: MetricsRangeKey): ProjectMetricsData {
  const { apiCall } = useAuth();
  const [metrics, setMetrics] = useState<ProjectMetrics | null>(null);
  const [usage, setUsage] = useState<ProjectUsage | null>(null);
  const [metricsError, setMetricsError] = useState<unknown>(null);
  const [usageError, setUsageError] = useState<unknown>(null);
  const [loading, setLoading] = useState(projectId != null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  // Each load gets a number. A response from an older load is dropped, so a
  // slow reply for an old project or range cannot replace newer data.
  const requestRef = useRef(0);
  // The project the shown data belongs to. Data from another project is
  // cleared before the new load, so it is never shown under the wrong name.
  const loadedProjectRef = useRef<string | null>(null);

  const load = useCallback(async () => {
    if (!projectId) return;
    const request = ++requestRef.current;
    if (loadedProjectRef.current !== projectId) {
      setMetrics(null);
      setUsage(null);
      loadedProjectRef.current = projectId;
    }
    setLoading(true);
    const span = metricsRange(range);
    const [metricsResult, usageResult] = await Promise.allSettled([
      apiCall((token, tenant) => gatewayClient.getProjectMetrics(token, projectId, span, tenant), null),
      apiCall((token, tenant) => gatewayClient.getProjectUsage(token, projectId, span, tenant), null),
    ]);
    if (request !== requestRef.current) return;
    // A failed call clears its data, so old numbers are not shown as current.
    if (metricsResult.status === 'fulfilled') { setMetrics(metricsResult.value); setMetricsError(null); }
    else { setMetrics(null); setMetricsError(metricsResult.reason); }
    if (usageResult.status === 'fulfilled') { setUsage(usageResult.value); setUsageError(null); }
    else { setUsage(null); setUsageError(usageResult.reason); }
    setLoadedAt(new Date());
    setLoading(false);
  }, [apiCall, projectId, range]);

  // Start the load in a zero-delay timer so no state changes inside the effect
  // body. The cleanup cancels the timer and marks any running load as old.
  useEffect(() => {
    const timer = window.setTimeout(() => { void load(); }, 0);
    return () => {
      window.clearTimeout(timer);
      requestRef.current += 1;
    };
  }, [load]);

  const reload = useCallback(() => { void load(); }, [load]);
  return { metrics, usage, metricsError, usageError, loading, loadedAt, reload };
}
