import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
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
  getAuthorizationContext: vi.fn(),
  getProject: vi.fn(),
  apiCall: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ session: { accessToken: 'token' }, apiCall: mocks.apiCall }),
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

function graph(tasks: BacklogTaskSnapshot[], revision = 5, dependencies: BacklogGraph['dependencies'] = []): BacklogGraph {
  return { projectId: 'p1', revision, tasks, dependencies };
}

function context(permissions: Array<'readProjects' | 'writeProjects'>): ProjectAuthorizationContextResponse {
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
  } as ProjectAuthorizationContextResponse;
}

const issueLink = { providerId: 'github', repository: { owner: 'acme', name: 'web' }, issueNumber: 12 };

function renderBoard() {
  return render(
    <MemoryRouter initialEntries={['/projects/p1/board']}>
      <Routes>
        <Route path="/projects/:projectId/board" element={<BoardPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.apiCall.mockReset().mockImplementation((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
  mocks.readProjectBacklog.mockReset().mockResolvedValue(graph([]));
  mocks.listProjectIssues.mockReset().mockResolvedValue({ projectId: 'p1', items: [], nextCursor: null });
  mocks.captureBacklogTask.mockReset();
  mocks.editBacklogTaskContent.mockReset();
  mocks.setProjectBacklogTaskState.mockReset();
  mocks.setProjectBacklogTaskPosition.mockReset();
  mocks.setAllProjectBacklogTasksReady.mockReset();
  mocks.archiveProjectBacklogTask.mockReset();
  mocks.addProjectBacklogDependency.mockReset();
  mocks.removeProjectBacklogDependency.mockReset();
  mocks.getAuthorizationContext.mockReset().mockResolvedValue(context(['readProjects', 'writeProjects']));
  mocks.getProject.mockReset().mockResolvedValue({ projectId: 'p1', name: 'Alpha', effectiveRoles: [] });
});

describe('BoardPage', () => {
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
    // Queue actions are available on cards; drag and drop remains unavailable.
    expect(screen.getByTestId('board-move-unavailable').textContent).toBe('Drag and drop is unavailable. Use task actions to change queue and order.');
    expect(document.querySelector('[draggable="true"]')).toBeNull();
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
    mocks.getAuthorizationContext.mockResolvedValue(context(['readProjects']));
    mocks.readProjectBacklog.mockResolvedValue(graph([task('t1', 'Draft spec')]));
    renderBoard();
    expect(await screen.findByText('Draft spec')).toBeTruthy();
    await waitFor(() => expect(mocks.getAuthorizationContext).toHaveBeenCalled());
    expect(screen.queryByRole('textbox', { name: 'New task title' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit task Draft spec' })).toBeNull();
  });
});
