import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ProjectPage } from '../pages/ProjectPage';
import { makeBoard } from './fixtures/board';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { Project } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProject: vi.fn(),
    listProjectRuns: vi.fn(),
    deleteRun: vi.fn(),
    getBoard: vi.fn(),
    getBacklogSettings: vi.fn(),
    getTeam: vi.fn(),
    getUnattendedReadiness: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjectRuns`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function runsPage<T>(items: T[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId = 'proj-1', currentUserKey = 'entra-user-1') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/board`]}>
        <Routes>
          <Route
            path="/projects/:projectId/board"
            element={<ProjectPage currentUserKey={currentUserKey} />}
          />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const project: Project = {
  project_id: 'proj-1',
  name: 'Demo Project',
  origin: 'local',
  source_repository: 'https://example.com/repo.git',
  working_directory: 'C:/work/demo',
  default_branch: 'main',
  default_model_github_copilot: 'gpt-4o',
  available: true,
  owner: 'project-owner',
  state: 'active',
  created_at: '2026-09-01T00:00:00Z',
  updated_at: '2026-09-01T00:00:00Z',
} as unknown as Project;

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.mocked(apiClient.getProject).mockResolvedValue(project);
  vi.mocked(apiClient.listProjectRuns).mockResolvedValue(runsPage([]));
  vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard({}));
  vi.mocked(apiClient.getBacklogSettings).mockResolvedValue({
    max_ready_per_heartbeat: 3,
    pickup_autopilot: false,
    pickup_auto_approve_tools: false,
  });
  vi.mocked(apiClient.getTeam).mockResolvedValue({ members: [] } as never);
  vi.mocked(apiClient.getUnattendedReadiness).mockResolvedValue({
    status: 'ready',
    reason_code: 'ready',
    message: 'Ready.',
    repo_app_installation_connected: false,
    repository: {
      required: false,
      status: 'not_required',
      reason_code: 'not_required',
      repo_app_installation_connected: false,
    },
  });
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe('ProjectPage board (board-dedupe)', () => {
  it('shows repository access as optional for a blank project', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
    } as Project);

    renderPage();

    expect(await screen.findByText('Project setup')).toBeDefined();
    expect(screen.getAllByText('Optional').length).toBeGreaterThan(0);
    expect(screen.getByText(/Local agent work can continue without a repository/)).toBeDefined();
    expect(screen.getByRole('button', { name: 'Set up repository access' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Dismiss Project setup' })).toBeDefined();
  });

  it('persists setup dismissal for the current project and user across route reloads', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
    } as Project);

    const firstRender = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Dismiss Project setup' }));
    expect(screen.queryByText('Project setup')).toBeNull();
    firstRender.unmount();

    renderPage();
    await waitFor(() => expect(apiClient.getProject).toHaveBeenCalledTimes(2));
    expect(screen.queryByText('Project setup')).toBeNull();
  });

  it('does not show setup again after an unrelated project update', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
    } as Project);

    const firstRender = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Dismiss Project setup' }));
    firstRender.unmount();

    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
      updated_at: '2026-09-02T00:00:00Z',
    } as Project);
    renderPage();

    await waitFor(() => expect(apiClient.getProject).toHaveBeenCalledTimes(2));
    expect(screen.queryByText('Project setup')).toBeNull();
  });

  it('shows setup again when repository access becomes required', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
    } as Project);

    const firstRender = renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Dismiss Project setup' }));
    firstRender.unmount();

    vi.mocked(apiClient.getUnattendedReadiness).mockResolvedValue({
      status: 'not_ready',
      reason_code: 'repo_app_installation_required',
      message: 'Repository access is required.',
      repo_app_installation_connected: false,
      repository: {
        required: true,
        status: 'not_ready',
        reason_code: 'repo_app_installation_required',
        repo_app_installation_connected: false,
      },
    });
    renderPage();

    expect(await screen.findByText('Project setup')).toBeDefined();
    expect(screen.getAllByText('Required').length).toBeGreaterThan(0);
    expect(screen.getByText('Action required')).toBeDefined();
  });

  it('keeps setup dismissal scoped to the signed-in user', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      ...project,
      origin: 'blank',
      source_repository: null,
    } as Project);

    const firstRender = renderPage('proj-1', 'entra-user-1');
    fireEvent.click(await screen.findByRole('button', { name: 'Dismiss Project setup' }));
    firstRender.unmount();

    renderPage('proj-1', 'entra-user-2');
    expect(await screen.findByText('Project setup')).toBeDefined();
  });

  it('keeps the Start task CTA and removes the standalone Start run affordance', async () => {
    renderPage();

    await waitFor(() => expect(screen.getByRole('button', { name: 'Start task' })).toBeTruthy());

    // Primary CTA remains.
    expect(screen.getByRole('button', { name: 'Start task' })).toBeTruthy();

    // The standalone "Start run" entry point is gone — no button, no overflow menu item,
    // and the overflow "More actions" menu that only hosted it is removed.
    expect(screen.queryByRole('button', { name: 'Start run' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: 'Start run' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'More actions' })).toBeNull();

    // Redundant nav-duplicating buttons are gone.
    expect(screen.queryByRole('button', { name: 'Settings' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Team' })).toBeNull();
  });

  it('does not render the project metadata info grid (now on Settings)', async () => {
    renderPage();

    await waitFor(() => expect(screen.getByRole('button', { name: 'Start task' })).toBeTruthy());

    expect(screen.queryByText('Repository path')).toBeNull();
    expect(screen.queryByText('Default branch')).toBeNull();
    expect(screen.queryByText('Copilot model')).toBeNull();
    expect(screen.queryByText('C:/work/demo')).toBeNull();
  });

  it('still renders the Runs section', async () => {
    renderPage();

    await waitFor(() => expect(screen.getByRole('button', { name: /Run audit trail/i })).toBeTruthy());
    expect(screen.queryByText('No run history yet. Runs started from orchestration tasks will appear here for audit.')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Run audit trail/i }));

    expect(screen.getByText('No run history yet. Runs started from orchestration tasks will appear here for audit.')).toBeTruthy();
  });

  it('shows coordinator assembly handoff as automatic preparation in the run audit trail', async () => {
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue(runsPage([
      {
        workflow_run_id: 'coord-1',
        execution_id: 'coord-1',
        agent_name: 'Coordinator',
        task: 'Prepare assembly',
        status: 'in_progress',
        coordinator_status: 'awaiting_assembly',
        started_at: new Date().toISOString(),
      },
    ]) as never);

    renderPage();

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    fireEvent.click(screen.getByRole('button', { name: /Run audit trail/i }));

    expect(screen.getByText('Preparing assembly')).toBeTruthy();
    expect(screen.queryByText('Awaiting assembly')).toBeNull();
  });

  it('treats assemble_ready runs as terminal history and does not keep polling them', async () => {
    const setIntervalSpy = vi.spyOn(globalThis, 'setInterval');
    vi.mocked(apiClient.listProjectRuns).mockResolvedValue(runsPage([
      {
        workflow_run_id: 'coord-1',
        execution_id: 'coord-1',
        agent_name: 'Coordinator',
        task: 'Assembly ready handoff',
        status: 'assemble_ready',
        coordinator_status: 'complete',
        started_at: new Date().toISOString(),
      },
    ]) as never);

    renderPage();

    await waitFor(() => expect(screen.getByRole('button', { name: /Run audit trail/i })).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: /Run audit trail/i }));

    expect(screen.getByText('Assembly ready handoff')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Abandon run' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Delete run' })).toBeTruthy();
    expect(vi.mocked(apiClient.listProjectRuns)).toHaveBeenCalledTimes(1);
    expect(setIntervalSpy).not.toHaveBeenCalledWith(expect.any(Function), 5000);
  });
});
