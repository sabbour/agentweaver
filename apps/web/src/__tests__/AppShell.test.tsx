import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AppShell } from '../components/shell/AppShell';
import { projectIdFromPath } from '../components/shell/projectIdFromPath';
import { ConsoleRouteRedirect } from '../components/shell/ConsoleRouteRedirect';
import { resolveActiveKey } from '../components/shell/navConfig';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
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
    listProjects: vi.fn(),
    getProject: vi.fn(),
    checkHealth: vi.fn(),
    getGitHubAuthStatus: vi.fn(),
    getNotifications: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function projectsPage(items: Project[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

const LAST_ACTIVE_KEY = 'agentweaver:last-active-project-id';

function makeProject(id: string, name: string): Project {
  return {
    project_id: id,
    name,
    origin: 'blank',
    source_repository: null,
    working_directory: '/tmp/x',
    default_branch: 'main',
    owner: 'me',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderShellAt(path: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[path]}>
        <AppShell>
          <Routes>
            <Route path="/" element={<div>Gallery</div>} />
            <Route path="/overview" element={<div>Overview content</div>} />
            <Route path="/console" element={<ConsoleRouteRedirect />} />
            <Route path="/projects/:projectId" element={<div>Board content <Link to="/projects/proj-1/team">Go team</Link></div>} />
            <Route path="/projects/:projectId/team" element={<div>Team content <Link to="/projects/proj-1">Go board</Link></div>} />
          </Routes>
        </AppShell>
      </MemoryRouter>
    </Wrapper>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
  vi.mocked(apiClient.getProject).mockResolvedValue(makeProject('proj-1', 'Project One'));
  vi.mocked(apiClient.checkHealth).mockResolvedValue(true);
  vi.mocked(apiClient.getGitHubAuthStatus).mockResolvedValue({ status: 'signed_in' } as never);
});

afterEach(() => {
  cleanup();
});

describe('AppShell navigation', () => {
  it('renders all section groups when a project is in scope', async () => {
    renderShellAt('/projects/proj-1/team');

    expect(screen.getByRole('group', { name: 'Work' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Squad' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Operations' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'System' })).toBeDefined();

    // Existing destinations present, with Team relabelled to Agents.
    expect(screen.getByText('Dashboard')).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    expect(screen.getByText('Flow')).toBeDefined();
    expect(screen.getByText('Orchestrations')).toBeDefined();
    expect(screen.getByText('Agents')).toBeDefined();
    expect(screen.getByText('Memories')).toBeDefined();
    expect(screen.getByText('Workflows')).toBeDefined();
    expect(screen.getByText('Settings')).toBeDefined();
    expect(screen.getByText('Diagnostics')).toBeDefined();
    expect(screen.getByText('Heartbeat')).toBeDefined();

    // Global destinations are always present (above the project sections).
    expect(screen.getByText('Overview')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Agents' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByTestId('app-navigation-scroll').getAttribute('data-scrollbar-mode')).toBe('hover');
    expect(screen.getByTestId('app-navigation-scroll').getAttribute('tabindex')).toBe('0');
    expect(getComputedStyle(screen.getByRole('group', { name: 'Operations' })).gap).toBe('2px');

    // The top bar exposes the project switcher and an API status indicator.
    expect(screen.getByLabelText('Project switcher')).toBeDefined();
    const startTask = screen.getByTestId('start-task-topbar-action');
    expect(startTask.closest('main')).toBeTruthy();
    expect(getComputedStyle(startTask).position).not.toBe('fixed');
    await waitFor(() => expect(screen.getByLabelText('API reachable')).toBeDefined());
  });

  it('hides project-scoped groups at the app root (no project selected)', () => {
    renderShellAt('/');
    expect(screen.getByText('Overview')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
    expect(screen.queryByText('Work')).toBeNull();
    expect(screen.queryByText('System')).toBeNull();
  });

  it('resolves the active nav item from the route', () => {
    expect(resolveActiveKey('/projects/p1', 'p1')).toBe('dashboard');
    expect(resolveActiveKey('/projects/p1/board', 'p1')).toBe('board');
    expect(resolveActiveKey('/projects/p1/flow', 'p1')).toBe('flow');
    expect(resolveActiveKey('/projects/p1/team', 'p1')).toBe('agents');
    expect(resolveActiveKey('/projects/p1/team/cast', 'p1')).toBe('agents');
    expect(resolveActiveKey('/projects/p1/memories', 'p1')).toBe('memories');
    expect(resolveActiveKey('/projects/p1/observability', 'p1')).toBe('observability');
    expect(resolveActiveKey('/projects/p1/observability/traces', 'p1')).toBe('observability');
    expect(resolveActiveKey('/projects/p1/workflows', 'p1')).toBe('workflows');
    expect(resolveActiveKey('/projects/p1/settings', 'p1')).toBe('settings');
    // Removed run-page routes are no longer special-cased; orchestration detail keeps Orchestrations active.
    expect(resolveActiveKey('/projects/p1/runs/r1/workflow', 'p1')).toBe('dashboard');
    expect(resolveActiveKey('/projects/p1/runs/r1/execution/e1', 'p1')).toBe('dashboard');
    expect(resolveActiveKey('/projects/p1/orchestrations/o1', 'p1')).toBe('orchestrations');
    // No project scope → global keys.
    expect(resolveActiveKey('/', undefined)).toBe('overview');
    expect(resolveActiveKey('/overview', undefined)).toBe('overview');
    expect(resolveActiveKey('/projects', undefined)).toBe('projects');
  });

  it('extracts the project id from project-scoped paths', () => {
    expect(projectIdFromPath('/projects/abc/team')).toBe('abc');
    expect(projectIdFromPath('/')).toBeUndefined();
    expect(projectIdFromPath('/projects')).toBeUndefined();
  });

  it('collapses to an icon-only rail and persists the choice', () => {
    renderShellAt('/projects/proj-1');

    // Expanded by default: section groups exist and item text is visible.
    expect(screen.getByRole('group', { name: 'Work' })).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    expect(screen.getByTestId('app-navigation-menu').getAttribute('data-collapsed')).toBe('false');

    const collapse = screen.getByRole('button', { name: 'Collapse navigation' });
    fireEvent.click(collapse);

    // Collapsed: text labels gone, but items remain reachable via aria-label.
    expect(screen.queryByText('Work')).toBeNull();
    expect(screen.queryByText('Board')).toBeNull();
    expect(screen.getByRole('link', { name: 'Board' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Expand navigation' })).toBeDefined();
    expect(screen.getByTestId('app-navigation-menu').getAttribute('data-collapsed')).toBe('true');
    expect(screen.getByTestId('app-navigation-scroll').getAttribute('data-scrollbar-mode')).toBe('hidden');
    expect(localStorage.getItem('aw.nav.collapsed')).toBe('1');
  });

  it('keeps the persisted project in context on the global Overview route', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([makeProject('proj-9', 'Persisted Proj')]));
    localStorage.setItem(LAST_ACTIVE_KEY, 'proj-9');

    renderShellAt('/overview');

    // The switcher still shows the loaded project (not ejected to empty)…
    await waitFor(() =>
      expect((screen.getByLabelText('Project switcher') as HTMLInputElement).value).toBe(
        'Persisted Proj',
      ),
    );
    // …and the project-scoped sections render so their nav targets resolve to it.
    expect(screen.getByRole('group', { name: 'Work' })).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    // Overview is still the active item (global content stays global).
    expect(resolveActiveKey('/overview', undefined)).toBe('overview');
  });

  it('clears a deleted persisted project gracefully on a global route', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
    localStorage.setItem(LAST_ACTIVE_KEY, 'gone');

    renderShellAt('/overview');

    // Once the project list loads and the persisted id is absent, it is cleared…
    await waitFor(() => expect(localStorage.getItem(LAST_ACTIVE_KEY)).toBeNull());
    // …and the shell falls back to the no-project state without crashing.
    expect(screen.queryByText('Work')).toBeNull();
  });

  it('opens a singleton slide-in console from the top bar', async () => {
    renderShellAt('/projects/proj-1');

    const opener = screen.getByTestId('open-console-panel');
    expect(opener.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(opener);

    expect(await screen.findByRole('dialog', { name: 'Agentweaver Copilot dock' })).toBeDefined();
    expect(opener.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getAllByTestId('browser-console')).toHaveLength(1);
    expect(screen.getByText('Agentweaver Console')).toBeDefined();
  });

  it('keeps the singleton console mounted and its transcript intact across route changes', async () => {
    renderShellAt('/projects/proj-1');

    fireEvent.click(screen.getByTestId('open-console-panel'));
    await screen.findByRole('dialog', { name: 'Agentweaver Copilot dock' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Ask Agentweaver' }), { target: { value: '/help' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText(/Secondary shortcuts:/)).toBeDefined();

    fireEvent.click(screen.getByRole('link', { name: 'Go team' }));
    await screen.findByText(/Team content/);

    expect(screen.getAllByTestId('browser-console')).toHaveLength(1);
    expect(screen.getByText(/Secondary shortcuts:/)).toBeDefined();
    expect(screen.getByRole('dialog', { name: 'Agentweaver Copilot dock' })).toBeDefined();
  });

  it('closes the singleton console from the close button or Escape and restores focus to the opener', async () => {
    renderShellAt('/projects/proj-1');

    const opener = screen.getByTestId('open-console-panel') as HTMLButtonElement;
    opener.focus();
    fireEvent.click(opener);
    await screen.findByRole('dialog', { name: 'Agentweaver Copilot dock' });

    fireEvent.click(screen.getByRole('button', { name: 'Close panel' }));

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Agentweaver Copilot dock' })).toBeNull());
    expect(document.activeElement).toBe(opener);

    fireEvent.click(opener);
    await screen.findByRole('dialog', { name: 'Agentweaver Copilot dock' });
    fireEvent.keyDown(document, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Agentweaver Copilot dock' })).toBeNull());
    expect(screen.getAllByTestId('browser-console')).toHaveLength(1);
    expect(opener.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(opener);
  });

  it('redirects the obsolete /console route into the shell console panel', async () => {
    renderShellAt('/console');

    expect(await screen.findByRole('dialog', { name: 'Agentweaver Copilot dock' })).toBeDefined();
    expect(await screen.findByText('Overview content')).toBeDefined();
    expect(screen.getAllByTestId('browser-console')).toHaveLength(1);
  });
});
