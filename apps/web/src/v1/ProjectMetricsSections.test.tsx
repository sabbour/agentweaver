import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type { ProjectMetrics, ProjectUsage } from './contracts';
import type { ProjectMetricsData } from './projectMetrics';
import { ProjectMetricsSections } from './ProjectMetricsSections';

function data(overrides: Partial<ProjectMetricsData> = {}): ProjectMetricsData {
  return { metrics: null, usage: null, metricsError: null, usageError: null, loading: false, loadedAt: null, reload: vi.fn(), ...overrides };
}

const metrics: ProjectMetrics = {
  projectId: 'p1', from: 'a', to: 'b', activeRuns: 1, blockedRuns: 0, runsInRange: 3, runsTotal: 9, completedRuns: 2, succeededRuns: 1,
  series: [{ date: '2026-01-02', created: 1, completed: 2 }, { date: '2026-01-01', created: 2, completed: 0 }],
};

const usage: ProjectUsage = {
  projectId: 'p1', from: 'a', to: 'b',
  usageByModel: [{
    id: 'gpt-test', requestCount: 12, inputTokens: 1000, outputTokens: null, cachedTokens: 0, cacheWriteTokens: null,
    reasoningTokens: null, avgDurationMs: 1500, runs: 2, unpricedRequestCount: 3,
    cost: [{ priceUnit: 'USD', disposition: 'estimate', amount: 0.5 }, { priceUnit: 'AIC', disposition: 'reconciled', amount: 7 }],
  }],
  usageByAgent: [{
    id: 'a1', requestCount: null, inputTokens: null, outputTokens: null, cachedTokens: null, cacheWriteTokens: null,
    reasoningTokens: null, avgDurationMs: null, runs: 1, unpricedRequestCount: null, cost: [],
  }],
};

describe('ProjectMetricsSections', () => {
  it('shows the chart, totals, and usage tables', () => {
    render(<ProjectMetricsSections data={data({ metrics, usage })} range="30d" onRangeChange={vi.fn()} agentNames={new Map([['a1', 'Ada']])} />);
    expect(screen.getByRole('img').getAttribute('aria-label')).toBe('Created and completed runs per day: 3 created and 2 completed from 2026-01-01 to 2026-01-02.');
    const totals = screen.getByRole('complementary');
    expect(totals.textContent).toContain('Created3');
    expect(totals.textContent).toContain('Completed2');
    const models = screen.getByRole('table', { name: 'Usage by model' });
    const modelRow = within(models).getAllByRole('row')[1];
    expect(modelRow.textContent).toContain('gpt-test');
    expect(modelRow.textContent).toContain('1,000');
    expect(modelRow.textContent).toContain('1.5 s');
    // Each cost subtotal stays on its own line with its unit.
    expect(within(modelRow).getByText('0.5 USD · Estimate')).toBeTruthy();
    expect(within(modelRow).getByText('7 AIC · Reconciled')).toBeTruthy();
    const agentRow = within(screen.getByRole('table', { name: 'Usage by agent' })).getAllByRole('row')[1];
    expect(within(agentRow).getByText('Ada')).toBeTruthy();
    expect(within(agentRow).getByText('a1')).toBeTruthy();
    // Unmeasured values show a dash, never 0.
    expect(within(agentRow).getAllByText('—').length).toBeGreaterThanOrEqual(8);
  });

  it('changes the range', () => {
    const onRangeChange = vi.fn();
    render(<ProjectMetricsSections data={data({ metrics, usage })} range="30d" onRangeChange={onRangeChange} />);
    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '90d' } });
    expect(onRangeChange).toHaveBeenCalledWith('90d');
  });

  it('shows empty states', () => {
    render(<ProjectMetricsSections data={data({ metrics: { ...metrics, series: [] }, usage: { ...usage, usageByModel: [], usageByAgent: [] } })} range="7d" onRangeChange={vi.fn()} />);
    expect(screen.getByText('No runs were created or completed in this range.')).toBeTruthy();
    expect(screen.getByText('No model usage in this range.')).toBeTruthy();
    expect(screen.getByText('No agent usage in this range.')).toBeTruthy();
  });

  it('shows loading', () => {
    render(<ProjectMetricsSections data={data({ loading: true })} range="7d" onRangeChange={vi.fn()} />);
    expect(screen.getByText('Loading run metrics')).toBeTruthy();
    expect(screen.getByText('Loading usage')).toBeTruthy();
  });

  it('shows permission and other errors', () => {
    render(<ProjectMetricsSections
      data={data({ metricsError: new GatewayError(403, {}, 'Forbidden'), usageError: new Error('Usage is down') })}
      range="7d"
      onRangeChange={vi.fn()}
    />);
    expect(screen.getByText('You do not have permission to read run metrics for this project.')).toBeTruthy();
    expect(screen.getByText('Usage is down')).toBeTruthy();
    expect(screen.getByText('Run metrics are not available.')).toBeTruthy();
    expect(screen.getByText('Usage is not available.')).toBeTruthy();
  });
});
