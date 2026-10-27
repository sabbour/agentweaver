import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import type {
  BacklogGraph,
  BacklogTaskSnapshot,
  PlatformComponentArea,
  PlatformComponentAreaReport,
  ProjectIssueSummary,
  ProjectMetrics,
  ProjectPullRequestSummary,
  ProjectRunSummary,
  ProjectUsage,
  VersionedProjectConfiguration,
} from './contracts';
import {
  ClusterPage,
  DiagnosticsPage,
  FlowPage,
  HeartbeatPage,
  ProjectObservabilityPage,
  WorkflowsPage,
  WorkspacePage,
} from './OperationsPages';
import { metricsRange } from './projectMetrics';
import type { AttentionItem, RunAttention } from './runAttention';

const mocks = vi.hoisted(() => ({
  listProjectRuns: vi.fn(),
  readProjectBacklog: vi.fn(),
  listProjectIssues: vi.fn(),
  getProjectConfiguration: vi.fn(),
  updateProjectConfiguration: vi.fn(),
  listProjectPullRequests: vi.fn(),
  getProjectMetrics: vi.fn(),
  getProjectUsage: vi.fn(),
  getPlatformComponents: vi.fn(),
  attentionReload: vi.fn(),
  attention: { items: [] as unknown[], count: 0, loading: false, error: null as unknown },
}));

// One stable apiCall, like the real provider, so hooks do not read again on every render.
const auth = vi.hoisted(() => ({
  session: { accessToken: 'token' },
  apiCall: (operation: (token: string, tenant: string) => unknown) => operation('token', 'tenant-1'),
}));

vi.mock('./AuthContext', () => ({ useAuth: () => auth }));

vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    listProjectRuns: mocks.listProjectRuns,
    readProjectBacklog: mocks.readProjectBacklog,
    listProjectIssues: mocks.listProjectIssues,
    getProjectConfiguration: mocks.getProjectConfiguration,
    updateProjectConfiguration: mocks.updateProjectConfiguration,
    listProjectPullRequests: mocks.listProjectPullRequests,
    getProjectMetrics: mocks.getProjectMetrics,
    getProjectUsage: mocks.getProjectUsage,
    getPlatformComponents: mocks.getPlatformComponents,
  },
}));

vi.mock('./RunAttentionContext', () => ({
  useSharedRunAttention: (): RunAttention => ({
    ...(mocks.attention as Pick<RunAttention, 'items' | 'count' | 'loading' | 'error'>),
    reload: mocks.attentionReload,
    dismiss: vi.fn(),
    dismissError: null,
  }),
}));
vi.mock('./runAttention', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runAttention')>(),
  useRunAttention: () => ({
    items: [], count: 0, loading: false, error: null, reload: vi.fn(), dismiss: vi.fn(), dismissError: null,
  }),
}));

vi.mock('./pageParts', async (importOriginal) => ({
  ...await importOriginal<typeof import('./pageParts')>(),
  PageHeading: ({ title, actions }: { title: string; actions?: ReactNode }) => <header><h1>{title}</h1>{actions}</header>,
}));

/** Internal service and transport names that must not appear in user copy. */
const INTERNAL_NAMES = /gateway|broker|orchestrator|projects owner|coordinator|\bapi\b|endpoint|\/api\/|http/i;

function run(runId: string, title: string, state: ProjectRunSummary['state'] = 'active'): ProjectRunSummary {
  return {
    runId, rootSessionId: 'coordinator', title, goal: null, workflowId: null, state, needsInputCount: 0,
    createdAt: '2026-01-02T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z', completedAt: null,
    startedBy: { issuer: 'https://broker.test', subject: 'user-1' },
  };
}

function runPage(items: ProjectRunSummary[]) {
  return { projectId: 'p1', items, nextCursor: null };
}

function task(taskId: string, title: string, extra: Partial<BacklogTaskSnapshot> = {}): BacklogTaskSnapshot {
  return {
    reference: { projectId: 'p1', taskId }, revision: 1, state: 'backlog', isArchived: false,
    isAutomationInvocationPending: false, title, description: null, lifecycle: 'open', repositoryIssue: null, position: 0, ...extra,
  };
}

function graph(tasks: BacklogTaskSnapshot[]): BacklogGraph {
  return { projectId: 'p1', revision: 3, tasks, dependencies: [] };
}

function configuration(overrides: Record<string, unknown> = {}, revision = 4): VersionedProjectConfiguration {
  return {
    projectId: 'p1', revision, updatedByActorId: 'user-1', createdAt: '2026-01-01T00:00:00Z',
    configuration: { defaultWorkflowId: null, blueprintWorkflowReferences: [], agentCharters: [], ...overrides },
  } as unknown as VersionedProjectConfiguration;
}

const pull: ProjectPullRequestSummary = {
  providerId: 'github', repository: { owner: 'acme', name: 'web' }, number: 7, title: 'Add login',
  htmlUrl: 'https://github.com/acme/web/pull/7', state: 'merged', headBranch: 'feature/login', headSha: 'a'.repeat(40),
  baseBranch: 'main', baseSha: 'b'.repeat(40), runId: 'r1', disposition: 'created',
  recordedAt: '2026-01-03T00:00:00Z', updatedAt: '2026-01-03T00:00:00Z',
};

function pullPage(items: ProjectPullRequestSummary[]) {
  return { projectId: 'p1', coverage: { recordedSince: '2026-01-01T00:00:00Z', includesEarlierPullRequests: false }, items, nextCursor: null };
}

const issue: ProjectIssueSummary = {
  taskId: 't1', taskState: 'backlog', providerId: 'github', repository: { owner: 'acme', name: 'web' },
  number: 12, title: 'Draft spec', state: 'open', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
};

function issuePage(items: ProjectIssueSummary[]) {
  return { projectId: 'p1', items, nextCursor: null };
}

const metrics: ProjectMetrics = {
  projectId: 'p1', from: 'a', to: 'b', activeRuns: 4, blockedRuns: 2, runsInRange: 9, runsTotal: 30,
  completedRuns: 4, succeededRuns: 3, series: [{ date: '2026-01-01', created: 5, completed: 4 }],
};

const usage: ProjectUsage = {
  projectId: 'p1', from: 'a', to: 'b',
  usageByModel: [{
    id: 'gpt-test', requestCount: 12, inputTokens: 1000, outputTokens: 50, cachedTokens: 0, cacheWriteTokens: null,
    reasoningTokens: null, avgDurationMs: 1500, runs: 2, unpricedRequestCount: 0, cost: [],
  }],
  usageByAgent: [{
    id: 'a1', requestCount: 3, inputTokens: 10, outputTokens: 5, cachedTokens: null, cacheWriteTokens: null,
    reasoningTokens: null, avgDurationMs: null, runs: 1, unpricedRequestCount: null, cost: [],
  }],
};

function area(name: PlatformComponentArea, overrides: Partial<PlatformComponentAreaReport> = {}): PlatformComponentAreaReport {
  return {
    area: name, purpose: `${name} purpose`, owner: 'environment', cardinality: 'exclusive',
    readiness: 'ready', reason: null, active: [], available: [], ...overrides,
  };
}

function report(areas: PlatformComponentAreaReport[]) {
  return { observedAt: '2026-01-01T00:00:00Z', areas };
}

function promoted(notificationId: string, projectId: string, runId: string, taskCount: number): AttentionItem {
  return { source: 'backlogPromoted', notificationId, projectId, runId, sessionId: 's1', createdAt: '2026-01-02T00:00:00Z', promotionId: 'pr1', taskCount };
}

function renderPage(page: string, element: ReactElement) {
  return render(
    <MemoryRouter initialEntries={[`/projects/p1/${page}`]}>
      <Routes>
        <Route path={`/projects/:projectId/${page}`} element={element} />
      </Routes>
    </MemoryRouter>,
  );
}

/** The "Not available in this version" note body. */
function note(): HTMLElement {
  const title = screen.getByText('Not available in this version');
  return title.parentElement as HTMLElement;
}

function expectPlainNote(text: string) {
  const body = note();
  expect(body.textContent).toContain(text);
  expect(body.textContent).not.toMatch(INTERNAL_NAMES);
}

const forbidden = () => new GatewayError(403, {}, 'Forbidden');
const failure = (message: string) => new GatewayError(500, {}, message);

beforeEach(() => {
  vi.clearAllMocks();
  mocks.attention = { items: [], count: 0, loading: false, error: null };
  mocks.listProjectRuns.mockResolvedValue(runPage([]));
  mocks.readProjectBacklog.mockResolvedValue(graph([]));
  mocks.listProjectIssues.mockResolvedValue(issuePage([]));
  mocks.getProjectConfiguration.mockResolvedValue(configuration());
  mocks.listProjectPullRequests.mockResolvedValue(pullPage([]));
  mocks.getProjectMetrics.mockResolvedValue({ ...metrics, activeRuns: 0, blockedRuns: 0, runsInRange: 0, runsTotal: 0, completedRuns: 0, succeededRuns: 0, series: [] });
  mocks.getProjectUsage.mockResolvedValue({ ...usage, usageByModel: [], usageByAgent: [] });
  mocks.getPlatformComponents.mockResolvedValue(report([]));
});

describe('FlowPage', () => {
  it('shows this project’s notifications, task stages, and open runs', async () => {
    mocks.attention = {
      items: [promoted('n1', 'p1', 'run-a', 2), promoted('n2', 'p2', 'run-b', 1)],
      count: 2, loading: false, error: null,
    };
    mocks.readProjectBacklog.mockResolvedValue(graph([
      task('t1', 'Draft spec'),
      task('t2', 'Build API', { state: 'ready' }),
      task('t3', 'Ship UI', { state: 'claimed' }),
      task('t4', 'Old work', { lifecycle: 'closed' }),
      task('t5', 'Archived', { isArchived: true }),
    ]));
    mocks.listProjectRuns.mockResolvedValue(runPage([run('r1', 'Fix the login bug')]));
    renderPage('flow', <FlowPage />);

    expect(await screen.findByRole('link', { name: 'Open run Fix the login bug' })).toBeTruthy();
    const attention = screen.getByRole('list', { name: 'Needs your attention' });
    expect(within(attention).getAllByRole('listitem')).toHaveLength(1);
    expect(within(attention).getByText('2 subtasks created')).toBeTruthy();
    expect(within(attention).getByText('p1 · Run run-a')).toBeTruthy();
    expect(screen.queryByText(/Run run-b/)).toBeNull();
    // The other project's item does not inflate the "more" hint.
    expect(screen.queryByText(/Showing the newest/)).toBeNull();
    const stages = await screen.findByLabelText('Tasks by stage');
    expect(stages.textContent).toContain('Backlog1');
    expect(stages.textContent).toContain('Ready1');
    expect(stages.textContent).toContain('Active1');
    expect(stages.textContent).toContain('Done1');
    expect(screen.getByRole('link', { name: 'Open Board' }).getAttribute('href')).toBe('/projects/p1/board');
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { states: undefined, cursor: null, pageSize: 25 }, 'tenant-1');
  });

  it('refreshes notifications, runs, and the board', async () => {
    renderPage('flow', <FlowPage />);
    await screen.findByText('No open runs.');
    await waitFor(() => expect(mocks.readProjectBacklog).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(mocks.attentionReload).toHaveBeenCalled();
    await waitFor(() => expect(mocks.listProjectRuns).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.readProjectBacklog).toHaveBeenCalledTimes(2));
  });

  it('shows empty states', async () => {
    renderPage('flow', <FlowPage />);
    expect(await screen.findByText('No open runs.')).toBeTruthy();
    expect(screen.getAllByText('Nothing needs your attention.').length).toBeGreaterThan(0);
    const stages = await screen.findByLabelText('Tasks by stage');
    expect(stages.textContent).toContain('Backlog0');
    expect(stages.textContent).toContain('Done0');
  });

  it('explains a 403 in plain copy', async () => {
    mocks.listProjectRuns.mockRejectedValue(forbidden());
    mocks.readProjectBacklog.mockRejectedValue(forbidden());
    renderPage('flow', <FlowPage />);
    expect(await screen.findByText('You do not have permission to read runs in this project.')).toBeTruthy();
    expect(await screen.findByText("You don't have permission to view this project's board.")).toBeTruthy();
    expect(screen.queryByLabelText('Tasks by stage')).toBeNull();
  });

  it('shows other errors', async () => {
    mocks.listProjectRuns.mockRejectedValue(failure('Runs are down'));
    mocks.readProjectBacklog.mockRejectedValue(failure('Backlog is down'));
    mocks.attention = { items: [], count: 0, loading: false, error: new Error('Notifications are down') };
    renderPage('flow', <FlowPage />);
    expect(await screen.findByText('HTTP 500: Runs are down')).toBeTruthy();
    expect(await screen.findByText('HTTP 500: Backlog is down')).toBeTruthy();
    expect(screen.getByText('Could not load notifications. Notifications are down')).toBeTruthy();
  });

  it('names the missing live agent graph in plain copy', async () => {
    renderPage('flow', <FlowPage />);
    await screen.findByText('No open runs.');
    expectPlainNote('The live agent graph for the whole project is not available.');
  });
});

describe('WorkflowsPage', () => {
  it('shows the default workflow and the workflow for each blueprint', async () => {
    mocks.getProjectConfiguration.mockResolvedValue(configuration({
      defaultWorkflowId: 'wf-default',
      blueprintWorkflowReferences: [{ blueprintId: 'bp-review', workflowId: 'wf-review' }],
    }, 7));
    renderPage('workflows', <WorkflowsPage />);
    const defaultWorkflow = await screen.findByRole('list', { name: 'Active workflow' });
    expect(within(defaultWorkflow).getByText('wf-default')).toBeTruthy();
    expect(within(defaultWorkflow).getByText('Project default')).toBeTruthy();
    const blueprintWorkflows = screen.getByRole('list', { name: 'Referenced workflows' });
    expect(within(blueprintWorkflows).getByText('wf-review')).toBeTruthy();
    fireEvent.click(within(blueprintWorkflows).getByRole('button', { name: 'View blueprints for wf-review' }));
    expect(within(blueprintWorkflows).getByText('bp-review')).toBeTruthy();
    expect(screen.getByText('Configuration revision 7')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Open Project settings' }).getAttribute('href')).toBe('/projects/p1/settings');
  });

  it('groups references by workflow, keeps the default first, and expands assignments with the keyboard', async () => {
    const user = userEvent.setup();
    mocks.getProjectConfiguration.mockResolvedValue(configuration({
      defaultWorkflowId: 'wf-default',
      blueprintWorkflowReferences: [
        { blueprintId: 'bp-build', workflowId: 'wf-default' },
        { blueprintId: 'bp-review', workflowId: 'wf-review' },
        { blueprintId: 'bp-verify', workflowId: 'wf-review' },
      ],
    }));
    renderPage('workflows', <WorkflowsPage />);
    const active = await screen.findByRole('list', { name: 'Active workflow' });
    expect(within(active).getByText('1 blueprint uses this workflow.')).toBeTruthy();
    const referenced = screen.getByRole('list', { name: 'Referenced workflows' });
    expect(within(referenced).getAllByRole('listitem')).toHaveLength(1);
    expect(within(referenced).getByText('2 blueprints use this workflow.')).toBeTruthy();
    const toggle = within(referenced).getByRole('button', { name: 'View blueprints for wf-review' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('list', { name: 'Blueprints using wf-review' })).toBeNull();
    toggle.focus();
    await user.keyboard('{Enter}');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const assignments = screen.getByRole('list', { name: 'Blueprints using wf-review' });
    expect(assignments.parentElement?.id).toBe(toggle.getAttribute('aria-controls'));
    expect(within(assignments).getAllByRole('listitem').map((item) => item.textContent)).toEqual(['bp-review', 'bp-verify']);
    await user.keyboard(' ');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(toggle);
    expect(screen.queryByRole('button', { name: /Run now|View graph|Edit|Sync|Set as default/ })).toBeNull();
  });

  it('does not duplicate the active workflow when all blueprints use it', async () => {
    mocks.getProjectConfiguration.mockResolvedValue(configuration({
      defaultWorkflowId: 'wf-default',
      blueprintWorkflowReferences: [{ blueprintId: 'bp-build', workflowId: 'wf-default' }],
    }));
    renderPage('workflows', <WorkflowsPage />);
    await screen.findByRole('list', { name: 'Active workflow' });
    expect(screen.getAllByText('wf-default')).toHaveLength(1);
    expect(screen.getByText('All blueprint workflow references use the project default shown above.')).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Referenced workflows' })).toBeNull();
  });

  it('uses the incumbent loading cards without reporting an empty catalog', async () => {
    mocks.getProjectConfiguration.mockReturnValue(new Promise(() => {}));
    renderPage('workflows', <WorkflowsPage />);
    expect(screen.getByRole('status', { name: 'Loading workflow references' }).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('No other workflow references')).toBeNull();
    expect(screen.getByRole('button', { name: 'Refresh' }).hasAttribute('disabled')).toBe(true);
  });

  it('resets the saved default without changing other project settings', async () => {
    const saved = configuration({
      defaultWorkflowId: null,
      blueprintWorkflowReferences: [{ blueprintId: 'bp-review', workflowId: 'wf-review' }],
    }, 8);
    mocks.getProjectConfiguration.mockResolvedValue(configuration({
      defaultWorkflowId: 'wf-default',
      blueprintWorkflowReferences: [{ blueprintId: 'bp-review', workflowId: 'wf-review' }],
    }, 7));
    mocks.updateProjectConfiguration.mockResolvedValue(saved);
    renderPage('workflows', <WorkflowsPage />);

    await screen.findByText('wf-default');
    fireEvent.click(screen.getByRole('button', { name: 'Use platform default' }));

    expect(await screen.findByText('None set. Runs use the platform default.')).toBeTruthy();
    expect(mocks.updateProjectConfiguration).toHaveBeenCalledWith(
      'token',
      'p1',
      7,
      expect.objectContaining({
        defaultWorkflowId: null,
        blueprintWorkflowReferences: [{ blueprintId: 'bp-review', workflowId: 'wf-review' }],
      }),
      'tenant-1',
    );
  });

  it('keeps the saved default reset when an older refresh finishes later', async () => {
    const current = configuration({ defaultWorkflowId: 'wf-default' }, 7);
    const saved = configuration({ defaultWorkflowId: null }, 8);
    let resolveRefresh!: (value: VersionedProjectConfiguration) => void;
    mocks.getProjectConfiguration
      .mockResolvedValueOnce(current)
      .mockImplementationOnce(() => new Promise((resolve) => { resolveRefresh = resolve; }));
    mocks.updateProjectConfiguration.mockResolvedValue(saved);
    renderPage('workflows', <WorkflowsPage />);

    await screen.findByText('wf-default');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(mocks.getProjectConfiguration).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('button', { name: 'Use platform default' }));
    expect(await screen.findByText('None set. Runs use the platform default.')).toBeTruthy();

    await act(async () => resolveRefresh(current));
    expect(screen.getByText('None set. Runs use the platform default.')).toBeTruthy();
  });

  it('explains when the default workflow cannot be changed', async () => {
    mocks.getProjectConfiguration.mockResolvedValue(configuration({ defaultWorkflowId: 'wf-default' }, 7));
    mocks.updateProjectConfiguration.mockRejectedValue(forbidden());
    renderPage('workflows', <WorkflowsPage />);

    await screen.findByText('wf-default');
    fireEvent.click(screen.getByRole('button', { name: 'Use platform default' }));

    expect(await screen.findByText('You do not have permission to change the default workflow in this project.')).toBeTruthy();
    expect(screen.getByText('wf-default')).toBeTruthy();
  });

  it('shows empty states', async () => {
    renderPage('workflows', <WorkflowsPage />);
    expect(await screen.findByText('None set. Runs use the platform default.')).toBeTruthy();
    expect(screen.getByText('No blueprint uses a specific workflow.')).toBeTruthy();
    expect(screen.queryByRole('list')).toBeNull();
  });

  it('explains a 403 in plain copy', async () => {
    mocks.getProjectConfiguration.mockRejectedValue(forbidden());
    renderPage('workflows', <WorkflowsPage />);
    expect(await screen.findByText('You do not have permission to read the configuration in this project.')).toBeTruthy();
  });

  it('shows other errors', async () => {
    mocks.getProjectConfiguration.mockRejectedValue(failure('Configuration is down'));
    renderPage('workflows', <WorkflowsPage />);
    expect(await screen.findByText('HTTP 500: Configuration is down')).toBeTruthy();
  });

  it('hides saved choices and open details when a refresh loses permission', async () => {
    mocks.getProjectConfiguration
      .mockResolvedValueOnce(configuration({
        defaultWorkflowId: 'wf-default',
        blueprintWorkflowReferences: [{ blueprintId: 'bp-secret', workflowId: 'wf-default' }],
      }))
      .mockRejectedValueOnce(forbidden());
    renderPage('workflows', <WorkflowsPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'View blueprints for wf-default' }));
    expect(screen.getByRole('list', { name: 'Blueprints using wf-default' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await screen.findByText('You do not have permission to read the configuration in this project.');
    expect(screen.queryByText('wf-default')).toBeNull();
    expect(screen.queryByText('bp-secret')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Use platform default' })).toBeNull();
    expect(screen.queryByText('No other workflow references')).toBeNull();
  });

  it('names the missing workflow features in plain copy', async () => {
    renderPage('workflows', <WorkflowsPage />);
    await screen.findByText('No blueprint uses a specific workflow.');
    expectPlainNote('The workflow catalog, definitions, validation results, editing, triggers, runs, generation, sync, and graphs are not available yet.');
  });
});

describe('WorkspacePage', () => {
  it('shows the repository, recorded pull requests, and synced issues', async () => {
    mocks.getProjectConfiguration.mockResolvedValue(configuration({ sourceControl: { repository: { owner: 'acme', name: 'web' } } }));
    mocks.listProjectPullRequests.mockResolvedValue(pullPage([pull, { ...pull, number: 8, title: null, state: 'open', runId: 'r2' }]));
    mocks.listProjectIssues.mockResolvedValue(issuePage([issue]));
    renderPage('workspace', <WorkspacePage />);

    expect(await screen.findByText('acme/web')).toBeTruthy();
    const pulls = await screen.findByRole('list', { name: 'Recorded pull requests' });
    const link = within(pulls).getByRole('link', { name: '#7 Add login' });
    expect(link.getAttribute('href')).toBe('https://github.com/acme/web/pull/7');
    // A pull request without a stored title shows its branches.
    expect(within(pulls).getByRole('link', { name: '#8 feature/login → main' })).toBeTruthy();
    expect(within(pulls).getAllByRole('link', { name: 'Open run' })[0].getAttribute('href')).toBe('/projects/p1/runs/r1');
    expect(within(pulls).getByText('merged')).toBeTruthy();
    expect(screen.getByText(/^Recorded since .+\. Earlier pull requests are not listed\.$/)).toBeTruthy();
    const issues = await screen.findByRole('list', { name: 'Synced issues' });
    expect(within(issues).getByText('#12 Draft spec')).toBeTruthy();
    expect(within(issues).getByText('acme/web · task backlog')).toBeTruthy();
    expect(mocks.listProjectPullRequests).toHaveBeenCalledWith('token', 'p1', { state: 'all', pageSize: 20 }, 'tenant-1');
    expect(mocks.listProjectIssues).toHaveBeenCalledWith('token', 'p1', { state: 'open', pageSize: 20 }, 'tenant-1');
  });

  it('shows empty states', async () => {
    renderPage('workspace', <WorkspacePage />);
    expect(await screen.findByText(/No repository is connected\./)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Connect one in Project settings' }).getAttribute('href')).toBe('/projects/p1/settings');
    expect(await screen.findByText('No run has opened a pull request yet.')).toBeTruthy();
    expect(await screen.findByText('No open issues are synced into the backlog.')).toBeTruthy();
  });

  it('says when the server does not record pull requests', async () => {
    mocks.listProjectPullRequests.mockRejectedValue(new GatewayError(503, { code: 'pull_request_records_unavailable' }, 'Unavailable'));
    renderPage('workspace', <WorkspacePage />);
    expect(await screen.findByText('This server does not record pull requests yet.')).toBeTruthy();
  });

  it('explains a 403 in plain copy', async () => {
    mocks.getProjectConfiguration.mockRejectedValue(forbidden());
    mocks.listProjectPullRequests.mockRejectedValue(forbidden());
    mocks.listProjectIssues.mockRejectedValue(forbidden());
    renderPage('workspace', <WorkspacePage />);
    expect(await screen.findByText('You do not have permission to read the configuration in this project.')).toBeTruthy();
    expect(await screen.findByText('You do not have permission to read pull requests in this project.')).toBeTruthy();
    expect(await screen.findByText('You do not have permission to read issues in this project.')).toBeTruthy();
  });

  it('shows other errors', async () => {
    mocks.getProjectConfiguration.mockRejectedValue(failure('Configuration is down'));
    mocks.listProjectPullRequests.mockRejectedValue(failure('Pull requests are down'));
    mocks.listProjectIssues.mockRejectedValue(new Error('Issues are down'));
    renderPage('workspace', <WorkspacePage />);
    expect(await screen.findByText('HTTP 500: Configuration is down')).toBeTruthy();
    expect(await screen.findByText('HTTP 500: Pull requests are down')).toBeTruthy();
    expect(await screen.findByText('Issues are down')).toBeTruthy();
  });

  it('names the missing file browser in plain copy', async () => {
    renderPage('workspace', <WorkspacePage />);
    await screen.findByText('No run has opened a pull request yet.');
    expectPlainNote('Browsing branches and repository files is not available.');
  });
});

describe('ProjectObservabilityPage', () => {
  it('shows metrics, usage with agent names, and recent runs', async () => {
    mocks.getProjectMetrics.mockResolvedValue(metrics);
    mocks.getProjectUsage.mockResolvedValue(usage);
    mocks.getProjectConfiguration.mockResolvedValue(configuration({ agentCharters: [{ agentId: 'a1', name: 'Ada' }] }));
    mocks.listProjectRuns.mockResolvedValue(runPage([run('r1', 'Nightly sync', 'completed')]));
    renderPage('observability', <ProjectObservabilityPage />);

    const models = await screen.findByRole('table', { name: 'Usage by model' });
    expect(within(models).getAllByRole('row')[1].textContent).toContain('gpt-test');
    const agents = screen.getByRole('table', { name: 'Usage by agent' });
    await waitFor(() => expect(within(agents).getByText('Ada')).toBeTruthy());
    expect(screen.getByRole('img').getAttribute('aria-label')).toContain('5 created and 4 completed');
    expect(await screen.findByRole('link', { name: 'Open run Nightly sync' })).toBeTruthy();
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { states: undefined, cursor: null, pageSize: 10 }, 'tenant-1');
  });

  it('shows empty states', async () => {
    renderPage('observability', <ProjectObservabilityPage />);
    expect(await screen.findByText('No runs were created or completed in this range.')).toBeTruthy();
    expect(screen.getByText('No model usage in this range.')).toBeTruthy();
    expect(screen.getByText('No agent usage in this range.')).toBeTruthy();
    expect(await screen.findByText('No runs yet.')).toBeTruthy();
  });

  it('explains a 403 in plain copy', async () => {
    mocks.getProjectMetrics.mockRejectedValue(forbidden());
    mocks.getProjectUsage.mockRejectedValue(forbidden());
    mocks.listProjectRuns.mockRejectedValue(forbidden());
    renderPage('observability', <ProjectObservabilityPage />);
    expect(await screen.findByText('You do not have permission to read run metrics for this project.')).toBeTruthy();
    expect(screen.getByText('You do not have permission to read usage for this project.')).toBeTruthy();
    expect(await screen.findByText('You do not have permission to read runs in this project.')).toBeTruthy();
  });

  it('shows other errors', async () => {
    mocks.getProjectMetrics.mockRejectedValue(failure('Metrics are down'));
    mocks.getProjectUsage.mockRejectedValue(failure('Usage is down'));
    mocks.listProjectRuns.mockRejectedValue(failure('Runs are down'));
    renderPage('observability', <ProjectObservabilityPage />);
    expect(await screen.findByText('HTTP 500: Metrics are down')).toBeTruthy();
    expect(screen.getByText('HTTP 500: Usage is down')).toBeTruthy();
    expect(await screen.findByText('HTTP 500: Runs are down')).toBeTruthy();
  });

  it('names the missing traces and percentiles in plain copy', async () => {
    renderPage('observability', <ProjectObservabilityPage />);
    await screen.findByText('No runs yet.');
    expectPlainNote('Per-run traces and response time percentiles (p50 and p95) are not available.');
  });
});

describe('DiagnosticsPage', () => {
  it('shows areas that are not ready and failed runs', async () => {
    mocks.getPlatformComponents.mockResolvedValue(report([
      area('sessions'),
      area('canvas', { readiness: 'disabled' }),
      area('sandbox', { readiness: 'degraded', reason: 'readinessTimedOut' }),
    ]));
    mocks.listProjectRuns.mockResolvedValue(runPage([run('r9', 'Broken deploy', 'failed')]));
    renderPage('diagnostics', <DiagnosticsPage />);

    const table = await screen.findByRole('table', { name: 'Platform component areas' });
    // One header row plus only the degraded area; ready and disabled areas are left out.
    expect(within(table).getAllByRole('row')).toHaveLength(2);
    expect(within(table).getByText('Degraded')).toBeTruthy();
    expect(within(table).getByText('The readiness check timed out.')).toBeTruthy();
    expect(await screen.findByRole('link', { name: 'Open run Broken deploy' })).toBeTruthy();
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { states: ['failed', 'indeterminate'], cursor: null, pageSize: 10 }, 'tenant-1');
  });

  it('shows empty states', async () => {
    mocks.getPlatformComponents.mockResolvedValue(report([area('sessions'), area('canvas', { readiness: 'disabled' })]));
    renderPage('diagnostics', <DiagnosticsPage />);
    expect(await screen.findByText('Every enabled platform area reports Ready.')).toBeTruthy();
    expect(await screen.findByText('No failed runs.')).toBeTruthy();
  });

  it('explains a 403 in plain copy and still shows runs', async () => {
    mocks.getPlatformComponents.mockRejectedValue(forbidden());
    mocks.listProjectRuns.mockResolvedValue(runPage([run('r9', 'Broken deploy', 'failed')]));
    renderPage('diagnostics', <DiagnosticsPage />);
    expect(await screen.findByText('Only tenant or platform administrators can view platform components.')).toBeTruthy();
    expect(await screen.findByRole('link', { name: 'Open run Broken deploy' })).toBeTruthy();
  });

  it('explains a run list 403 in plain copy', async () => {
    mocks.listProjectRuns.mockRejectedValue(forbidden());
    renderPage('diagnostics', <DiagnosticsPage />);
    expect(await screen.findByText('You do not have permission to read runs in this project.')).toBeTruthy();
  });

  it('shows other errors', async () => {
    mocks.getPlatformComponents.mockRejectedValue(failure('Components are down'));
    mocks.listProjectRuns.mockRejectedValue(failure('Runs are down'));
    renderPage('diagnostics', <DiagnosticsPage />);
    expect(await screen.findByText('HTTP 500: Components are down')).toBeTruthy();
    expect(await screen.findByText('HTTP 500: Runs are down')).toBeTruthy();
  });

  it('names the missing diagnostics in plain copy', async () => {
    renderPage('diagnostics', <DiagnosticsPage />);
    await screen.findByText('No failed runs.');
    expectPlainNote('Service self-checks, detailed diagnostics, and project diagnostic reports are not available.');
  });
});

describe('HeartbeatPage', () => {
  it('shows the seven-day tiles and active or blocked runs', async () => {
    mocks.getProjectMetrics.mockResolvedValue(metrics);
    mocks.listProjectRuns.mockResolvedValue(runPage([run('r1', 'Answer review', 'blocked')]));
    renderPage('heartbeat', <HeartbeatPage />);

    const tiles = screen.getByLabelText('Run summary');
    await waitFor(() => expect(tiles.textContent).toContain('Active runs4'));
    expect(tiles.textContent).toContain('Needs input2');
    expect(tiles.textContent).toContain('Runs in range9In the last 7 days · 30 total');
    expect(tiles.textContent).toContain('Success75%');
    expect(await screen.findByRole('link', { name: 'Open run Answer review' })).toBeTruthy();
    expect(mocks.listProjectRuns).toHaveBeenCalledWith('token', 'p1', { states: ['active', 'blocked'], cursor: null, pageSize: 25 }, 'tenant-1');
    const [, , span] = mocks.getProjectMetrics.mock.calls[0] as [string, string, { from: string; to: string }];
    expect(span.from).toBe(metricsRange('7d').from);
  });

  it('reads the tiles and runs again on Refresh', async () => {
    renderPage('heartbeat', <HeartbeatPage />);
    await screen.findByText('No run is active or blocked.');
    await waitFor(() => expect(mocks.getProjectMetrics).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(mocks.getProjectMetrics).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.listProjectRuns).toHaveBeenCalledTimes(2));
  });

  it('shows empty states', async () => {
    renderPage('heartbeat', <HeartbeatPage />);
    expect(await screen.findByText('No run is active or blocked.')).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText('Run summary').textContent).toContain('Active runs0'));
    expect(screen.getByLabelText('Run summary').textContent).toContain('No completed runs in this range');
  });

  it('explains a 403 in plain copy', async () => {
    mocks.getProjectMetrics.mockRejectedValue(forbidden());
    mocks.listProjectRuns.mockRejectedValue(forbidden());
    renderPage('heartbeat', <HeartbeatPage />);
    expect(await screen.findByText('You do not have permission to read run metrics in this project.')).toBeTruthy();
    expect(await screen.findByText('You do not have permission to read runs in this project.')).toBeTruthy();
    // Tiles show dashes, not zeros, when metrics fail.
    expect(screen.getByLabelText('Run summary').textContent).toContain('Not available');
  });

  it('shows other errors', async () => {
    mocks.getProjectMetrics.mockRejectedValue(failure('Metrics are down'));
    mocks.listProjectRuns.mockRejectedValue(failure('Runs are down'));
    renderPage('heartbeat', <HeartbeatPage />);
    expect(await screen.findByText('HTTP 500: Metrics are down')).toBeTruthy();
    expect(await screen.findByText('HTTP 500: Runs are down')).toBeTruthy();
  });

  it('names the missing heartbeat records in plain copy', async () => {
    renderPage('heartbeat', <HeartbeatPage />);
    await screen.findByText('No run is active or blocked.');
    expectPlainNote('Heartbeat records from each service process are not available.');
  });
});

describe('ClusterPage', () => {
  it('shows every platform area with its state', async () => {
    mocks.getPlatformComponents.mockResolvedValue(report([
      area('sessions', {
        active: [{
          implementationId: 'aca', displayName: 'Container sessions', version: '1.2', hosting: 'hosted', layer: null,
          meterSource: null, order: null, readiness: 'ready', reason: null, observedAt: '2026-01-01T00:00:00Z',
        }],
      }),
      area('canvas', { readiness: 'notAvailable', reason: 'noBackendOwner', owner: 'none' }),
    ]));
    renderPage('cluster', <ClusterPage />);
    const table = await screen.findByRole('table', { name: 'Platform component areas' });
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    expect(within(table).getByText('Container sessions')).toBeTruthy();
    expect(within(table).getByText('No service owns this area yet.')).toBeTruthy();
    expect(screen.getByText(/^Reported /)).toBeTruthy();
  });

  it('shows the empty state', async () => {
    renderPage('cluster', <ClusterPage />);
    expect(await screen.findByText('No platform component areas were reported.')).toBeTruthy();
  });

  it('explains a 403 in plain copy', async () => {
    mocks.getPlatformComponents.mockRejectedValue(forbidden());
    renderPage('cluster', <ClusterPage />);
    expect(await screen.findByText('Only tenant or platform administrators can view platform components.')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('shows other errors', async () => {
    mocks.getPlatformComponents.mockRejectedValue(failure('Components are down'));
    renderPage('cluster', <ClusterPage />);
    expect(await screen.findByText('HTTP 500: Components are down')).toBeTruthy();
  });

  it('names the missing node membership in plain copy', async () => {
    renderPage('cluster', <ClusterPage />);
    await screen.findByText('No platform component areas were reported.');
    expectPlainNote('Node and replica membership for the cluster is not available.');
  });
});
