import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, within, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ProjectDashboardDto, ProjectMetricsDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectDashboard: vi.fn(),
    getProjectMetrics: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { DashboardPage } from '../pages/DashboardPage';

const dto: ProjectDashboardDto = {
  project_id: 'p1',
  project_name: 'Demo',
  generated_utc: new Date().toISOString(),
  summary: {
    runs_this_week: 5,
    runs_total: 20,
    active_runs: 2,
    active_agents: 3,
    tasks_done_this_week: 4,
  },
};

const metricsDto: ProjectMetricsDto = {
  throughput: [
    { date: '2026-06-01', created: 2, done: 1 },
    { date: '2026-06-02', created: 3, done: 2 },
  ],
  leaderboard: [
    {
      agentName: 'Ada',
      role: 'Frontend engineer',
      runsThisWeek: 3,
      runsTotal: 10,
      successRate: 90,
      avgDurationMs: 65000,
      costAic: 12.5,
    },
  ],
};

function renderPage() {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={['/projects/p1']}>
        <Routes>
          <Route path="/projects/:projectId" element={<DashboardPage />} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProjectMetrics).mockResolvedValue(metricsDto);
});
afterEach(() => {
  cleanup();
});

describe('DashboardPage', () => {
  it('renders summary cards, throughput, and the agent leaderboard', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(screen.getByText('Live pressure')).toBeDefined());
    expect(screen.getByText('Recent runs')).toBeDefined();
    expect(screen.getByText('Tasks done')).toBeDefined();
    expect(screen.getByText('Quality evidence')).toBeDefined();
    expect(screen.getByText('20 total runs')).toBeDefined();
    expect(screen.getByText('Throughput')).toBeDefined();
    expect(screen.getByText('Run creation count')).toBeDefined();
    expect(screen.getByText('Daily project run creations across the last 30 days.')).toBeDefined();
    expect(screen.getByText('Diagnostics and quality')).toBeDefined();
    expect(screen.getByText('Model telemetry pending')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.getByText('Ada')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Ada' }).getAttribute('href'))
      .toBe('/projects/p1/flow?agent=Ada');
    expect(screen.getByText('Frontend engineer')).toBeDefined();
    const table = screen.getByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('90%').length).toBeGreaterThan(0);
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual(['Agent', 'Role', 'Runs this week', 'Runs total', 'Success rate', 'Avg duration', 'Cost']);
  });

  it('keeps summary activity visible when quality telemetry is missing', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue({
      ...dto,
      summary: {
        runs_this_week: 6,
        runs_total: 12,
        active_runs: 0,
        active_agents: 0,
        tasks_done_this_week: 3,
      },
    });
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      throughput: [],
      leaderboard: [],
      invocationTrend: [],
      modelUsage: [],
      responseDuration: [],
      timeToFirstToken: [],
      aiCreditUsageTrend: [],
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Recent activity, telemetry pending.')).toBeDefined());
    const summary = screen.getByLabelText('Project summary');
    expect(within(summary).getByText('Recent runs')).toBeDefined();
    expect(within(summary).getByText('6')).toBeDefined();
    expect(within(summary).getByText('Tasks done')).toBeDefined();
    expect(within(summary).getByText('3')).toBeDefined();
    expect(within(summary).getByText('12 total runs')).toBeDefined();
    expect(screen.getByText(/Summary shows 6 runs this week, 3 tasks done this week/)).toBeDefined();
    expect(screen.getByText(/Review board is primary because summary evidence shows 6 runs this week and 3 tasks done this week/)).toBeDefined();
    expect(screen.getByText('Activity exists; diagnostics are still catching up.')).toBeDefined();
    expect(screen.getByLabelText('Available dashboard evidence')).toBeDefined();
    expect(screen.getAllByRole('link', { name: 'Review board' }).some((link) => link.getAttribute('href') === '/projects/p1/board')).toBe(true);
    expect(screen.queryByRole('link', { name: 'Start task' })).toBeNull();
  });

  it('does not treat run creation trend as quality telemetry', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue({
      ...dto,
      summary: {
        runs_this_week: 4,
        runs_total: 9,
        active_runs: 0,
        active_agents: 0,
        tasks_done_this_week: 2,
      },
    });
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      throughput: [],
      leaderboard: [],
      invocationTrend: [
        { date: '2026-06-01', count: 2 },
        { date: '2026-06-02', count: 1 },
      ],
      modelUsage: [],
      responseDuration: [],
      timeToFirstToken: [],
      aiCreditUsageTrend: [],
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Recent activity, telemetry pending.')).toBeDefined());
    expect(screen.getByText(/Quality and model telemetry are not ready yet/)).toBeDefined();
    expect(screen.getByText('Activity exists; diagnostics are still catching up.')).toBeDefined();
    expect(screen.getByText(/Summary evidence is present, but this range has no model telemetry or agent leaderboard rows yet/)).toBeDefined();
    expect(screen.queryByText('Model telemetry present')).toBeNull();
    expect(screen.queryByText(/backed by quality evidence/)).toBeNull();
  });

  it('renders a role fallback when the dashboard payload omits role', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      ...metricsDto,
      leaderboard: [
        {
          agentName: 'Ada',
          runsThisWeek: 3,
          runsTotal: 10,
          successRate: 90,
          avgDurationMs: 65000,
          costAic: 0,
        },
      ],
    });

    renderPage();

    const table = await screen.findByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('—').length).toBeGreaterThan(0);
  });

  it('renders zero-run success rate as unknown', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      ...metricsDto,
      leaderboard: [
        {
          agentName: 'Ada',
          role: 'Frontend engineer',
          runsThisWeek: 0,
          runsTotal: 0,
          successRate: 0,
          avgDurationMs: null,
          costAic: 0,
        },
      ],
    });

    renderPage();

    const table = await screen.findByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('—').length).toBeGreaterThan(0);
  });

  it('surfaces a load error', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.getProjectDashboard).mockRejectedValue(new ApiError(404, 'Not found'));

    renderPage();

    await waitFor(() => expect(screen.getByText(/API error 404/)).toBeDefined());
  });

  it('uses the selected range for metrics queries', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(apiClient.getProjectDashboard).toHaveBeenCalled());
    await waitFor(() => expect(apiClient.getProjectMetrics).toHaveBeenCalled());

    const initialDashboardArgs = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
    const initialMetricsArgs = vi.mocked(apiClient.getProjectMetrics).mock.calls.at(-1)!;

    expect(initialDashboardArgs[0]).toBe('p1');
    expect(initialMetricsArgs[0]).toBe('p1');

    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '7d' } });

    await waitFor(() => expect(vi.mocked(apiClient.getProjectDashboard).mock.calls.length).toBeGreaterThan(1));
    await waitFor(() => expect(vi.mocked(apiClient.getProjectMetrics).mock.calls.length).toBeGreaterThan(1));

    const updatedDashboardArgs = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
    const updatedMetricsArgs = vi.mocked(apiClient.getProjectMetrics).mock.calls.at(-1)!;

    expect(updatedDashboardArgs[0]).toBe('p1');
    expect(updatedMetricsArgs[0]).toBe('p1');
    expect(updatedMetricsArgs[1]).not.toBe(initialMetricsArgs[1]);
  });
});
