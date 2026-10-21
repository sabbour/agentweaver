import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { NewSessionPage } from './NewSessionPage';
import { PENDING_RUN_STORAGE_KEY, readPendingRun, savePendingRun } from './pendingRun';

const mocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  getProjectConfiguration: vi.fn(),
  createProjectRun: vi.fn(),
  startProjectRun: vi.fn(),
  ensureSession: vi.fn(),
  authorize: vi.fn(),
  apiCall: vi.fn(),
  session: null as { binding: { projectId: string; runId: string } | null } | null,
}));

vi.mock('./api', async () => {
  const actual = await vi.importActual<typeof import('./api')>('./api');
  return {
    ...actual,
    gatewayClient: {
      listProjects: mocks.listProjects,
      getProjectConfiguration: mocks.getProjectConfiguration,
      createProjectRun: mocks.createProjectRun,
      startProjectRun: mocks.startProjectRun,
    },
  };
});

vi.mock('./AuthContext', () => ({
  useAuth: () => ({
    session: mocks.session,
    apiCall: mocks.apiCall,
    authorize: mocks.authorize,
    ensureSession: mocks.ensureSession,
  }),
}));

vi.mock('./pageParts', async () => {
  const actual = await vi.importActual<typeof import('./pageParts')>('./pageParts');
  return { ...actual, PageHeading: ({ title }: { title: string }) => <h1>{title}</h1> };
});

function project(projectId: string, name: string) {
  return { projectId, name, state: 'active', revision: 1, configurationRevision: 7, createdAt: '', updatedAt: '' };
}

/** Shows the current path so tests can check navigation. */
function Where() {
  const location = useLocation();
  return <output data-testid="where">{`${location.pathname}${location.search}`}</output>;
}

function renderPage(path = '/assistant?project=p2') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/assistant" element={<NewSessionPage />} />
        <Route path="*" element={null} />
      </Routes>
      <Where />
    </MemoryRouter>,
  );
}

async function send(text: string) {
  fireEvent.change(screen.getByRole('textbox', { name: 'Message' }), { target: { value: text } });
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Start chat' })); });
}

describe('NewSessionPage', () => {
  beforeEach(() => {
    sessionStorage.clear();
    mocks.session = { binding: null };
    mocks.listProjects.mockReset().mockResolvedValue([project('p1', 'Alpha'), project('p2', 'Beta')]);
    mocks.getProjectConfiguration.mockReset().mockResolvedValue({ projectId: 'p2', revision: 9 });
    mocks.createProjectRun.mockReset().mockResolvedValue({
      runId: 'r1', rootSessionId: 's1', state: 'awaiting_binding', binding: { projectId: 'p2', runId: 'r1' },
    });
    mocks.startProjectRun.mockReset().mockResolvedValue({ runId: 'r1', rootSessionId: 's1', state: 'started' });
    mocks.ensureSession.mockReset().mockResolvedValue(true);
    mocks.authorize.mockReset().mockResolvedValue(undefined);
    // Records which session each call used: null for unbound, or the run binding.
    mocks.apiCall.mockReset().mockImplementation(
      (operation: (token: string, tenant: string | null) => Promise<unknown>, binding: unknown) =>
        operation(binding ? 'run-token' : 'unbound-token', 'tenant-1'),
    );
  });

  afterEach(() => sessionStorage.clear());

  it('creates, binds, and starts a chat, then opens its Chat tab', async () => {
    renderPage();
    const picker = await screen.findByRole('combobox', { name: /Project/ }) as HTMLSelectElement;
    expect(picker.value).toBe('p2');
    fireEvent.change(screen.getByRole('textbox', { name: /Title/ }), { target: { value: ' Plan ' } });
    await send('Hello team');
    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/projects/p2/runs/r1?view=chat'));
    expect(mocks.createProjectRun).toHaveBeenCalledWith(
      'unbound-token', 'p2', { firstMessage: 'Hello team', expectedProjectConfigRevision: 9, title: 'Plan' }, expect.any(String), 'tenant-1',
    );
    expect(mocks.ensureSession).toHaveBeenCalledWith({ projectId: 'p2', runId: 'r1' });
    expect(mocks.startProjectRun).toHaveBeenCalledWith('run-token', 'p2', 'r1', 'tenant-1');
    expect(readPendingRun()).toBeNull();
  });

  it('shows plain copy when the Orchestrator role is missing', async () => {
    mocks.createProjectRun.mockRejectedValue(new GatewayError(403, { code: 'project_orchestrator_role_required_acceptRunSelection' }, 'denied'));
    renderPage();
    await screen.findByRole('combobox', { name: /Project/ });
    await send('Hello');
    expect(await screen.findByText(/You need the Orchestrator role in this project to start a chat/)).toBeTruthy();
    expect(mocks.startProjectRun).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(PENDING_RUN_STORAGE_KEY)).toBeNull();
  });

  it('sends a retry after a settings conflict with a new key', async () => {
    mocks.createProjectRun.mockRejectedValueOnce(new GatewayError(409, { code: 'run_selection_conflict' }, 'stale'));
    renderPage();
    await screen.findByRole('combobox', { name: /Project/ });
    await send('Hello');
    expect(await screen.findByText(/project settings changed/)).toBeTruthy();
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Start chat' })); });
    await waitFor(() => expect(mocks.createProjectRun).toHaveBeenCalledTimes(2));
    expect(mocks.getProjectConfiguration).toHaveBeenCalledTimes(2);
    expect(mocks.createProjectRun.mock.calls[1][3]).not.toBe(mocks.createProjectRun.mock.calls[0][3]);
  });

  it('asks for sign-in when the run session needs the user, then continues', async () => {
    mocks.ensureSession.mockImplementation(async (binding: unknown) => binding === null);
    const view = renderPage();
    await screen.findByRole('combobox', { name: /Project/ });
    await send('Hello');
    const signIn = await screen.findByRole('button', { name: 'Sign in' });
    expect(readPendingRun()).toMatchObject({ projectId: 'p2', runId: 'r1' });
    expect(mocks.startProjectRun).not.toHaveBeenCalled();
    fireEvent.click(signIn);
    expect(mocks.authorize).toHaveBeenCalledWith({ projectId: 'p2', runId: 'r1' });
    // The manual sign-in makes the run session active; the page then starts the run.
    mocks.ensureSession.mockResolvedValue(true);
    mocks.session = { binding: { projectId: 'p2', runId: 'r1' } };
    view.rerender(
      <MemoryRouter initialEntries={['/assistant?project=p2']}>
        <Routes><Route path="/assistant" element={<NewSessionPage />} /><Route path="*" element={null} /></Routes>
        <Where />
      </MemoryRouter>,
    );
    await waitFor(() => expect(mocks.startProjectRun).toHaveBeenCalledTimes(1));
    expect(mocks.ensureSession).toHaveBeenCalledTimes(3);
  });

  it('resumes a saved chat on load without creating another run', async () => {
    savePendingRun({ projectId: 'p1', runId: 'r9', createdAt: new Date().toISOString() });
    renderPage('/assistant');
    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/projects/p1/runs/r9?view=chat'));
    expect(mocks.createProjectRun).not.toHaveBeenCalled();
    expect(mocks.startProjectRun).toHaveBeenCalledWith('run-token', 'p1', 'r9', 'tenant-1');
    expect(mocks.listProjects).not.toHaveBeenCalled();
    expect(readPendingRun()).toBeNull();
  });

  it('offers Try again after start keeps timing out', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      mocks.startProjectRun.mockRejectedValue(new GatewayError(504, { code: 'run_start_timeout' }, 'slow'));
      savePendingRun({ projectId: 'p1', runId: 'r9', createdAt: new Date().toISOString() });
      renderPage('/assistant');
      await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
      expect(await screen.findByText(/did not start in time/)).toBeTruthy();
      expect(mocks.startProjectRun).toHaveBeenCalledTimes(3);
      mocks.startProjectRun.mockResolvedValue({ runId: 'r9', rootSessionId: 's', state: 'started' });
      await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Try again' })); });
      await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/projects/p1/runs/r9?view=chat'));
    } finally {
      vi.useRealTimers();
    }
  });
});
