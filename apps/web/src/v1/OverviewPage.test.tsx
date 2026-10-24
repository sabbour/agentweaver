import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary } from './contracts';
import { OverviewPage } from './OverviewPage';

const mocks = vi.hoisted(() => ({
  projects: [] as unknown[],
  useProjectMetrics: vi.fn(),
  metricsReload: vi.fn(),
  apiCall: vi.fn(),
}));
const gatewayMocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  getProjectConfiguration: vi.fn(),
  getProjectMetrics: vi.fn(),
  readProjectBacklog: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: mocks.apiCall }),
}));

vi.mock('./api', () => ({ gatewayClient: gatewayMocks }));

vi.mock('./RunAttentionContext', () => ({
  useSharedRunAttention: () => ({ items: [], reload: vi.fn() }),
}));

vi.mock('./AttentionList', () => ({ AttentionList: () => <p>No attention items</p> }));
// Metrics tests isolate planning hooks; their owner reads are exercised in the planning suites.
vi.mock('./chiefOfStaff', async (importOriginal) => ({
  ...await importOriginal<typeof import('./chiefOfStaff')>(),
  useChiefOfStaffConversations: () => ({ items: [], loading: false, error: null, reload: vi.fn() }),
  useChiefOfStaffPriorities: () => ({ list: { items: [], orderRevision: 1 }, loading: false, error: null, reload: vi.fn() }),
}));

vi.mock('./runListData', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runListData')>(),
  useCrossProjectRunList: () => ({
    items: [], loading: false, error: null, hasMore: false, loadingMore: false, loadMore: vi.fn(), reload: vi.fn(),
    projectNames: new Map(), unreadableProjects: [],
  }),
}));

vi.mock('./projectMetrics', async (importOriginal) => ({
  ...await importOriginal<typeof import('./projectMetrics')>(),
  useProjectMetrics: (projectId: string | null, range: string) => {
    mocks.useProjectMetrics(projectId, range);
    return {
      metrics: projectId ? { projectId, from: 'a', to: 'b', activeRuns: 4, blockedRuns: 2, runsInRange: 7, runsTotal: 20, completedRuns: 0, succeededRuns: 0, series: [] } : null,
      usage: null,
      metricsError: null,
      usageError: null,
      loading: false,
      loadedAt: null,
      reload: mocks.metricsReload,
    };
  },
}));

vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return {
    ...actual,
    PageHeading: ({ title, actions }: { title: string; actions?: React.ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
  };
});

function project(
  projectId: string,
  name: string,
  updatedAt: string,
  state: ProjectSummary['state'] = 'active',
  effectiveRoles: ProjectSummary['effectiveRoles'] = [],
): ProjectSummary {
  return { projectId, name, state, revision: 1, configurationRevision: 1, createdAt: updatedAt, updatedAt, effectiveRoles };
}

function renderPage(initialEntry = '/overview') {
  return render(<MemoryRouter initialEntries={[initialEntry]}><OverviewPage /></MemoryRouter>);
}

describe('OverviewPage metrics', () => {
  beforeEach(() => {
    mocks.useProjectMetrics.mockClear();
    mocks.metricsReload.mockClear();
    mocks.apiCall.mockImplementation((operation: (token: string, tenant: null) => Promise<unknown>) => operation('token', null));
    gatewayMocks.listProjects.mockImplementation(async () => mocks.projects);
    gatewayMocks.getProjectConfiguration.mockImplementation(async (_token: string, projectId: string) => ({
      projectId,
      revision: 1,
      configuration: { agentCharters: [], sourceControl: { repository: null } },
    }));
    gatewayMocks.getProjectMetrics.mockImplementation(async (_token: string, projectId: string) => ({
      projectId,
      from: '2026-01-01T00:00:00Z',
      to: '2026-01-31T00:00:00Z',
      activeRuns: projectId === 'p1' ? 0 : 1,
      blockedRuns: 0,
      runsInRange: 3,
      runsTotal: 8,
      completedRuns: 2,
      succeededRuns: 2,
      series: [],
    }));
    gatewayMocks.readProjectBacklog.mockImplementation(async (_token: string, projectId: string) => ({
      projectId,
      revision: 1,
      tasks: projectId === 'p1'
        ? [
          {
            reference: { projectId, taskId: 'task-1' }, revision: 1, state: 'ready', isArchived: false,
            isAutomationInvocationPending: false, title: 'Review issue', lifecycle: 'open',
            repositoryIssue: { providerId: 'github', repository: { owner: 'octo', name: 'agentweaver' }, issueNumber: 1 },
          },
          {
            reference: { projectId, taskId: 'task-2' }, revision: 1, state: 'ready', isArchived: false,
            isAutomationInvocationPending: false, title: 'Unlinked backlog task', lifecycle: 'open',
          },
          {
            reference: { projectId, taskId: 'task-3' }, revision: 1, state: 'ready', isArchived: true,
            isAutomationInvocationPending: false, title: 'Archived issue', lifecycle: 'open',
            repositoryIssue: { providerId: 'github', repository: { owner: 'octo', name: 'agentweaver' }, issueNumber: 2 },
          },
        ]
        : [],
      dependencies: [],
    }));
    mocks.projects = [
      project('p1', 'Alpha', '2026-01-01T00:00:00Z', 'active', [
        { resourceType: 'project', resourceId: 'p1', role: 'owner' },
      ]),
      project('p2', 'Beta', '2026-02-01T00:00:00Z'),
      project('p3', 'Old', '2026-03-01T00:00:00Z', 'archived'),
    ];
  });

  it('shows the owner-confirmed role on a recent project tile', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText('Owner')).toBeTruthy());
    expect(screen.getByLabelText('Beta project counts').textContent).toContain('Runs 8');
    expect(screen.getByLabelText('Alpha project counts').textContent).toContain('Issues 1');
    expect(screen.getByText('Queued')).toBeTruthy();
  });

  it('restores the 0.x Overview section order and reports missing aggregate routes', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByLabelText('Project metric summary')).toBeTruthy());
    const sections = ['Chief of Staff', 'Operations', 'Recent projects', 'AI usage & performance', 'Activity feed', 'Needs attention'];
    const positions = sections.map((section) => document.body.textContent!.indexOf(section));
    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
    expect(screen.getAllByText(/GET \/api\/v1\/overview/).length).toBeGreaterThanOrEqual(3);
    expect(screen.getByText(/recentActivity/)).toBeTruthy();
  });

  it('shows metrics for the most recently updated active project and says they are per project', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByLabelText('Project metric summary')).toBeTruthy());
    expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith('p2', '30d');
    expect(screen.getByText(/GET \/api\/v1\/overview route for aggregate metrics or usage/)).toBeTruthy();
    const summary = screen.getByLabelText('Project metric summary');
    expect(summary.textContent).toContain('Active runs4');
    expect(summary.textContent).toContain('Needs input2');
    expect(summary.textContent).toContain('Success—No completed runs in this range');
    expect(screen.getByRole('link', { name: 'Open project dashboard' }).getAttribute('href')).toBe('/projects/p2');
    expect(screen.queryByText(/metrics API/)).toBeNull();
  });

  it('switches the project and the range', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByLabelText('Metrics project')).toBeTruthy());
    const options = Array.from((screen.getByLabelText('Metrics project') as HTMLSelectElement).options).map((option) => option.textContent);
    // Archived projects are not offered.
    expect(options).toEqual(['Alpha', 'Beta']);
    fireEvent.change(screen.getByLabelText('Metrics project'), { target: { value: 'p1' } });
    expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith('p1', '30d');
    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '7d' } });
    expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith('p1', '7d');
  });

  it('reloads metrics on Refresh', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByLabelText('Project metric summary')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(mocks.metricsReload).toHaveBeenCalled();
  });

  it('explains that metrics need an active project', async () => {
    mocks.projects = [];
    renderPage();
    await waitFor(() => expect(screen.getByText('Metrics appear when you have an active project.')).toBeTruthy());
    expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith(null, '30d');
  });

  it('keeps recent projects visible when a detail read fails', async () => {
    gatewayMocks.readProjectBacklog.mockRejectedValueOnce(new Error('Backlog read denied'));
    renderPage();
    await waitFor(() => expect(screen.getByRole('status').textContent).toContain('Some project counts could not load'));
    expect(screen.getByRole('heading', { name: 'Recent projects' })).toBeTruthy();
    expect(screen.getByLabelText('Beta project counts').textContent).toContain('Issues —');
  });

  it('groups Priorities, New chat, and My conversations under Chief of Staff', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Chief of Staff' })).toBeTruthy());
    const group = screen.getByRole('heading', { name: 'Chief of Staff' }).closest('section');
    expect(group).toBeTruthy();
    for (const heading of ['Priorities', 'New chat', 'My conversations']) {
      expect(group?.contains(screen.getByRole('heading', { name: heading }))).toBe(true);
    }
  });

  it('scrolls to the Chief of Staff section after its project-gated page content loads', async () => {
    const descriptor = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
    const scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, 'scrollIntoView', { configurable: true, value: scrollIntoView });
    try {
      renderPage('/overview#chief-of-staff');
      await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({ block: 'start' }));
    } finally {
      if (descriptor) Object.defineProperty(Element.prototype, 'scrollIntoView', descriptor);
      else Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
    }
  });
});
