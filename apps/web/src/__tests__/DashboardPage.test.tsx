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

    await waitFor(() => expect(screen.getByText('Active agents')).toBeDefined());
    expect(screen.getByText('Tasks done (7d)')).toBeDefined();
    expect(screen.getByText('Throughput')).toBeDefined();
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
