import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectConfiguration } from './contracts';
import { DashboardPage } from './DashboardPage';
import type { RunListItem } from './runListData';

interface LoadedStub<T> { data: T | null; loading: boolean; error: unknown; loadedAt: Date | null; reload: () => void; replace: () => void }

const mocks = vi.hoisted(() => ({
  project: null as unknown,
  configuration: null as unknown,
  runs: [] as unknown[],
  useProjectRunList: vi.fn(),
  reloadRuns: vi.fn(),
  useProjectMetrics: vi.fn(),
  metricsData: null as unknown,
}));

vi.mock('./projectMetrics', async (importOriginal) => ({
  ...await importOriginal<typeof import('./projectMetrics')>(),
  useProjectMetrics: (projectId: string | null, range: string) => {
    mocks.useProjectMetrics(projectId, range);
    return mocks.metricsData;
  },
}));

vi.mock('./projectData', () => ({
  useProjectSummary: () => mocks.project,
  useProjectConfiguration: () => mocks.configuration,
}));

vi.mock('./runListData', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runListData')>(),
  useProjectRunList: (projectId: string, options?: { states?: string[] }) => {
    mocks.useProjectRunList(projectId, options);
    return { items: mocks.runs, loading: false, error: null, hasMore: false, loadingMore: false, loadMore: vi.fn(), reload: mocks.reloadRuns };
  },
}));

vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return {
    ...actual,
    PageHeading: ({ title, actions }: { title: string; actions?: React.ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
  };
});

/** Builds a fake load result in the same shape the project data hooks return. */
function loaded<T>(data: T | null, overrides: Partial<LoadedStub<T>> = {}): LoadedStub<T> {
  return { data, loading: false, error: null, loadedAt: null, reload: vi.fn(), replace: vi.fn(), ...overrides };
}

/** A fully set-up configuration: model, repository, one cast agent, one enabled skill. */
function config(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return {
    modelSelection: { reference: 'gpt-test' } as ProjectConfiguration['modelSelection'],
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: [{ agentId: 'a1', name: 'Ada', role: 'Lead', charter: '' }],
    casting: [{ agentId: 'a1', role: 'Lead', order: 1 }],
    blueprintWorkflowReferences: [],
    skills: [{ skillId: 's1', enabled: true, order: 0 }, { skillId: 's2', enabled: false, order: 1 }],
    runLimits: {},
    sourceControl: { repository: { owner: 'octo', name: 'repo' } } as ProjectConfiguration['sourceControl'],
    ...overrides,
  };
}

function run(runId: string, executionState: string): RunListItem {
  return { projectId: 'p1', runId, label: `Task ${runId}`, executionState, time: { label: 'Started', value: '2026-01-01T00:00:00Z' } };
}

function metricsData() {
  return {
    metrics: { projectId: 'p1', from: 'a', to: 'b', activeRuns: 2, blockedRuns: 1, runsInRange: 10, runsTotal: 40, completedRuns: 8, succeededRuns: 6, series: [] },
    usage: { projectId: 'p1', from: 'a', to: 'b', usageByModel: [], usageByAgent: [{ id: 'a1', requestCount: 1, inputTokens: null, outputTokens: null, cachedTokens: null, cacheWriteTokens: null, reasoningTokens: null, avgDurationMs: null, runs: 1, unpricedRequestCount: null, cost: [] }] },
    metricsError: null,
    usageError: null,
    loading: false,
    loadedAt: null,
    reload: vi.fn(),
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/projects/p1']}>
      <Routes><Route path="/projects/:projectId" element={<DashboardPage />} /></Routes>
    </MemoryRouter>,
  );
}

describe('DashboardPage', () => {
  beforeEach(() => {
    mocks.project = loaded({ projectId: 'p1', name: 'Alpha', state: 'active' });
    mocks.configuration = loaded({ projectId: 'p1', revision: 12, configuration: config() });
    mocks.runs = [];
    mocks.useProjectRunList.mockClear();
    mocks.reloadRuns.mockClear();
    mocks.useProjectMetrics.mockClear();
    mocks.metricsData = metricsData();
  });

  it('shows loading until the configuration arrives', () => {
    mocks.configuration = loaded(null, { loading: true });
    renderPage();
    expect(screen.getByText('Loading dashboard')).toBeTruthy();
    expect(screen.queryByText('Setup checks')).toBeNull();
  });

  it('shows a load error', () => {
    mocks.configuration = loaded(null, { error: new Error('Could not load') });
    renderPage();
    expect(screen.getByText('Could not load')).toBeTruthy();
  });

  it('shows a ready project with its checks, summary, and team', () => {
    mocks.runs = [run('r1', 'blocked'), run('r2', 'active'), run('r3', 'idle')];
    renderPage();
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { pageSize: 5 });
    expect(mocks.useProjectMetrics).toHaveBeenCalledWith('p1', '30d');
    expect(screen.getByRole('link', { name: 'Open run Task r1' }).getAttribute('href')).toBe('/projects/p1/runs/r1');
    expect(screen.getByText('Ready')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Project is set up for runs' })).toBeTruthy();
    const checks = within(screen.getByRole('list', { name: 'Setup checks' })).getAllByRole('listitem').map((entry) => entry.textContent);
    expect(checks).toEqual(['Default model: gpt-test', 'Repository: octo/repo', '1 agent in the team', '1 skill assigned']);
    const actions = screen.getByLabelText('Project actions');
    expect(within(actions).getByRole('link', { name: 'Open Orchestrations' }).getAttribute('href')).toBe('/projects/p1/orchestrations');
    expect(within(actions).getAllByRole('link', { name: 'Open Orchestrations' })).toHaveLength(1);
    const summary = screen.getByLabelText('Project summary');
    expect(summary.textContent).toContain('Active runs2');
    expect(summary.textContent).toContain('Needs input1');
    expect(summary.textContent).toContain('Runs in range10In the last 30 days · 40 total');
    expect(summary.textContent).toContain('Success75%6 of 8 completed runs succeeded');
    expect(screen.getByRole('table', { name: 'Usage by agent' }).textContent).toContain('Ada');
    expect(summary.textContent).not.toContain('Configuration revision');
    // Ada appears in the Team panel and in the agent usage table.
    expect(screen.getAllByText('Ada')).toHaveLength(2);
  });

  it('points setup at the first missing step', () => {
    mocks.configuration = loaded({ projectId: 'p1', revision: 1, configuration: config({ modelSelection: null, agentCharters: [], casting: [] }) });
    renderPage();
    expect(screen.getByText('Setup needed')).toBeTruthy();
    const actions = screen.getByLabelText('Project actions');
    expect(within(actions).getByRole('link', { name: 'Choose a model' }).getAttribute('href')).toBe('/projects/p1/settings?section=models');
    expect(within(actions).getByRole('link', { name: 'Open Orchestrations' })).toBeTruthy();
    expect(screen.getByText('No agents yet. Add agents in Agents.')).toBeTruthy();
  });

  it('shows the archived state', () => {
    mocks.project = loaded({ projectId: 'p1', name: 'Alpha', state: 'archived' });
    renderPage();
    expect(screen.getByText('Archived')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'This project is archived' })).toBeTruthy();
  });

  it('reloads the project, configuration, and runs on Refresh', () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect((mocks.project as LoadedStub<unknown>).reload).toHaveBeenCalled();
    expect((mocks.configuration as LoadedStub<unknown>).reload).toHaveBeenCalled();
    expect(mocks.reloadRuns).toHaveBeenCalledTimes(1);
    expect((mocks.metricsData as { reload: () => void }).reload).toHaveBeenCalled();
  });
});
