import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectSummary } from './contracts';
import { OverviewPage } from './OverviewPage';

const mocks = vi.hoisted(() => ({
  projects: [] as unknown[],
  metricsLoading: false,
  metricsReloadPromise: undefined as Promise<void> | undefined,
  deferMetricsReload: false,
  resolveMetricsReload: undefined as (() => void) | undefined,
  useProjectMetrics: vi.fn(),
  metricsReload: vi.fn(),
  attentionReload: vi.fn(async () => undefined),
  conversationsReload: vi.fn(async () => undefined),
  prioritiesReload: vi.fn(async (): Promise<void> => undefined),
  runsReload: vi.fn(),
  apiCall: vi.fn(),
}));
const gatewayMocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  getProjectConfiguration: vi.fn(),
  getProjectMetrics: vi.fn(),
  getProjectUsage: vi.fn(),
  readProjectBacklog: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: mocks.apiCall }),
}));

vi.mock('./api', () => ({ gatewayClient: gatewayMocks }));

vi.mock('./RunAttentionContext', () => ({
  useSharedRunAttention: () => ({ items: [], loading: false, reload: mocks.attentionReload }),
}));

vi.mock('./AttentionList', () => ({ AttentionList: () => <p>No attention items</p> }));
// Metrics tests isolate planning hooks; their owner reads are exercised in the planning suites.
vi.mock('./chiefOfStaff', async (importOriginal) => ({
  ...await importOriginal<typeof import('./chiefOfStaff')>(),
  useChiefOfStaffConversations: () => ({ items: [], loading: false, error: null, reload: mocks.conversationsReload }),
  useChiefOfStaffPriorities: () => ({
    list: { items: [], orderRevision: 1 }, loading: false, error: null, reload: mocks.prioritiesReload,
  }),
}));

vi.mock('./runListData', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runListData')>(),
  useCrossProjectRunList: () => ({
    items: [], loading: false, error: null, hasMore: false, loadingMore: false, loadMore: vi.fn(), reload: mocks.runsReload,
    projectNames: new Map(), unreadableProjects: [],
  }),
}));

vi.mock('./projectMetrics', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./projectMetrics')>();
  return {
    ...actual,
    useProjectMetrics: (projectId: string | null, range: string) => {
      mocks.useProjectMetrics(projectId, range);
      if (!projectId) {
        mocks.metricsLoading = false;
        mocks.resolveMetricsReload?.();
      }
      return {
        metrics: projectId ? { projectId, from: 'a', to: 'b', activeRuns: 4, blockedRuns: 2, runsInRange: 7, runsTotal: 20, completedRuns: 0, succeededRuns: 0, series: [] } : null,
        usage: null,
        metricsError: null,
        usageError: null,
        loading: projectId != null && mocks.metricsLoading,
        loadedAt: null,
        reload: () => {
          if (!mocks.deferMetricsReload || !projectId) return mocks.metricsReload();
          mocks.metricsLoading = true;
          mocks.metricsReloadPromise = new Promise<void>((resolve) => {
            mocks.resolveMetricsReload = resolve;
          });
          return mocks.metricsReloadPromise;
        },
      };
    },
  };
});

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
    mocks.metricsLoading = false;
    mocks.metricsReloadPromise = undefined;
    mocks.deferMetricsReload = false;
    mocks.resolveMetricsReload = undefined;
    mocks.useProjectMetrics.mockClear();
    mocks.metricsReload.mockClear();
    mocks.attentionReload.mockClear();
    mocks.conversationsReload.mockClear();
    mocks.prioritiesReload.mockReset().mockResolvedValue(undefined);
    mocks.runsReload.mockClear();
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
    gatewayMocks.getProjectUsage.mockResolvedValue({
      projectId: 'p1',
      from: '2026-01-01T00:00:00Z',
      to: '2026-01-31T00:00:00Z',
      usageByModel: [],
      usageByAgent: [],
    });
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

  it('refreshes owner data every ten seconds without overlap and stops after unmount', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    gatewayMocks.listProjects.mockClear();
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    const view = renderPage();
    try {
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled,
      ).toBe(false));
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(1);
      gatewayMocks.listProjects.mockClear();

      const intervalIndex = setIntervalSpy.mock.calls.findIndex(([, delay]) => delay === 10_000);
      expect(intervalIndex).toBeGreaterThanOrEqual(0);
      const intervalId = setIntervalSpy.mock.results[intervalIndex]?.value;

      let finishPriorityReload: (() => void) | undefined;
      mocks.prioritiesReload.mockImplementationOnce(() => new Promise<void>((resolve) => {
        finishPriorityReload = resolve;
      }));
      await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(1);
      expect(mocks.attentionReload).toHaveBeenCalledTimes(1);
      expect(mocks.metricsReload).toHaveBeenCalledTimes(1);
      expect(mocks.conversationsReload).toHaveBeenCalledTimes(1);
      expect(mocks.prioritiesReload).toHaveBeenCalledTimes(1);
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled,
      ).toBe(true));

      await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(1);

      await act(async () => { finishPriorityReload?.(); });
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled,
      ).toBe(false));

      await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(2);
      view.unmount();
      expect(clearIntervalSpy).toHaveBeenCalledWith(intervalId);
      await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(2);
    } finally {
      view.unmount();
      setIntervalSpy.mockRestore();
      clearIntervalSpy.mockRestore();
      vi.useRealTimers();
    }
  });

  it('resumes scheduled refreshes when the last active project is archived during a metrics refresh', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    mocks.projects = [project('p1', 'Alpha', '2026-01-01T00:00:00Z')];
    const activeProject = mocks.projects[0] as ProjectSummary;
    gatewayMocks.listProjects.mockImplementation(async () => [activeProject]);
    const view = renderPage();
    try {
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled,
      ).toBe(false));
      mocks.deferMetricsReload = true;
      const archivedProject = { ...activeProject, state: 'archived' };
      gatewayMocks.listProjects.mockImplementationOnce(async () => [archivedProject]);
      gatewayMocks.listProjects.mockImplementation(async () => [archivedProject]);
      fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

      await waitFor(() => expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith(null, '30d'));
      await waitFor(() => expect(mocks.metricsLoading).toBe(false));
      await waitFor(() => expect(mocks.resolveMetricsReload).toBeDefined());
      await act(async () => {
        await mocks.metricsReloadPromise;
      });
      await waitFor(() => expect(mocks.metricsLoading).toBe(false));
      await waitFor(() => expect(
        (screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled,
      ).toBe(false));

      const projectListReads = gatewayMocks.listProjects.mock.calls.length;
      const intervalCallback = setIntervalSpy.mock.calls.find(([, delay]) => delay === 10_000)?.[0];
      expect(typeof intervalCallback).toBe('function');
      await act(async () => {
        if (typeof intervalCallback === 'function') intervalCallback();
      });
      expect(gatewayMocks.listProjects).toHaveBeenCalledTimes(projectListReads + 1);
    } finally {
      view.unmount();
      setIntervalSpy.mockRestore();
    }
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
