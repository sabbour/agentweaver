import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { OverviewPage } from '../pages/OverviewPage';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { OverviewDto, Project, ProjectMetricsDto } from '../api/types';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getOverview: vi.fn(),
    listProjects: vi.fn(),
    getTeam: vi.fn(),
    getProjectRuns: vi.fn(),
    getBoard: vi.fn(),
    getProjectMetrics: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects` and
// `getProjectRuns` now resolve a `{ items, page, page_size, total_count, total_pages }` envelope.
function page<T>(items: T[], pageNumber = 1, pageSize = 100, totalCount = items.length) {
  return {
    items,
    page: pageNumber,
    page_size: pageSize,
    total_count: totalCount,
    total_pages: Math.max(1, Math.ceil(totalCount / Math.max(1, pageSize))),
  } as never;
}

const dto: OverviewDto = {
  generated_utc: new Date().toISOString(),
  at_a_glance: { in_flight: 2, queued_work: 5, done_today: 3, active_projects: 1, health: 'healthy' },
  live_sessions: [
    { project_id: 'p1', project_name: 'Demo', agent: 'Ada', status: 'in_progress', started_utc: new Date().toISOString(), last_activity_utc: new Date().toISOString() },
  ],
  active_workflow_runs: [
    { project_id: 'p1', project_name: 'Demo', trigger: 'interactive', status: 'in_progress', started_utc: new Date().toISOString() },
  ],
  active_projects: [
    { project_id: 'p1', project_name: 'Demo', active_count: 2, queued_count: 1, last_activity_utc: new Date().toISOString() },
  ],
  recent_activity: [
    { project_id: 'p1', project_name: 'Demo', label: 'Run completed', kind: 'completed', timestamp_utc: new Date().toISOString() },
    { project_id: 'p1', project_name: 'Demo', label: 'Run started', kind: 'in_progress', timestamp_utc: new Date().toISOString() },
  ],
};

const project: Project = {
  project_id: 'p1',
  name: 'Demo',
  origin: 'github',
  source_repository: 'https://github.com/microsoft/demo',
  working_directory: 'C:\\demo',
  default_branch: 'main',
  owner: 'sabbour',
  default_provider: 'github-copilot',
  default_model_github_copilot: null,
  default_model_microsoft_foundry: null,
  blueprint_generation_model: null,
  workflow_generation_model: null,
  outcome_spec_generation_model: null,
  available: true,
  state: 'active',
  created_at: new Date().toISOString(),
  updated_at: new Date().toISOString(),
};

const metrics: ProjectMetricsDto = {
  throughput: [],
  leaderboard: [{ agentName: 'Ada', runsThisWeek: 1, runsTotal: 2, successRate: 100, avgDurationMs: 1000, costAic: 1 }],
  invocationTrend: [{ date: '2026-07-05', count: 2 }],
  modelUsage: [{ model: 'gpt-5', invocationCount: 2, totalNanoAiu: 1_000_000_000 }],
  responseDuration: [{ label: 'gpt-5', p50Ms: 100, p95Ms: 200 }],
  timeToFirstToken: [{ label: 'gpt-5', p50Ms: 50, p95Ms: 80 }],
};
function renderPage() {
  return render(
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={['/overview']}>
        <OverviewPage />
      </MemoryRouter>
    </AzureFluentProvider>,
  );
}

beforeEach(() => vi.clearAllMocks());
afterEach(() => cleanup());

describe('OverviewPage', () => {
  it('renders redesigned overview sections with real data', async () => {
    vi.mocked(apiClient.getOverview).mockResolvedValue(dto);
    vi.mocked(apiClient.listProjects).mockResolvedValue(page([project]));
    vi.mocked(apiClient.getTeam).mockResolvedValue({ project_name: 'Demo', universe: 'main', members: [{ name: 'Ada', role_title: 'Engineer', charter_path: '', status: 'active', default_model: 'gpt-5', is_named: true, is_built_in: false }], layout: 'canonical', migration_available: false });
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue(page([{ workflow_run_id: 'wr1', execution_id: 'r1', task: 'Run completed', status: 'completed', started_at: new Date().toISOString() }]));
    vi.mocked(apiClient.getBoard).mockResolvedValue({ project_id: 'p1', workflow_stages_available: true, columns: [{ id: 'backlog', kind: 'intake', label: 'Backlog', cards: [{ kind: 'task', task_id: 't1', title: 'Issue', description: null, state: 'backlog', order_key: '1', captured_by: 'Ada', created_at: new Date().toISOString() }] }] });
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue(metrics);

    renderPage();

    await waitFor(() => expect(screen.getByText('Recent projects')).toBeDefined());
    expect(screen.getByText('AI usage & performance')).toBeDefined();
    expect(screen.getByText('Activity feed')).toBeDefined();
    expect(screen.getByText('Needs attention')).toBeDefined();
    expect(screen.getAllByText('Demo').length).toBeGreaterThan(0);
    expect(screen.getByText('microsoft/demo')).toBeDefined();
    expect(screen.getByText('Run completed', { exact: false })).toBeDefined();
    expect(screen.getByText('Token consumption by model')).toBeDefined();
    expect(screen.getAllByText('gpt-5').length).toBeGreaterThan(0);
    expect(screen.getByText(/Next refresh in/)).toBeDefined();
  });

  it('links attention failures to valid orchestration routes and avoids bare diagnostics', async () => {
    vi.mocked(apiClient.getOverview).mockResolvedValue({
      ...dto,
      at_a_glance: { ...dto.at_a_glance, health: 'degraded' },
      recent_activity: [
        { project_id: 'p1', project_name: 'Demo', label: 'Run failed', kind: 'failed', timestamp_utc: new Date().toISOString() },
      ],
    });
    vi.mocked(apiClient.listProjects).mockResolvedValue(page([project]));
    vi.mocked(apiClient.getTeam).mockResolvedValue({ project_name: 'Demo', universe: 'main', members: [], layout: 'canonical', migration_available: false });
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue(page([]));
    vi.mocked(apiClient.getBoard).mockResolvedValue({ project_id: 'p1', workflow_stages_available: true, columns: [] });
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue(metrics);

    renderPage();

    await waitFor(() => {
      const hrefs = Array.from(document.querySelectorAll<HTMLElement>('[href]'))
        .map((el) => el.getAttribute('href'));
      expect(hrefs).toContain('/projects/p1/orchestrations');
      expect(hrefs).toContain('/projects');
      expect(hrefs).not.toContain('/diagnostics');
    });
  });

  it('pages through projects so recent selection still considers items beyond the first 100', async () => {
    const olderProjects = Array.from({ length: 100 }, (_, index) => ({
      ...project,
      project_id: `p${index + 1}`,
      name: `Project ${index + 1}`,
      updated_at: '2026-01-01T00:00:00Z',
    }));
    const newestProject = {
      ...project,
      project_id: 'p101',
      name: 'Project 101',
      updated_at: '2026-07-10T00:00:00Z',
    };

    vi.mocked(apiClient.getOverview).mockResolvedValue({
      ...dto,
      active_projects: [],
      recent_activity: [],
    });
    vi.mocked(apiClient.listProjects).mockImplementation(async (options) => {
      const pageNumber = options?.page ?? 1;
      return page(
        pageNumber === 1 ? olderProjects : [newestProject],
        pageNumber,
        options?.pageSize ?? 100,
        101,
      );
    });
    vi.mocked(apiClient.getTeam).mockResolvedValue({ project_name: 'Demo', universe: 'main', members: [], layout: 'canonical', migration_available: false });
    vi.mocked(apiClient.getProjectRuns).mockResolvedValue(page([]));
    vi.mocked(apiClient.getBoard).mockResolvedValue({ project_id: 'p1', workflow_stages_available: true, columns: [] });
    vi.mocked(apiClient.getProjectMetrics).mockResolvedValue(metrics);

    renderPage();

    await waitFor(() => expect(screen.getAllByText('Project 101').length).toBeGreaterThan(0));
    expect(
      vi.mocked(apiClient.listProjects).mock.calls.some(([options]) => options?.page === 2),
    ).toBe(true);
  });
});
