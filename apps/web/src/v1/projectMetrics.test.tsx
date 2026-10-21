import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectMetrics, ProjectUsage, ProjectUsageItem } from './contracts';
import {
  formatCost,
  formatCount,
  formatDuration,
  metricTiles,
  metricsErrorMessage,
  metricsRange,
  successRate,
  sortUsage,
  useProjectMetrics,
} from './projectMetrics';

const mocks = vi.hoisted(() => ({ getProjectMetrics: vi.fn(), getProjectUsage: vi.fn(), apiCall: vi.fn() }));

vi.mock('./AuthContext', () => ({ useAuth: () => ({ apiCall: mocks.apiCall }) }));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: { getProjectMetrics: mocks.getProjectMetrics, getProjectUsage: mocks.getProjectUsage },
}));

import { GatewayError } from './api';

function metrics(overrides: Partial<ProjectMetrics> = {}): ProjectMetrics {
  return {
    projectId: 'p1',
    from: '2026-01-01T00:00:00Z',
    to: '2026-01-31T00:00:00Z',
    activeRuns: 2,
    blockedRuns: 1,
    runsInRange: 10,
    runsTotal: 40,
    completedRuns: 8,
    succeededRuns: 6,
    series: [],
    ...overrides,
  };
}

function item(id: string, requestCount: number | null): ProjectUsageItem {
  return {
    id, requestCount, inputTokens: null, outputTokens: null, cachedTokens: null, cacheWriteTokens: null,
    reasoningTokens: null, avgDurationMs: null, runs: 1, unpricedRequestCount: null, cost: [],
  };
}

const usage: ProjectUsage = { projectId: 'p1', from: 'a', to: 'b', usageByModel: [], usageByAgent: [] };

beforeEach(() => {
  mocks.getProjectMetrics.mockReset();
  mocks.getProjectUsage.mockReset();
  mocks.apiCall.mockReset().mockImplementation(<T,>(operation: (token: string, tenant: string | null) => Promise<T>) => operation('token', 'tenant'));
});

describe('metricsRange', () => {
  it('starts at UTC midnight and covers today as the first day', () => {
    const now = new Date('2026-03-10T15:30:00Z');
    expect(metricsRange('7d', now)).toEqual({ from: '2026-03-04T00:00:00.000Z', to: '2026-03-10T15:30:00.000Z' });
    expect(metricsRange('30d', now).from).toBe('2026-02-09T00:00:00.000Z');
  });

  it('keeps the longest range under the 92-day limit', () => {
    const now = new Date('2026-03-10T23:59:59Z');
    const span = metricsRange('90d', now);
    const days = (Date.parse(span.to) - Date.parse(span.from)) / 86_400_000;
    expect(days).toBeLessThanOrEqual(92);
    expect(days).toBeGreaterThan(89);
  });
});

describe('formatting', () => {
  it('shows a dash for values that were not measured', () => {
    expect(formatCount(null)).toBe('—');
    expect(formatCount(0)).toBe('0');
    expect(formatDuration(null)).toBe('—');
    expect(formatDuration(250)).toBe('250 ms');
    expect(formatDuration(2500)).toBe('2.5 s');
    expect(formatDuration(90_000)).toBe('1.5 m');
  });

  it('shows each cost subtotal as reported without pricing it', () => {
    expect(formatCost({ priceUnit: 'USD', disposition: 'estimate', amount: 1.25 })).toBe('1.25 USD · Estimate');
    expect(formatCost({ priceUnit: 'AIC', disposition: 'reconciled', amount: 3 })).toBe('3 AIC · Reconciled');
    expect(formatCost({ priceUnit: null, disposition: 'unpriced', amount: null })).toBe('Unpriced');
    // A missing amount shows a single dash, not one dash per field.
    expect(formatCost({ priceUnit: 'USD', disposition: 'estimate', amount: null })).toBe('— · Estimate');
  });

  it('sorts usage by request count with unmeasured rows last', () => {
    expect(sortUsage([item('b', null), item('a', 2), item('c', 5)]).map((row) => row.id)).toEqual(['c', 'a', 'b']);
  });

  it('gives a plain message for 403', () => {
    expect(metricsErrorMessage(new GatewayError(403, {}, 'Forbidden'), 'usage')).toBe('You do not have permission to read usage for this project.');
    expect(metricsErrorMessage(new Error('Down'), 'run metrics')).toBe('Down');
  });
});

describe('success and tiles', () => {
  it('has no success rate without completed runs', () => {
    expect(successRate({ completedRuns: 0, succeededRuns: 0 })).toBeNull();
    expect(successRate({ completedRuns: 4, succeededRuns: 3 })).toBe(0.75);
  });

  it('builds tiles from metrics', () => {
    const tiles = metricTiles(metrics(), '30d', false);
    expect(tiles.map((tile) => [tile.label, tile.value])).toEqual([
      ['Active runs', '2'], ['Needs input', '1'], ['Runs in range', '10'], ['Success', '75%'],
    ]);
    expect(tiles[2].meta).toBe('In the last 30 days · 40 total');
    expect(tiles[3].meta).toBe('6 of 8 completed runs succeeded');
    expect(metricTiles(metrics({ completedRuns: 0, succeededRuns: 0 }), '7d', false)[3]).toMatchObject({ value: '—', meta: 'No completed runs in this range' });
  });

  it('shows dashes while loading or after a failure', () => {
    expect(metricTiles(null, '30d', true).every((tile) => tile.value === '—' && tile.meta === 'Loading…')).toBe(true);
    expect(metricTiles(null, '30d', false)[0].meta).toBe('Not available');
  });
});

describe('useProjectMetrics', () => {
  it('loads metrics and usage with the unbound token and the chosen range', async () => {
    mocks.getProjectMetrics.mockResolvedValue(metrics());
    mocks.getProjectUsage.mockResolvedValue(usage);
    const { result } = renderHook(() => useProjectMetrics('p1', '7d'));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.metrics?.activeRuns).toBe(2);
    expect(result.current.usage).toBe(usage);
    expect(mocks.apiCall).toHaveBeenCalledWith(expect.any(Function), null);
    const [, projectId, span, tenant] = mocks.getProjectMetrics.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(tenant).toBe('tenant');
    expect(Object.keys(span)).toEqual(['from', 'to']);
    expect(mocks.getProjectUsage.mock.calls[0][2]).toEqual(span);
  });

  it('keeps usage when metrics fail', async () => {
    mocks.getProjectMetrics.mockRejectedValue(new GatewayError(403, {}, 'Forbidden'));
    mocks.getProjectUsage.mockResolvedValue(usage);
    const { result } = renderHook(() => useProjectMetrics('p1', '30d'));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.metrics).toBeNull();
    expect(result.current.metricsError).toBeInstanceOf(GatewayError);
    expect(result.current.usage).toBe(usage);
    expect(result.current.usageError).toBeNull();
  });

  it('loads nothing without a project', async () => {
    const { result } = renderHook(() => useProjectMetrics(null, '30d'));
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(result.current.loading).toBe(false);
    expect(mocks.apiCall).not.toHaveBeenCalled();
  });
});
