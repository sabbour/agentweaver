import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
  mocks.getAuthorizationContext.mockReset().mockResolvedValue(context(['readProjects', 'writeProjects']));
  mocks.getProject.mockReset().mockResolvedValue({ projectId: 'p1', name: 'Alpha' });
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
    // Moves have no service operation, so nothing is draggable and the board says so.
    expect(screen.getByTestId('board-move-unavailable').textContent).toBe('Moving tasks between columns is not available yet.');
    expect(document.querySelector('[draggable="true"]')).toBeNull();
    expect(mocks.listProjectIssues).toHaveBeenCalledWith('token', 'p1', { state: 'all', cursor: null, pageSize: 100 }, 'tenant-1');
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
