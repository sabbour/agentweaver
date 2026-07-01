import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup, within, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ProjectDashboardDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectDashboard: vi.fn(),
    getProjectUsage: vi.fn(),
    getProjectRuns: vi.fn(),
    getRunUsage: vi.fn(),
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
  throughput: [
    { date: '2026-06-01', created: 2, done: 1 },
    { date: '2026-06-02', created: 3, done: 2 },
  ],
  agent_leaderboard: [
    {
      agent: 'Ada',
      role_title: 'Frontend engineer',
      runs_this_week: 3,
      runs_total: 10,
      success_rate: 0.9,
      successful_runs: 9,
      terminal_runs: 10,
      avg_duration_ms: 65000,
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
  vi.mocked(apiClient.getProjectUsage).mockResolvedValue({
    input_tokens: 0,
    output_tokens: 0,
    total_tokens: 0,
    total_nano_aiu: 0,
    by_model: [],
  });
  vi.mocked(apiClient.getProjectRuns).mockResolvedValue([]);
  vi.mocked(apiClient.getRunUsage).mockResolvedValue({
    input_tokens: 0,
    output_tokens: 0,
    total_tokens: 0,
    total_nano_aiu: 0,
    by_model: [],
  });
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
    expect(screen.getByText('Throughput (last 30 days)')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.getByText('Ada')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Ada' }).getAttribute('href'))
      .toBe('/projects/p1/flow?agent=Ada');
    expect(screen.getByText('Frontend engineer')).toBeDefined();
    expect(screen.getByText('Success rate = successful terminal runs / terminal runs (queued, waiting-review, and in-progress excluded).')).toBeDefined();
    expect(screen.getByText('90%')).toBeDefined();
    expect(screen.getByText('9/10')).toBeDefined();

    const table = screen.getByRole('table', { name: 'Agent leaderboard' });
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual(['Agent', 'Role', 'Runs this week', 'Runs total', 'Success rate', 'Avg duration', 'Cost']);
  });

  it('renders a role fallback when the dashboard payload omits role', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue({
      ...dto,
      agent_leaderboard: [
        {
          agent: 'Ada',
          runs_this_week: 3,
          runs_total: 10,
          success_rate: 0.9,
          successful_runs: 9,
          terminal_runs: 10,
          avg_duration_ms: 65000,
        },
      ],
    });

    renderPage();

    const table = await screen.findByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('—').length).toBeGreaterThan(0);
  });

  it('renders zero-terminal success rate as unknown with count basis', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue({
      ...dto,
      agent_leaderboard: [
        {
          agent: 'Ada',
          role_title: 'Frontend engineer',
          runs_this_week: 2,
          runs_total: 2,
          success_rate: 0,
          successful_runs: 0,
          terminal_runs: 0,
          avg_duration_ms: null,
        },
      ],
    });

    renderPage();

    const table = await screen.findByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('—').length).toBeGreaterThan(0);
    expect(within(table).getByText('0/0')).toBeDefined();
  });

  it('surfaces a load error', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.getProjectDashboard).mockRejectedValue(new ApiError(404, 'Not found'));

    renderPage();

    await waitFor(() => expect(screen.getByText(/API error 404/)).toBeDefined());
  });

  it('uses the same range for dashboard and usage queries', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(apiClient.getProjectDashboard).toHaveBeenCalled());
    await waitFor(() => expect(apiClient.getProjectUsage).toHaveBeenCalled());

    const initialDashboardArgs = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
    const initialUsageArgs = vi.mocked(apiClient.getProjectUsage).mock.calls.at(-1)!;

    expect(initialDashboardArgs[0]).toBe('p1');
    expect(initialUsageArgs[0]).toBe('p1');
    expect(initialDashboardArgs[1]).toBe(initialUsageArgs[1]);
    expect(initialDashboardArgs[2]).toBe(initialUsageArgs[2]);

    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '7d' } });

    await waitFor(() => expect(vi.mocked(apiClient.getProjectDashboard).mock.calls.length).toBeGreaterThan(1));
    await waitFor(() => expect(vi.mocked(apiClient.getProjectUsage).mock.calls.length).toBeGreaterThan(1));

    const updatedDashboardArgs = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
    const updatedUsageArgs = vi.mocked(apiClient.getProjectUsage).mock.calls.at(-1)!;

    expect(updatedDashboardArgs[0]).toBe('p1');
    expect(updatedUsageArgs[0]).toBe('p1');
    expect(updatedDashboardArgs[1]).toBe(updatedUsageArgs[1]);
    expect(updatedDashboardArgs[2]).toBe(updatedUsageArgs[2]);
    expect(updatedDashboardArgs[1]).not.toBe(initialDashboardArgs[1]);
  });
});
