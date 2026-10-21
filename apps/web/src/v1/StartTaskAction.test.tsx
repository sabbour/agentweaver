import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { StartTaskAction } from './StartTaskAction';

const mocks = vi.hoisted(() => ({
  apiCall: vi.fn(),
  readProjectBacklog: vi.fn(),
  captureBacklogTask: vi.fn(),
  getAuthorizationContext: vi.fn(),
}));

vi.mock('./AuthContext', () => ({
  useAuth: () => ({ session: { accessToken: 'token' }, apiCall: mocks.apiCall }),
}));

vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    readProjectBacklog: mocks.readProjectBacklog,
    captureBacklogTask: mocks.captureBacklogTask,
    getAuthorizationContext: mocks.getAuthorizationContext,
  },
}));

function authorization(permissions: string[]) {
  return {
    contractVersion: 1, issuer: 'https://broker.test', actorId: 'user-1', tenantId: 'tenant-1', membershipRevision: 1,
    boundProjectId: null, boundRunId: null,
    effectiveAuthority: [{ resourceType: 'project', resourceId: 'p1', permissions: permissions.map((permission) => ({ permission, roleRevision: 1 })) }],
  };
}

function BoardProbe() {
  const location = useLocation();
  return <p>Board for {String((location.state as { capturedTaskTitle?: string } | null)?.capturedTaskTitle)}</p>;
}

function renderAction() {
  return render(
    <MemoryRouter initialEntries={['/projects/p1']}>
      <Routes>
        <Route path="/projects/p1" element={<StartTaskAction projectId="p1" projectName="Alpha" />} />
        <Route path="/projects/p1/board" element={<BoardProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.apiCall.mockReset().mockImplementation((operation: (token: string, tenant: string) => Promise<unknown>) => operation('token', 'tenant-1'));
  mocks.readProjectBacklog.mockReset().mockResolvedValue({ projectId: 'p1', revision: 2, tasks: [], dependencies: [] });
  mocks.captureBacklogTask.mockReset().mockResolvedValue({ projectId: 'p1', revision: 3, tasks: [], dependencies: [] });
  mocks.getAuthorizationContext.mockReset().mockResolvedValue(authorization(['readProjects', 'writeProjects']));
});

describe('StartTaskAction', () => {
  it('captures a task into the project backlog and opens the Board', async () => {
    renderAction();
    fireEvent.click(await screen.findByRole('button', { name: 'Start task' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Start a task')).toBeTruthy();
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'New task title' }), { target: { value: 'Write docs' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('Board for Write docs')).toBeTruthy();
    // No graph is loaded in the shell, so the action reads the revision first.
    expect(mocks.readProjectBacklog).toHaveBeenCalledWith('token', 'p1', 'tenant-1');
    expect(mocks.captureBacklogTask).toHaveBeenCalledWith('token', 'p1', expect.objectContaining({
      content: { title: 'Write docs' }, expectedGraphRevision: 2,
    }), 'tenant-1');
  });

  it('keeps the dialog open with a plain message when capture fails', async () => {
    mocks.captureBacklogTask.mockRejectedValue(new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    renderAction();
    fireEvent.click(await screen.findByRole('button', { name: 'Start task' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'New task title' }), { target: { value: 'Write docs' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));
    expect(await within(dialog).findByText("You don't have permission to change tasks in this project.")).toBeTruthy();
    expect(screen.queryByText(/Board for/)).toBeNull();
  });

  it('renders nothing for users who cannot change the project', async () => {
    mocks.getAuthorizationContext.mockResolvedValue(authorization(['readProjects']));
    const { container } = renderAction();
    await waitFor(() => expect(mocks.getAuthorizationContext).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: 'Start task' })).toBeNull();
    expect(container.textContent).toBe('');
  });
});
