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

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ apiCall: mocks.apiCall }),
}));

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

function project(projectId: string, name: string, updatedAt: string, state: ProjectSummary['state'] = 'active'): ProjectSummary {
  return { projectId, name, state, revision: 1, configurationRevision: 1, createdAt: updatedAt, updatedAt };
}

function renderPage(initialEntry = '/overview') {
  return render(<MemoryRouter initialEntries={[initialEntry]}><OverviewPage /></MemoryRouter>);
}

describe('OverviewPage metrics', () => {
  beforeEach(() => {
    mocks.useProjectMetrics.mockClear();
    mocks.metricsReload.mockClear();
    // The page loads projects through apiCall; return the test list.
    mocks.apiCall.mockImplementation(async () => mocks.projects);
    mocks.projects = [
      project('p1', 'Alpha', '2026-01-01T00:00:00Z'),
      project('p2', 'Beta', '2026-02-01T00:00:00Z'),
      project('p3', 'Old', '2026-03-01T00:00:00Z', 'archived'),
    ];
  });

  it('shows metrics for the most recently updated active project and says they are per project', async () => {
    renderPage();
    await waitFor(() => expect(screen.getByLabelText('Project metric summary')).toBeTruthy());
    expect(mocks.useProjectMetrics).toHaveBeenLastCalledWith('p2', '30d');
    expect(screen.getByText(/Metrics are per project/)).toBeTruthy();
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
