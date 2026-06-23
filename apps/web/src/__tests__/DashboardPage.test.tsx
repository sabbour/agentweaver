import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ProjectDashboardDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectDashboard: vi.fn(),
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
    { agent: 'Ada', runs_this_week: 3, runs_total: 10, success_rate: 0.9, avg_duration_ms: 65000 },
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

beforeEach(() => vi.clearAllMocks());
afterEach(() => cleanup());

describe('DashboardPage', () => {
  it('renders summary cards, throughput, and the agent leaderboard', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(screen.getByText('Active agents')).toBeDefined());
    expect(screen.getByText('Tasks done (7d)')).toBeDefined();
    expect(screen.getByText('Throughput (last 30 days)')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.getByText('Ada')).toBeDefined();
    expect(screen.getByText('90%')).toBeDefined();
  });

  it('surfaces a load error', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.getProjectDashboard).mockRejectedValue(new ApiError(404, 'Not found'));

    renderPage();

    await waitFor(() => expect(screen.getByText(/API error 404/)).toBeDefined());
  });
});
