import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { DashboardPage } from '../pages/DashboardPage';
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { ProjectDashboardDto, ProjectMetricsDto } from '../api/types';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProjectDashboard: vi.fn(),
    getProjectMetrics: vi.fn(),
  },
}));

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
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/projects/p1']}>
        <Routes>
          <Route path="/projects/:projectId" element={<DashboardPage />} />
        </Routes>
      </MemoryRouter>
    </AzureFluentProvider>,
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

    await waitFor(() => expect(screen.getByText('Active runs')).toBeDefined());
    const summary = screen.getByLabelText('Project summary');
    expect(within(summary).getByText('Runs this week')).toBeDefined();
    expect(within(summary).getByText('Completed tasks')).toBeDefined();
    expect(within(summary).getByText('Run success')).toBeDefined();
    expect(within(summary).getByText('20 total runs')).toBeDefined();
    expect(screen.getByText('Runs over time')).toBeDefined();
    expect(screen.getByText('Runs created by date')).toBeDefined();
    expect(screen.getByText('Daily runs created during the last 30 days.')).toBeDefined();
    expect(screen.getByText('Model and agent metrics')).toBeDefined();
    expect(screen.getByText('No model data')).toBeDefined();
    expect(screen.getByText('Agent leaderboard')).toBeDefined();
    expect(screen.queryByText('Decision guide')).toBeNull();
    expect(screen.getByText('Ada')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Ada' }).getAttribute('href'))
      .toBe('/projects/p1/flow?agent=Ada');
    expect(screen.getByText('Frontend engineer')).toBeDefined();
    const table = screen.getByRole('table', { name: 'Agent leaderboard' });
    expect(within(table).getAllByText('90%').length).toBeGreaterThan(0);
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual(['Agent', 'Role', 'Runs this week', 'Total runs', 'Success rate', 'Average duration', 'Cost']);
  });

  it('formats latency percentile cells in readable units without changing their numeric sort values', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue({
      ...metricsDto,
      responseDuration: [
        { label: 'first', p50Ms: 130450, p95Ms: 4537 },
        { label: 'second', p50Ms: 4537, p95Ms: 130450 },
        { label: 'just-below-minute', p50Ms: 59949, p95Ms: 59950 },
        { label: 'one-minute', p50Ms: 60000, p95Ms: null },
      ],
      timeToFirstToken: [
        { label: 'first-token', p50Ms: 4537, p95Ms: 130450 },
      ],
    });

    renderPage();

    const latencyTables = await screen.findAllByRole('table', { name: 'Latency percentiles' });
    const responseTable = latencyTables[0]!;
    const firstTokenTable = latencyTables[1]!;
    const firstResponseRow = within(responseTable).getByText('first').closest('tr')!;
    const secondResponseRow = within(responseTable).getByText('second').closest('tr')!;
    const justBelowMinuteRow = within(responseTable).getByText('just-below-minute').closest('tr')!;
    const oneMinuteRow = within(responseTable).getByText('one-minute').closest('tr')!;
    const firstTokenRow = within(firstTokenTable).getByText('first-token').closest('tr')!;
    expect(within(firstResponseRow).getByText('2m 10s')).toBeDefined();
    expect(within(firstResponseRow).getByText('4.5s')).toBeDefined();
    expect(within(secondResponseRow).getByText('2m 10s')).toBeDefined();
    expect(within(secondResponseRow).getByText('4.5s')).toBeDefined();
    expect(within(justBelowMinuteRow).getByText('59.9s')).toBeDefined();
    expect(within(justBelowMinuteRow).getByText('1m 0s')).toBeDefined();
    expect(within(oneMinuteRow).getByText('1m 0s')).toBeDefined();
    expect(within(firstTokenRow).getByText('2m 10s')).toBeDefined();
    expect(within(firstTokenRow).getByText('4.5s')).toBeDefined();

    fireEvent.click(within(responseTable).getByRole('button', { name: 'P50' }));
    expect(within(responseTable).getAllByRole('row')[1]!.textContent).toContain('second');
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

    await waitFor(() => expect(screen.getByText('Run activity is available.')).toBeDefined());
    const summary = screen.getByLabelText('Project summary');
    expect(within(summary).getByText('Runs this week')).toBeDefined();
    expect(within(summary).getByText('6')).toBeDefined();
    expect(within(summary).getByText('Completed tasks')).toBeDefined();
    expect(within(summary).getByText('3')).toBeDefined();
    expect(within(summary).getByText('12 total runs')).toBeDefined();
    expect(screen.getByText(/6 runs started and 3 tasks completed this week/)).toBeDefined();
    expect(screen.getByText('No model or agent metrics for this range.')).toBeDefined();
    expect(screen.getByLabelText('Available dashboard evidence')).toBeDefined();
    expect(screen.getAllByRole('link', { name: 'Open Board' }).some((link) => link.getAttribute('href') === '/projects/p1/board')).toBe(true);
    expect(screen.queryByRole('link', { name: 'Start task' })).toBeNull();
    expect(screen.queryByText('Decision guide')).toBeNull();
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

    await waitFor(() => expect(screen.getByText('Run activity is available.')).toBeDefined());
    expect(screen.getByText('No model or agent metrics for this range.')).toBeDefined();
    expect(screen.getByText(/Runs have not reported model usage or agent scores for this range/)).toBeDefined();
    expect(screen.queryByText('Model data available')).toBeNull();
    expect(screen.queryByText('Decision guide')).toBeNull();
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

  // #208 point 5 regression coverage: a scheduled refresh must carry an AbortSignal, and starting a
  // new refresh (poll tick) must abort the previous request's signal instead of letting both
  // in-flight requests race to update state.
  it('passes an AbortSignal to getProjectDashboard/getProjectMetrics and aborts the previous poll on the next tick', async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

      renderPage();

      await vi.waitFor(() => expect(apiClient.getProjectMetrics).toHaveBeenCalled());

      const firstDashboardCall = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
      const firstMetricsCall = vi.mocked(apiClient.getProjectMetrics).mock.calls.at(-1)!;
      const firstDashboardOptions = firstDashboardCall[1] as { includeMetrics?: boolean; signal?: AbortSignal } | undefined;
      const firstSignal = firstMetricsCall[3] as AbortSignal | undefined;

      expect(firstDashboardOptions?.signal).toBeInstanceOf(AbortSignal);
      expect(firstSignal).toBeInstanceOf(AbortSignal);
      expect(firstSignal!.aborted).toBe(false);

      // Advance past the 30s refresh interval to trigger the next scheduled poll.
      await vi.advanceTimersByTimeAsync(30000);

      expect(firstSignal!.aborted).toBe(true);

      const secondMetricsCall = vi.mocked(apiClient.getProjectMetrics).mock.calls.at(-1)!;
      const secondSignal = secondMetricsCall[3] as AbortSignal | undefined;
      expect(secondSignal).toBeInstanceOf(AbortSignal);
      expect(secondSignal).not.toBe(firstSignal);
      expect(secondSignal!.aborted).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  // #208 point 4 regression coverage: DashboardPage never reads Throughput/AgentLeaderboard off the
  // `/dashboard` response (it uses the separately-fetched full metrics DTO instead), so it should opt
  // out of the dashboard endpoint's own internal metrics fan-out.
  it('requests the dashboard endpoint with includeMetrics disabled', async () => {
    vi.mocked(apiClient.getProjectDashboard).mockResolvedValue(dto);

    renderPage();

    await waitFor(() => expect(apiClient.getProjectDashboard).toHaveBeenCalled());

    const dashboardArgs = vi.mocked(apiClient.getProjectDashboard).mock.calls.at(-1)!;
    expect(dashboardArgs[1]).toMatchObject({ includeMetrics: false });
  });
});
