import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { BoardPage } from './BoardPage';
import type { BacklogGraph, BacklogTaskSnapshot, ProjectAuthorizationContextResponse } from './contracts';

const mocks = vi.hoisted(() => ({
  readProjectBacklog: vi.fn(),
  listProjectIssues: vi.fn(),
  captureBacklogTask: vi.fn(),
  editBacklogTaskContent: vi.fn(),
  setProjectBacklogTaskState: vi.fn(),
  setProjectBacklogTaskPosition: vi.fn(),
  setAllProjectBacklogTasksReady: vi.fn(),
  archiveProjectBacklogTask: vi.fn(),
  addProjectBacklogDependency: vi.fn(),
  removeProjectBacklogDependency: vi.fn(),
  deleteProjectBacklogTask: vi.fn(),
  getAuthorizationContext: vi.fn(),
  getProject: vi.fn(),
  apiCall: vi.fn(),
  useProjectMetrics: vi.fn(),
  useProjectRunList: vi.fn(),
  session: { accessToken: 'token', binding: null },
  authorizationContext: { tenantId: 'tenant-1', membershipRevision: 1 },
  authorizationData: null as unknown,
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    session: mocks.session,
    authorizationContext: mocks.authorizationContext,
    apiCall: mocks.apiCall,
  }),
}));

vi.mock('./projectData', async (importOriginal) => ({
  ...await importOriginal<typeof import('./projectData')>(),
  useAuthorizationContext: () => ({ data: mocks.authorizationData, error: null, loading: false }),
}));

vi.mock('./projectMetrics', async (importOriginal) => ({
  ...await importOriginal<typeof import('./projectMetrics')>(),
  useProjectMetrics: mocks.useProjectMetrics,
}));

vi.mock('./runListData', async (importOriginal) => ({
  ...await importOriginal<typeof import('./runListData')>(),
  useProjectRunList: mocks.useProjectRunList,
}));

vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    readProjectBacklog: mocks.readProjectBacklog,
    listProjectIssues: mocks.listProjectIssues,
    captureBacklogTask: mocks.captureBacklogTask,
    editBacklogTaskContent: mocks.editBacklogTaskContent,
    setProjectBacklogTaskState: mocks.setProjectBacklogTaskState,
    setProjectBacklogTaskPosition: mocks.setProjectBacklogTaskPosition,
    setAllProjectBacklogTasksReady: mocks.setAllProjectBacklogTasksReady,
    archiveProjectBacklogTask: mocks.archiveProjectBacklogTask,
    deleteProjectBacklogTask: mocks.deleteProjectBacklogTask,
    addProjectBacklogDependency: mocks.addProjectBacklogDependency,
    removeProjectBacklogDependency: mocks.removeProjectBacklogDependency,
    getAuthorizationContext: mocks.getAuthorizationContext,
    getProject: mocks.getProject,
  },
}));

function task(taskId: string, title: string, extra: Partial<BacklogTaskSnapshot> = {}): BacklogTaskSnapshot {
  return {
    reference: { projectId: 'p1', taskId },
    revision: 1,
    state: 'backlog',
    isArchived: false,
    isAutomationInvocationPending: false,
    title,
    description: null,
    lifecycle: 'open',
    repositoryIssue: null,
    position: 0,
    ...extra,
  };
}

function graph(
  tasks: BacklogTaskSnapshot[],
  revision = 5,
  dependencies: BacklogGraph['dependencies'] = [],
  projectId = 'p1',
): BacklogGraph {
  return { projectId, revision, tasks, dependencies };
}

function runList(items: Array<{
  projectId: string;
  runId: string;
  label: string;
  executionState: string | null;
  time: { label: string; value: string };
}> = [], hasMore = false) {
  return { items, loading: false, error: null, hasMore, loadingMore: false, loadMore: vi.fn(), reload: vi.fn() };
}

function context(
  permissions: Array<'readProjects' | 'writeProjects'>,
  overrides: Partial<ProjectAuthorizationContextResponse> = {},
): ProjectAuthorizationContextResponse {
  return {
    contractVersion: 1,
    issuer: 'https://broker.test',
    actorId: 'user-1',
    tenantId: 'tenant-1',
    membershipRevision: 1,
    boundProjectId: null,
    boundRunId: null,
    effectiveAuthority: [{
      resourceType: 'project',
      resourceId: 'p1',
      permissions: permissions.map((permission) => ({ permission, roleRevision: 1 })),
    }],
    ...overrides,
  } as ProjectAuthorizationContextResponse;
}

const issueLink = { providerId: 'github', repository: { owner: 'acme', name: 'web' }, issueNumber: 12 };

function renderBoard() {
  let navigateTo: (path: string) => void = () => undefined;
  const tree = () => (
    <MemoryRouter initialEntries={['/projects/p1/board']}>
      <NavigationProbe onNavigate={(navigate) => { navigateTo = navigate; }} />
      <Routes>
        <Route path="/projects/:projectId/board" element={<BoardPage />} />
      </Routes>
    </MemoryRouter>
  );
  const result = render(tree());
  return {
    ...result,
    navigate: (path: string) => act(() => navigateTo(path)),
    rerenderBoard: () => result.rerender(tree()),
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

function dragTransfer() {
  const values = new Map<string, string>();
  return {
    effectAllowed: 'none',
    dropEffect: 'none',
    setData: (type: string, value: string) => values.set(type, value),
    getData: (type: string) => values.get(type) ?? '',
  } as unknown as DataTransfer;
}

function NavigationProbe({ onNavigate }: { onNavigate: (navigate: (path: string) => void) => void }) {
  const navigate = useNavigate();
  onNavigate(navigate);
  return null;
}

beforeEach(() => {
  mocks.session = { accessToken: 'token', binding: null };
  mocks.authorizationContext = { tenantId: 'tenant-1', membershipRevision: 1 };
  mocks.authorizationData = context(['readProjects', 'writeProjects']);
  mocks.apiCall.mockReset().mockImplementation((operation: (token: string, tenant: string) => Promise<unknown>) =>
    operation(mocks.session.accessToken, mocks.authorizationContext.tenantId));
  mocks.readProjectBacklog.mockReset().mockResolvedValue(graph([]));
  mocks.listProjectIssues.mockReset().mockResolvedValue({ projectId: 'p1', items: [], nextCursor: null });
  mocks.captureBacklogTask.mockReset();
  mocks.editBacklogTaskContent.mockReset();
  mocks.setProjectBacklogTaskState.mockReset();
  mocks.setProjectBacklogTaskPosition.mockReset();
  mocks.setAllProjectBacklogTasksReady.mockReset();
  mocks.archiveProjectBacklogTask.mockReset();
  mocks.deleteProjectBacklogTask.mockReset();
  mocks.addProjectBacklogDependency.mockReset();
  mocks.removeProjectBacklogDependency.mockReset();
  mocks.getAuthorizationContext.mockReset().mockResolvedValue(context(['readProjects', 'writeProjects']));
  mocks.getProject.mockReset().mockResolvedValue({ projectId: 'p1', name: 'Alpha', effectiveRoles: [] });
  mocks.useProjectMetrics.mockReset().mockReturnValue({
    metrics: {
      projectId: 'p1', from: 'a', to: 'b', activeRuns: 0, blockedRuns: 0, runsInRange: 0, runsTotal: 0,
      completedRuns: 0, succeededRuns: 0, series: [],
    },
    usage: null,
    metricsError: null,
    usageError: null,
    loading: false,
    loadedAt: null,
    reload: vi.fn(),
  });
  mocks.useProjectRunList.mockReset().mockImplementation(() => runList());
});

describe('BoardPage', () => {
  it('uses the Dashboard run metrics and shows review, problem, and history lanes', async () => {
    mocks.useProjectMetrics.mockReturnValue({
      metrics: {
        projectId: 'p1', from: 'a', to: 'b', activeRuns: 1, blockedRuns: 1, runsInRange: 3, runsTotal: 8,
        completedRuns: 2, succeededRuns: 1, series: [],
      },
      usage: null,
      metricsError: null,
      usageError: null,
      loading: false,
      loadedAt: null,
      reload: vi.fn(),
    });
    mocks.useProjectRunList.mockImplementation((_projectId: string, options?: { states?: string[] }) => {
      const state = options?.states?.[0];
      if (state === 'blocked') return runList([{
        projectId: 'p1', runId: 'review-1', label: 'Review the release', executionState: 'blocked',
        time: { label: 'Started', value: '2026-01-03T00:00:00Z' },
      }]);
      if (state === 'failed') return runList([{
        projectId: 'p1', runId: 'failed-1', label: 'Build the package', executionState: 'failed',
        time: { label: 'Started', value: '2026-01-02T00:00:00Z' },
      }]);
      if (state === 'terminal') return runList([{
        projectId: 'p1', runId: 'done-1', label: 'Ship the patch', executionState: 'completed',
        time: { label: 'Started', value: '2026-01-01T00:00:00Z' },
      }], true);
      return runList();
    });
    renderBoard();

    expect((await screen.findByTestId('board-active-run-count')).textContent).toContain('Active runs1');
    expect(screen.getByRole('heading', { name: 'Human review' })).toBeTruthy();
    expect(screen.getByText('Review the release')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Problems' })).toBeTruthy();
    expect(screen.getByText('Build the package')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Run history' })).toBeTruthy();
    expect(screen.getByText('Ship the patch')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Load more' })).toBeTruthy();
    expect(mocks.useProjectMetrics).toHaveBeenCalledWith('p1', '30d');
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { states: ['blocked'] });
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { states: ['failed', 'indeterminate'] });
    expect(mocks.useProjectRunList).toHaveBeenCalledWith('p1', { states: ['terminal'] });
  });

  it('shows unavailable run metrics instead of zero after a read failure', async () => {
    mocks.useProjectMetrics.mockReturnValue({
      metrics: null,
      usage: null,
      metricsError: new Error('Metrics are unavailable.'),
      usageError: null,
      loading: false,
      loadedAt: null,
      reload: vi.fn(),
    });
    renderBoard();

    const activeCount = await screen.findByTestId('board-active-run-count');
    expect(activeCount.textContent).toContain('—');
    expect(activeCount.textContent).not.toContain('0');
    expect(screen.getByText('Metrics are unavailable.')).toBeTruthy();
  });

  it('refreshes run metrics and every run lane with the Board', async () => {
    const reloadMetrics = vi.fn().mockResolvedValue(undefined);
    const reloads = {
      open: vi.fn(),
      review: vi.fn(),
      problem: vi.fn(),
      history: vi.fn(),
    };
    mocks.useProjectMetrics.mockReturnValue({
      metrics: null,
      usage: null,
      metricsError: null,
      usageError: null,
      loading: false,
      loadedAt: null,
      reload: reloadMetrics,
    });
    mocks.useProjectRunList.mockImplementation((_projectId: string, options?: { states?: string[] }) => {
      const state = options?.states?.[0];
      const reload = state === 'active' ? reloads.open
        : state === 'blocked' ? reloads.review
          : state === 'failed' ? reloads.problem
            : reloads.history;
      return { ...runList(), reload };
    });
    renderBoard();

    await waitFor(() => {
      expect((screen.getByRole('button', { name: 'Refresh' }) as HTMLButtonElement).disabled).toBe(false);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await waitFor(() => {
      expect(reloadMetrics).toHaveBeenCalledOnce();
      expect(reloads.open).toHaveBeenCalledOnce();
      expect(reloads.review).toHaveBeenCalledOnce();
      expect(reloads.problem).toHaveBeenCalledOnce();
      expect(reloads.history).toHaveBeenCalledOnce();
    });
  });

  it('groups tasks into the 0.x columns and adds synced issue details', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([
      task('t1', 'Draft spec', { repositoryIssue: issueLink }),
      task('t2', 'Build API', { state: 'ready' }),
      task('t3', 'Ship UI', { state: 'claimed' }),
      task('t4', 'Old work', { lifecycle: 'closed', state: 'ready' }),
      task('t5', 'Archived work', { isArchived: true }),
    ], 5, [{ task: { projectId: 'p1', taskId: 't2' }, prerequisite: { projectId: 'p1', taskId: 't1' } }]));
    mocks.listProjectIssues.mockResolvedValue({
      projectId: 'p1',
      items: [{
        taskId: 't1', taskState: 'backlog', providerId: 'github', repository: { owner: 'acme', name: 'web' },
        number: 12, title: 'Draft spec', state: 'open', createdAt: '', updatedAt: '',
      }],
      nextCursor: null,
    });
    renderBoard();

    const backlog = await screen.findByTestId('column-backlog');
    expect(within(backlog).getByText('Draft spec')).toBeTruthy();
    expect(within(backlog).getByText('Issue acme/web#12')).toBeTruthy();
    expect(within(backlog).getByText('Open')).toBeTruthy();
    expect(within(screen.getByTestId('column-ready')).getByText('Build API')).toBeTruthy();
    expect(within(screen.getByTestId('column-ready')).getByText('Needs Draft spec')).toBeTruthy();
    expect(within(screen.getByTestId('column-active')).getByText('Ship UI')).toBeTruthy();
    expect(within(screen.getByTestId('column-done')).getByText('Old work')).toBeTruthy();
    expect(screen.queryByText('Archived work')).toBeNull();
    expect(screen.getByTestId('count-backlog').textContent).toBe('1');
    expect(screen.getByTestId('board-move-guidance').textContent).toBe(
      'Drag cards within or between Backlog and Ready, or use task actions to change queue and order.',
    );
    expect(screen.getByTestId('task-card-t1').getAttribute('draggable')).toBe('true');
    expect(screen.getByTestId('task-card-t2').getAttribute('draggable')).toBe('true');
    expect(screen.getByTestId('task-card-t3').getAttribute('draggable')).toBe('false');
    expect(screen.getByTestId('task-card-t4').getAttribute('draggable')).toBe('false');
    expect(within(screen.getByTestId('task-card-t3')).queryByRole('button', { name: 'Mark Ready' })).toBeNull();
    expect(mocks.listProjectIssues).toHaveBeenCalledWith('token', 'p1', { state: 'all', cursor: null, pageSize: 100 }, 'tenant-1');
  });

  it('reorders tasks and sends eligible backlog work to Ready', async () => {
    const first = task('t1', 'First task', { position: 0 });
    const second = task('t2', 'Second task', { position: 1 });
    mocks.readProjectBacklog.mockResolvedValue(graph([first, second], 5));
    mocks.setProjectBacklogTaskPosition.mockResolvedValue(graph([
      task('t2', 'Second task', { position: 0 }),
      task('t1', 'First task', { position: 1 }),
    ], 6));
    mocks.setAllProjectBacklogTasksReady.mockResolvedValue(graph([
      task('t2', 'Second task', { state: 'ready', position: 0 }),
      task('t1', 'First task', { state: 'ready', position: 1 }),
    ], 7));
    renderBoard();

    fireEvent.click(await within(await screen.findByTestId('task-card-t1')).findByRole('button', { name: 'Move later' }));
    await waitFor(() => expect(mocks.setProjectBacklogTaskPosition).toHaveBeenCalledWith(
      'token', 'p1', 't1',
      { state: 'backlog', position: 1, expectedGraphRevision: 5, expectedTaskRevision: 1 },
      'tenant-1',
    ));

    fireEvent.click(await screen.findByRole('button', { name: 'Send all to Ready' }));
    await waitFor(() => expect(mocks.setAllProjectBacklogTasksReady).toHaveBeenCalledWith(
      'token', 'p1', { expectedGraphRevision: 6 }, 'tenant-1',
    ));
    expect(await screen.findByText('Eligible tasks moved to Ready.')).toBeTruthy();
    expect(within(screen.getByTestId('column-ready')).getByTestId('task-card-t1')).toBeTruthy();
  });

  it('moves a dragged Backlog task after a Ready task using the observed revisions', async () => {
    const backlogTask = task('t1', 'Backlog task', { position: 0 });
    const readyTask = task('t2', 'Ready task', { state: 'ready', position: 0 });
    mocks.readProjectBacklog.mockResolvedValue(graph([backlogTask, readyTask], 5));
    mocks.setProjectBacklogTaskPosition.mockResolvedValue(graph([
      task('t2', 'Ready task', { state: 'ready', position: 0 }),
      task('t1', 'Backlog task', { state: 'ready', position: 1 }),
    ], 6));
    renderBoard();

    const transfer = dragTransfer();
    fireEvent.dragStart(await screen.findByTestId('task-card-t1'), { dataTransfer: transfer });
    fireEvent.dragOver(await screen.findByTestId('task-card-t2'), { dataTransfer: transfer });
    fireEvent.drop(screen.getByTestId('task-card-t2'), { clientY: 100, dataTransfer: transfer });

    await waitFor(() => expect(mocks.setProjectBacklogTaskPosition).toHaveBeenCalledWith(
      'token', 'p1', 't1',
      { state: 'ready', position: 1, expectedGraphRevision: 5, expectedTaskRevision: 1 },
      'tenant-1',
    ));
    expect(await screen.findByText('Task moved to Ready.')).toBeTruthy();
    expect(within(screen.getByTestId('column-ready')).getByTestId('task-card-t1')).toBeTruthy();
  });

  it('keeps task drafts intact by disabling drag while the card is being edited', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Backlog task', { description: 'Saved description' })]));
    renderBoard();

    const card = await screen.findByTestId('task-card-t1');
    fireEvent.click(within(card).getByRole('button', { name: 'Edit task Backlog task' }));
    const title = within(card).getByRole('textbox', { name: 'Title' }) as HTMLInputElement;
    const description = within(card).getByRole('textbox', { name: 'Description' }) as HTMLTextAreaElement;
    fireEvent.change(title, { target: { value: 'Unsaved title draft' } });
    fireEvent.change(description, { target: { value: 'Unsaved description draft' } });

    expect(card.getAttribute('draggable')).toBe('false');
    const transfer = dragTransfer();
    fireEvent.dragStart(card, { dataTransfer: transfer });
    fireEvent.dragOver(screen.getByTestId('column-ready'), { dataTransfer: transfer });
    fireEvent.drop(screen.getByTestId('column-ready'), { dataTransfer: transfer });

    expect(within(card).getByRole('textbox', { name: 'Title' })).toHaveProperty('value', 'Unsaved title draft');
    expect(within(card).getByRole('textbox', { name: 'Description' })).toHaveProperty('value', 'Unsaved description draft');
    expect(mocks.setProjectBacklogTaskPosition).not.toHaveBeenCalled();
  });

  it('does not replace a newer capture graph with a delayed move response', async () => {
    const backlogTask = task('t1', 'Backlog task', { position: 0 });
    const readyTask = task('t2', 'Ready task', { state: 'ready', position: 0 });
    const movedGraph = graph([
      readyTask,
      task('t1', 'Backlog task', { state: 'ready', position: 1 }),
    ], 6);
    const capturedGraph = graph([
      ...movedGraph.tasks,
      task('t3', 'Captured work'),
    ], 7);
    const delayedMove = deferred<BacklogGraph>();
    mocks.readProjectBacklog
      .mockResolvedValueOnce(graph([backlogTask, readyTask], 5))
      .mockResolvedValueOnce(movedGraph);
    mocks.setProjectBacklogTaskPosition.mockReturnValue(delayedMove.promise);
    mocks.captureBacklogTask
      .mockRejectedValueOnce(new GatewayError(409, {
        issues: [{ code: 'staleGraphRevision', path: '', message: 'Stale.' }],
      }, 'Stale.'))
      .mockResolvedValueOnce(capturedGraph);
    renderBoard();

    const transfer = dragTransfer();
    fireEvent.dragStart(await screen.findByTestId('task-card-t1'), { dataTransfer: transfer });
    fireEvent.dragOver(screen.getByTestId('column-ready'), { dataTransfer: transfer });
    fireEvent.drop(screen.getByTestId('column-ready'), { dataTransfer: transfer });
    await waitFor(() => expect(mocks.setProjectBacklogTaskPosition).toHaveBeenCalled());

    fireEvent.change(screen.getByRole('textbox', { name: 'New task title' }), { target: { value: 'Captured work' } });
    fireEvent.click(within(screen.getByRole('region', { name: 'Intake' })).getByRole('button', { name: 'Add' }));
    expect(await screen.findByTestId('task-card-t3')).toBeTruthy();
    expect(mocks.captureBacklogTask).toHaveBeenCalledTimes(2);
    expect(mocks.captureBacklogTask.mock.calls[0][2].expectedGraphRevision).toBe(5);
    expect(mocks.captureBacklogTask.mock.calls[1][2].expectedGraphRevision).toBe(6);

    await act(async () => {
      delayedMove.resolve(movedGraph);
      await delayedMove.promise;
    });

    expect(screen.getByTestId('task-card-t3')).toBeTruthy();
    expect(screen.getByTestId('count-backlog').textContent).toBe('1');
  });

  it('reorders a dragged task within Backlog using the observed revisions', async () => {
    const first = task('t1', 'First task', { position: 0 });
    const second = task('t2', 'Second task', { position: 1 });
    mocks.readProjectBacklog.mockResolvedValue(graph([first, second], 5));
    mocks.setProjectBacklogTaskPosition.mockResolvedValue(graph([
      task('t2', 'Second task', { position: 0 }),
      task('t1', 'First task', { position: 1 }),
    ], 6));
    renderBoard();

    const transfer = dragTransfer();
    fireEvent.dragStart(await screen.findByTestId('task-card-t1'), { dataTransfer: transfer });
    fireEvent.dragOver(await screen.findByTestId('task-card-t2'), { dataTransfer: transfer });
    fireEvent.drop(screen.getByTestId('task-card-t2'), { clientY: 100, dataTransfer: transfer });

    await waitFor(() => expect(mocks.setProjectBacklogTaskPosition).toHaveBeenCalledWith(
      'token', 'p1', 't1',
      { state: 'backlog', position: 1, expectedGraphRevision: 5, expectedTaskRevision: 1 },
      'tenant-1',
    ));
    expect(await screen.findByText('Task reordered.')).toBeTruthy();
  });

  it('does not send a drag drop into the owner-controlled Active column', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([
      task('t1', 'Backlog task'),
      task('t2', 'Active task', { state: 'claimed' }),
    ], 5));
    renderBoard();

    const transfer = dragTransfer();
    fireEvent.dragStart(await screen.findByTestId('task-card-t1'), { dataTransfer: transfer });
    fireEvent.dragOver(screen.getByTestId('column-active'), { dataTransfer: transfer });
    fireEvent.drop(screen.getByTestId('column-active'), { dataTransfer: transfer });
    fireEvent.dragEnd(screen.getByTestId('task-card-t1'), { dataTransfer: transfer });

    expect(mocks.setProjectBacklogTaskPosition).not.toHaveBeenCalled();
  });

  it('computes insertion indexes when a state change leaves a position gap', async () => {
    const first = task('t1', 'First task', { position: 0 });
    const ready = task('t0', 'Already ready', { state: 'ready', position: 0 });
    const second = task('t2', 'Second task', { position: 2 });
    mocks.readProjectBacklog.mockResolvedValue(graph([
      first,
      ready,
      second,
    ], 5));
    mocks.setProjectBacklogTaskPosition.mockResolvedValue(graph([
      task('t2', 'Second task', { position: 0 }),
      task('t1', 'First task', { position: 1 }),
      ready,
    ], 6));
    renderBoard();

    fireEvent.click(await within(await screen.findByTestId('task-card-t1')).findByRole('button', { name: 'Move later' }));
    await waitFor(() => expect(mocks.setProjectBacklogTaskPosition).toHaveBeenCalledWith(
      'token', 'p1', 't1',
      { state: 'backlog', position: 1, expectedGraphRevision: 5, expectedTaskRevision: 1 },
      'tenant-1',
    ));
  });

  it('reads every issue page by following the cursor', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec'), task('t2', 'Fix bug')]));
    mocks.listProjectIssues
      .mockResolvedValueOnce({ projectId: 'p1', items: [{ taskId: 't1', taskState: 'backlog', providerId: 'github', repository: { owner: 'acme', name: 'web' }, number: 1, title: '', state: 'open', createdAt: '', updatedAt: '' }], nextCursor: 'c2' })
      .mockResolvedValueOnce({ projectId: 'p1', items: [{ taskId: 't2', taskState: 'backlog', providerId: 'github', repository: { owner: 'acme', name: 'web' }, number: 2, title: '', state: 'closed', createdAt: '', updatedAt: '' }], nextCursor: null });
    renderBoard();

    expect(await screen.findByText('Issue acme/web#2')).toBeTruthy();
    expect(screen.getByText('Closed')).toBeTruthy();
    expect(mocks.listProjectIssues).toHaveBeenLastCalledWith('token', 'p1', { state: 'all', cursor: 'c2', pageSize: 100 }, 'tenant-1');
  });

  it('shows the loading state, then the empty state', async () => {
    let resolve: (value: BacklogGraph) => void = () => undefined;
    mocks.readProjectBacklog.mockReturnValue(new Promise<BacklogGraph>((done) => { resolve = done; }));
    renderBoard();
    expect(await screen.findByText('Loading board')).toBeTruthy();
    resolve(graph([]));
    expect(await screen.findByText('No tasks yet')).toBeTruthy();
  });

  it('explains a 403 without offering a retry', async () => {
    mocks.readProjectBacklog.mockRejectedValue(new GatewayError(403, { code: 'backlog_project_permission_denied' }, 'Forbidden'));
    renderBoard();
    expect(await screen.findByText('No access to this board')).toBeTruthy();
    expect(screen.getByText("You don't have permission to view this project's board.")).toBeTruthy();
    expect(screen.queryByRole('button', { name: /try again/i })).toBeNull();
  });

  it('shows other read failures with a retry that reads again', async () => {
    mocks.readProjectBacklog.mockRejectedValueOnce(new GatewayError(503, {}, 'Unavailable')).mockResolvedValue(graph([task('t1', 'Draft spec')]));
    renderBoard();
    expect(await screen.findByText("Couldn't load the board")).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));
    expect(await screen.findByText('Draft spec')).toBeTruthy();
  });

  it('keeps the board when synced issues fail to load', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec', { repositoryIssue: issueLink })]));
    mocks.listProjectIssues.mockRejectedValue(new GatewayError(500, {}, 'Failed'));
    renderBoard();
    expect(await screen.findByText('Draft spec')).toBeTruthy();
    expect(screen.getByText("Linked issue details couldn't be loaded, so cards show without issue state.")).toBeTruthy();
    // The link stored on the task still names the issue.
    expect(screen.getByText('Issue acme/web#12')).toBeTruthy();
  });

  it('captures a task from Intake with the loaded graph revision', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([], 5));
    mocks.captureBacklogTask.mockImplementation(async (_token: string, _projectId: string, request: { taskId: string; content: { title: string } }) =>
      graph([task(request.taskId, request.content.title)], 6));
    renderBoard();

    const input = await screen.findByRole('textbox', { name: 'New task title' });
    fireEvent.change(input, { target: { value: '  Write release notes  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    expect(await within(screen.getByTestId('column-backlog')).findByText('Write release notes')).toBeTruthy();
    expect(screen.getByText('Task added to Backlog.')).toBeTruthy();
    const [, projectId, request] = mocks.captureBacklogTask.mock.calls[0];
    expect(projectId).toBe('p1');
    expect(request.content).toEqual({ title: 'Write release notes' });
    expect(request.expectedGraphRevision).toBe(5);
    expect(request.taskId).toMatch(/^[A-Za-z0-9][A-Za-z0-9._:-]*$/);
  });

  it('retries a stale capture once with a fresh revision and the same task ID', async () => {
    mocks.readProjectBacklog.mockResolvedValueOnce(graph([], 5)).mockResolvedValue(graph([], 8));
    mocks.captureBacklogTask
      .mockRejectedValueOnce(new GatewayError(409, { issues: [{ code: 'staleGraphRevision', path: '', message: 'Stale.' }] }, 'Stale.'))
      .mockImplementation(async (_token: string, _projectId: string, request: { taskId: string; content: { title: string } }) =>
        graph([task(request.taskId, request.content.title)], 9));
    renderBoard();

    fireEvent.change(await screen.findByRole('textbox', { name: 'New task title' }), { target: { value: 'Retry me' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('Retry me')).toBeTruthy();
    const [first, second] = mocks.captureBacklogTask.mock.calls.map((call) => call[2]);
    expect(first.expectedGraphRevision).toBe(5);
    expect(second.expectedGraphRevision).toBe(8);
    expect(second.taskId).toBe(first.taskId);
  });

  it('shows a plain message when capture is not allowed', async () => {
    mocks.captureBacklogTask.mockRejectedValue(new GatewayError(403, { code: 'backlog_project_permission_denied' }, 'Forbidden'));
    renderBoard();
    fireEvent.change(await screen.findByRole('textbox', { name: 'New task title' }), { target: { value: 'Nope' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    expect(await screen.findByText("You don't have permission to change tasks in this project.")).toBeTruthy();
  });

  it('edits a task title and description with both revisions and unchanged link', async () => {
    const original = task('t1', 'Draft spec', { revision: 3, repositoryIssue: issueLink, description: 'Old' });
    mocks.readProjectBacklog.mockResolvedValue(graph([original], 5));
    mocks.editBacklogTaskContent.mockResolvedValue(graph([{ ...original, revision: 4, title: 'Final spec', description: null }], 6));
    renderBoard();

    fireEvent.click(await screen.findByRole('button', { name: 'Edit task Draft spec' }));
    fireEvent.change(screen.getByRole('textbox', { name: /Title/ }), { target: { value: 'Final spec' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Description' }), { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Final spec')).toBeTruthy();
    expect(mocks.editBacklogTaskContent).toHaveBeenCalledWith('token', 'p1', 't1', {
      content: { title: 'Final spec', description: null, lifecycle: 'open', repositoryIssue: issueLink },
      expectedGraphRevision: 5,
      expectedTaskRevision: 3,
    }, 'tenant-1');
  });

  it('changes queue state, manages prerequisites, and archives with observed revisions', async () => {
    const first = task('t1', 'First task');
    const second = task('t2', 'Second task');
    mocks.readProjectBacklog.mockResolvedValue(graph([first, second], 5));
    mocks.setProjectBacklogTaskState.mockResolvedValue(graph([{ ...first, revision: 2, state: 'ready' }, second], 6));
    mocks.addProjectBacklogDependency.mockResolvedValue(graph(
      [{ ...first, revision: 2, state: 'ready' }, second],
      7,
      [{ task: first.reference, prerequisite: second.reference }],
    ));
    mocks.removeProjectBacklogDependency.mockResolvedValue(graph(
      [{ ...first, revision: 2, state: 'ready' }, second],
      8,
    ));
    mocks.archiveProjectBacklogTask.mockResolvedValue(graph(
      [{ ...first, revision: 3, state: 'ready', isArchived: true }, second],
      9,
    ));
    renderBoard();

    const card = await screen.findByTestId('task-card-t1');
    await act(async () => {
      fireEvent.click(within(card).getByRole('button', { name: 'Mark Ready' }));
      await mocks.setProjectBacklogTaskState.mock.results[0]?.value;
    });
    expect(mocks.setProjectBacklogTaskState).toHaveBeenCalledOnce();
    expect(await within(await screen.findByTestId('task-card-t1')).findByRole('button', { name: 'Move to Backlog' })).toBeTruthy();
    expect(mocks.setProjectBacklogTaskState).toHaveBeenCalledWith('token', 'p1', 't1', {
      state: 'ready',
      expectedGraphRevision: 5,
      expectedTaskRevision: 1,
    }, 'tenant-1');

    const readyCard = screen.getByTestId('task-card-t1');
    fireEvent.change(within(readyCard).getByRole('combobox', { name: 'Add prerequisite' }), { target: { value: 't2' } });
    fireEvent.click(within(readyCard).getByRole('button', { name: 'Add' }));
    expect(await within(await screen.findByTestId('task-card-t1')).findByText('Needs Second task')).toBeTruthy();
    expect(mocks.addProjectBacklogDependency).toHaveBeenCalledWith('token', 'p1', 't1', 't2', {
      expectedGraphRevision: 6,
    }, 'tenant-1');

    fireEvent.click(within(screen.getByTestId('task-card-t1')).getByRole('button', {
      name: 'Remove prerequisite Second task from First task',
    }));
    await waitFor(() => expect(mocks.removeProjectBacklogDependency).toHaveBeenCalledWith(
      'token', 'p1', 't1', 't2', { expectedGraphRevision: 7 }, 'tenant-1',
    ));
    expect(await screen.findByText('Prerequisite removed.')).toBeTruthy();

    fireEvent.click(within(screen.getByTestId('task-card-t1')).getByRole('button', { name: 'Archive' }));
    await waitFor(() => expect(mocks.archiveProjectBacklogTask).toHaveBeenCalledWith('token', 'p1', 't1', {
      expectedGraphRevision: 8,
      expectedTaskRevision: 2,
    }, 'tenant-1'));
    expect(await screen.findByText('Task archived.')).toBeTruthy();
    expect(screen.queryByTestId('task-card-t1')).toBeNull();
  });

  it('hides prerequisite edits for claimed tasks and pending prerequisites', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([
      task('t1', 'Claimed task', { state: 'claimed' }),
      task('t2', 'Existing prerequisite'),
      task('t3', 'Pending prerequisite', { isAutomationInvocationPending: true }),
      task('t4', 'Available prerequisite'),
      task('t5', 'Editable task', { state: 'ready' }),
    ], 5, [{
      task: { projectId: 'p1', taskId: 't1' },
      prerequisite: { projectId: 'p1', taskId: 't2' },
    }]));
    renderBoard();

    const claimedCard = await screen.findByTestId('task-card-t1');
    expect(within(claimedCard).getByText('Needs Existing prerequisite')).toBeTruthy();
    expect(within(claimedCard).queryByRole('button', {
      name: 'Remove prerequisite Existing prerequisite from Claimed task',
    })).toBeNull();

    const editableCard = screen.getByTestId('task-card-t5');
    const select = within(editableCard).getByRole('combobox', { name: 'Add prerequisite' });
    expect(within(select).queryByRole('option', { name: 'Pending prerequisite' })).toBeNull();
    expect(within(select).getByRole('option', { name: 'Available prerequisite' })).toBeTruthy();
  });

  it('reloads the board when an edit conflicts', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec')], 5));
    mocks.editBacklogTaskContent.mockRejectedValue(new GatewayError(409, { issues: [{ code: 'taskNotEditable', path: '', message: 'Not editable.' }] }, 'Not editable.'));
    renderBoard();

    fireEvent.click(await screen.findByRole('button', { name: 'Edit task Draft spec' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('This task can no longer be edited. It may be archived or waiting for an automation.')).toBeTruthy();
    await waitFor(() => expect(mocks.readProjectBacklog).toHaveBeenCalledTimes(2));
  });

  it('hides edit while an automation is pending', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Busy task', { isAutomationInvocationPending: true })]));
    renderBoard();
    expect(await screen.findByText('Busy task')).toBeTruthy();
    expect(screen.getByText('Automation running')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit task Busy task' })).toBeNull();
  });

  it('shows a read-only board without capture or edit for viewers', async () => {
    mocks.authorizationData = context(['readProjects']);
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec')]));
    renderBoard();
    expect(await screen.findByText('Draft spec')).toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'New task title' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit task Draft spec' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete task Draft spec' })).toBeNull();
  });

  it('offers delete only for eligible tasks and explains loaded blockers, including hidden dependents', async () => {
    const graphWithTasks = graph([
      task('eligible', 'Eligible'),
      task('closed', 'Closed unclaimed', { lifecycle: 'closed' }),
      task('claimed', 'Claimed', { state: 'claimed' }),
      task('held', 'Automation held', { isAutomationInvocationPending: true }),
      task('archived', 'Archived', { isArchived: true }),
      task('parent', 'Has a dependent'),
      task('dependent', 'Hidden dependent', { isArchived: true }),
      task('linked', 'Linked issue', { repositoryIssue: issueLink }),
    ], 5, [{
      task: { projectId: 'p1', taskId: 'dependent' },
      prerequisite: { projectId: 'p1', taskId: 'parent' },
    }]);
    mocks.readProjectBacklog.mockResolvedValue(graphWithTasks);
    renderBoard();

    expect(await screen.findByRole('button', { name: 'Delete task Eligible' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Delete task Closed unclaimed' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Delete task Claimed' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete task Automation held' })).toBeNull();
    expect(screen.queryByText('Archived')).toBeNull();

    const dependentCard = screen.getByTestId('task-card-parent');
    expect(within(dependentCard).getByRole('button', { name: 'Delete task Has a dependent' }).hasAttribute('disabled')).toBe(true);
    expect(within(dependentCard).getByText('Another task depends on this task. Remove its prerequisite link first.')).toBeTruthy();
    const linkedCard = screen.getByTestId('task-card-linked');
    expect(within(linkedCard).getByRole('button', { name: 'Delete task Linked issue' }).hasAttribute('disabled')).toBe(true);
    expect(within(linkedCard).getByText("This task is linked to a repository issue and can't be deleted here.")).toBeTruthy();
  });

  it('cancels or escapes confirmation without a request and restores focus', async () => {
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec')]));
    renderBoard();
    const card = await screen.findByTestId('task-card-t1');
    const trigger = within(card).getByRole('button', { name: 'Delete task Draft spec' });

    fireEvent.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Delete task?' });
    const cancel = within(dialog).getByRole('button', { name: 'Cancel' });
    await waitFor(() => expect(document.activeElement).toBe(cancel));
    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Delete task?' })).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
    expect(mocks.deleteProjectBacklogTask).not.toHaveBeenCalled();

    fireEvent.click(trigger);
    fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' })).findByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Delete task?' })).toBeNull());
    expect(mocks.deleteProjectBacklogTask).not.toHaveBeenCalled();
    expect(screen.getByTestId('task-card-t1')).toBeTruthy();
  });

  it('sends observed revisions once, keeps the task while pending, and replaces it with the owner graph', async () => {
    const target = task('t1', 'Delete me', { revision: 3 });
    const prerequisite = task('t2', 'Keep me', { position: 1 });
    const initial = graph([target, prerequisite], 5, [{
      task: target.reference,
      prerequisite: prerequisite.reference,
    }]);
    const authoritative = graph([task('t2', 'Keep me', { position: 0 })], 6);
    const response = deferred<BacklogGraph>();
    mocks.readProjectBacklog.mockResolvedValue(initial);
    mocks.deleteProjectBacklogTask.mockReturnValue(response.promise);
    renderBoard();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete task Delete me' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete task?' });
    expect(within(dialog).getByText(/Delete “Delete me”\?.*permanently removes the task and its prerequisite links/s)).toBeTruthy();
    expect(within(dialog).getByText(/does not delete a repository issue/i)).toBeTruthy();
    const submit = within(dialog).getByRole('button', { name: 'Delete task' });
    fireEvent.click(submit);
    fireEvent.click(submit);

    await waitFor(() => expect(mocks.deleteProjectBacklogTask).toHaveBeenCalledOnce());
    expect(mocks.deleteProjectBacklogTask).toHaveBeenCalledWith('token', 'p1', 't1', {
      expectedGraphRevision: 5,
      expectedTaskRevision: 3,
    }, 'tenant-1');
    expect(screen.getByTestId('task-card-t1')).toBeTruthy();
    expect(submit.hasAttribute('disabled')).toBe(true);
    fireEvent.keyDown(dialog, { key: 'Escape' });
    expect(screen.getByRole('dialog', { name: 'Delete task?' })).toBeTruthy();

    await act(async () => {
      response.resolve(authoritative);
      await response.promise;
    });
    await waitFor(() => expect(screen.queryByTestId('task-card-t1')).toBeNull());
    expect(await screen.findByText('Task deleted.')).toBeTruthy();
    expect(screen.getByTestId('task-card-t2')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Agent task board' })).toBe(document.activeElement);
  });

  it('does not let a refresh started before deletion restore the removed task', async () => {
    const target = task('t1', 'Delete me', { revision: 3 });
    const initial = graph([target], 5);
    const staleRefresh = deferred<BacklogGraph>();
    const authoritative = graph([], 6);
    mocks.readProjectBacklog.mockResolvedValueOnce(initial).mockReturnValueOnce(staleRefresh.promise);
    mocks.deleteProjectBacklogTask.mockResolvedValue(authoritative);
    renderBoard();

    await screen.findByRole('button', { name: 'Delete task Delete me' });
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(mocks.readProjectBacklog).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('button', { name: 'Delete task Delete me' }));
    fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' }))
      .findByRole('button', { name: 'Delete task' }));

    expect(mocks.deleteProjectBacklogTask).toHaveBeenCalledWith('token', 'p1', 't1', {
      expectedGraphRevision: 5,
      expectedTaskRevision: 3,
    }, 'tenant-1');
    expect(await screen.findByText('Task deleted.')).toBeTruthy();
    expect(screen.queryByTestId('task-card-t1')).toBeNull();

    await act(async () => {
      staleRefresh.resolve(initial);
      await staleRefresh.promise;
      await Promise.resolve();
    });
    expect(screen.queryByTestId('task-card-t1')).toBeNull();
    expect(screen.getByText('Task deleted.')).toBeTruthy();
  });

  it.each([
    {
      title: 'forbidden deletion',
      error: new GatewayError(403, {}, 'Forbidden'),
      message: "You don't have permission to delete tasks in this project.",
      remainsOpen: true,
    },
    {
      title: 'owner deletion-unavailable rejection',
      error: new GatewayError(409, {
        issues: [{ code: 'taskDeletionUnavailable', path: 'task', message: 'A repository receipt is still active.' }],
      }, 'A repository receipt is still active.'),
      message: 'A repository receipt is still active.',
      remainsOpen: true,
    },
    {
      title: 'dependent task rejection',
      error: new GatewayError(409, {
        issues: [{ code: 'taskHasDependents', path: 'task', message: 'A dependent task exists.' }],
      }, 'A dependent task exists.'),
      message: 'Another task depends on this task. Remove its prerequisite link before deleting it.',
      remainsOpen: false,
    },
    {
      title: 'stale revision rejection',
      error: new GatewayError(409, { issues: [{ code: 'staleGraphRevision', path: '', message: 'Stale.' }] }, 'Stale.'),
      message: 'The board changed while you were working. It has been reloaded; try again.',
      remainsOpen: false,
    },
    {
      title: 'missing task rejection',
      error: new GatewayError(404, {}, 'Missing'),
      message: 'This task no longer exists. The board has been reloaded.',
      remainsOpen: false,
      remainsVisible: false,
    },
    {
      title: 'service unavailable rejection',
      error: new GatewayError(503, {}, 'Unavailable'),
      message: 'Task deletion is unavailable right now. The task has not been removed. Try again later.',
      remainsOpen: true,
    },
  ])('preserves task-delete error semantics for $title', async ({ error, message, remainsOpen, remainsVisible = true }) => {
    const initial = graph([task('t1', 'Draft spec')]);
    if (remainsVisible) mocks.readProjectBacklog.mockResolvedValue(initial);
    else mocks.readProjectBacklog.mockResolvedValueOnce(initial).mockResolvedValue(graph([], 6));
    mocks.deleteProjectBacklogTask.mockRejectedValue(error);
    renderBoard();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete task Draft spec' }));
    fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' })).findByRole('button', { name: 'Delete task' }));
    expect((await screen.findByText(message)).textContent).toContain(message);
    if (remainsVisible) expect(screen.getByTestId('task-card-t1')).toBeTruthy();
    else await waitFor(() => expect(screen.queryByTestId('task-card-t1')).toBeNull());
    expect(screen.queryByText(/Queue ordering is unavailable/)).toBeNull();
    if (remainsOpen) expect(screen.getByRole('dialog', { name: 'Delete task?' })).toBeTruthy();
    else expect(screen.queryByRole('dialog', { name: 'Delete task?' })).toBeNull();
  });

  it('invalidates an open confirmation when the observed graph revision changes', async () => {
    mocks.readProjectBacklog
      .mockResolvedValueOnce(graph([task('t1', 'Draft spec')], 5))
      .mockResolvedValue(graph([task('t1', 'Draft spec', { revision: 2 })], 6));
    renderBoard();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete task Draft spec' }));
    expect(await screen.findByRole('dialog', { name: 'Delete task?' })).toBeTruthy();
    await act(async () => {
      screen.getByRole('button', { name: 'Refresh' }).click();
    });

    expect(await screen.findByText('The board changed while you were confirming. Review the task before deleting it.')).toBeTruthy();
    expect(screen.queryByRole('dialog', { name: 'Delete task?' })).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Agent task board' })));
    expect(mocks.deleteProjectBacklogTask).not.toHaveBeenCalled();
  });

  it.each(['session token', 'actor', 'tenant', 'membership', 'permission'])(
    'blocks a delayed delete dispatch after a %s change',
    async (change) => {
      mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec')]));
      const dispatchGate = deferred<void>();
      let markStarted!: () => void;
      const started = new Promise<void>((resolve) => { markStarted = resolve; });
      mocks.deleteProjectBacklogTask.mockResolvedValue(graph([], 6));
      const page = renderBoard();
      const trigger = await screen.findByRole('button', { name: 'Delete task Draft spec' });
      mocks.apiCall.mockImplementationOnce(async (operation: (token: string, tenant: string) => Promise<unknown>) => {
        markStarted();
        await dispatchGate.promise;
        return operation(mocks.session.accessToken, mocks.authorizationContext.tenantId);
      });

      fireEvent.click(trigger);
      fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' })).findByRole('button', { name: 'Delete task' }));
      await started;
      changeAuthorizationScope(change);
      page.rerenderBoard();
      await screen.findByText('Draft spec');

      await act(async () => {
        dispatchGate.resolve();
        await dispatchGate.promise;
      });
      await waitFor(() => expect(mocks.deleteProjectBacklogTask).not.toHaveBeenCalled());
      expect(screen.getByTestId('task-card-t1')).toBeTruthy();
    },
  );

  it('blocks a delayed delete after navigating A to B to A', async () => {
    mocks.readProjectBacklog.mockImplementation(async (_token: string, projectId: string) =>
      graph([task('t1', 'Draft spec')], 5, [], projectId));
    const dispatchGate = deferred<void>();
    let markStarted!: () => void;
    const started = new Promise<void>((resolve) => { markStarted = resolve; });
    mocks.deleteProjectBacklogTask.mockResolvedValue(graph([], 6));
    const page = renderBoard();
    const trigger = await screen.findByRole('button', { name: 'Delete task Draft spec' });
    mocks.apiCall.mockImplementationOnce(async (operation: (token: string, tenant: string) => Promise<unknown>) => {
      markStarted();
      await dispatchGate.promise;
      return operation(mocks.session.accessToken, mocks.authorizationContext.tenantId);
    });

    fireEvent.click(trigger);
    fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' })).findByRole('button', { name: 'Delete task' }));
    await started;
    page.navigate('/projects/p2/board');
    await screen.findByText('Draft spec');
    page.navigate('/projects/p1/board');
    await screen.findByText('Draft spec');

    await act(async () => {
      dispatchGate.resolve();
      await dispatchGate.promise;
    });
    await waitFor(() => expect(mocks.deleteProjectBacklogTask).not.toHaveBeenCalled());
    expect(screen.getByTestId('task-card-t1')).toBeTruthy();
  });

  it.each(['session token', 'actor', 'tenant', 'membership', 'permission'])(
    'ignores a late delete response after a %s change',
    async (change) => {
      const oldTask = task('t1', 'Draft spec');
      mocks.readProjectBacklog
        .mockResolvedValueOnce(graph([oldTask], 5))
        .mockResolvedValue(graph([task('t1', 'Fresh scope task', { revision: 2 })], 6));
      const response = deferred<BacklogGraph>();
      mocks.deleteProjectBacklogTask.mockReturnValue(response.promise);
      const page = renderBoard();

      fireEvent.click(await screen.findByRole('button', { name: 'Delete task Draft spec' }));
      fireEvent.click(await within(await screen.findByRole('dialog', { name: 'Delete task?' })).findByRole('button', { name: 'Delete task' }));
      await waitFor(() => expect(mocks.deleteProjectBacklogTask).toHaveBeenCalledOnce());
      changeAuthorizationScope(change);
      page.rerenderBoard();
      expect(await screen.findByText('Fresh scope task')).toBeTruthy();

      await act(async () => {
        response.resolve(graph([], 20));
        await response.promise;
      });
      expect(screen.getByTestId('task-card-t1')).toBeTruthy();
      expect(screen.getByText('Fresh scope task')).toBeTruthy();
      expect(screen.queryByText('Task deleted.')).toBeNull();
    },
  );

  it('drops a Board read from the previous session when a fresh scope loads', async () => {
    const oldRead = deferred<BacklogGraph>();
    mocks.readProjectBacklog
      .mockImplementationOnce(() => oldRead.promise)
      .mockResolvedValue(graph([task('t1', 'Fresh session task')], 6));
    const page = renderBoard();
    await waitFor(() => expect(mocks.readProjectBacklog).toHaveBeenCalledOnce());

    mocks.session.accessToken = 'new-session-token';
    page.rerenderBoard();
    expect(await screen.findByText('Fresh session task')).toBeTruthy();
    await act(async () => {
      oldRead.resolve(graph([task('t2', 'Old session task')], 5));
      await oldRead.promise;
    });

    expect(screen.getByText('Fresh session task')).toBeTruthy();
    expect(screen.queryByText('Old session task')).toBeNull();
  });
});

function changeAuthorizationScope(change: string): void {
  switch (change) {
    case 'session token':
      mocks.session.accessToken = 'new-session-token';
      break;
    case 'actor':
      mocks.authorizationData = context(['readProjects', 'writeProjects'], { actorId: 'user-2' });
      break;
    case 'tenant':
      mocks.authorizationContext = { tenantId: 'tenant-2', membershipRevision: 1 };
      mocks.authorizationData = context(['readProjects', 'writeProjects'], { tenantId: 'tenant-2' });
      break;
    case 'membership':
      mocks.authorizationContext = { tenantId: 'tenant-1', membershipRevision: 2 };
      mocks.authorizationData = context(['readProjects', 'writeProjects'], { membershipRevision: 2 });
      break;
    case 'permission':
      mocks.authorizationData = context(['readProjects']);
      break;
    default:
      throw new Error(`Unknown test scope change: ${change}`);
  }
}
