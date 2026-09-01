import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AppShell } from '../components/shell/AppShell';
import {
  GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT,
  GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE,
} from '../api/githubConnectionRequirement';
import { projectIdFromPath } from '../components/shell/projectIdFromPath';
import { resolveActiveKey } from '../components/shell/navConfig';
import * as useAppVersionModule from '../hooks/useAppVersion';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
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
    getAuthSession: vi.fn(),
    getProjectAccessOverview: vi.fn(),
    getNotifications: vi.fn(),
    beginProjectCopilotAuthorization: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function projectsPage(items: Project[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

const LAST_ACTIVE_KEY = 'agentweaver:last-active-project-id';
const shellCss = readFileSync(resolve(process.cwd(), 'src/components/shell/shell.css'), 'utf8');

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

function renderShellAt(path: string, isPlatformAdmin = false) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[path]}>
        <AppShell isPlatformAdmin={isPlatformAdmin}>
          <Routes>
            <Route path="/" element={<div>Gallery</div>} />
            <Route path="/overview" element={<div>Overview content</div>} />
            <Route path="/sessions" element={<div>Sessions content</div>} />
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
  vi.spyOn(useAppVersionModule, 'useAppVersion').mockReturnValue('');
  vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
  vi.mocked(apiClient.getProject).mockResolvedValue(makeProject('proj-1', 'Project One'));
  vi.mocked(apiClient.checkHealth).mockResolvedValue(true);
  vi.mocked(apiClient.getAuthSession).mockResolvedValue({
    authenticated: true,
    auth_mode: 'entra',
    display_name: 'Sabbour',
    email: 'sabbour@example.com',
    login: 'sabbour',
    avatar_url: 'https://example.com/sabbour.png',
    entra_object_id: 'entra-1',
    platform_roles: ['PlatformAdmin'],
    ai_configured: true,
  } as never);
  vi.mocked(apiClient.getProjectAccessOverview).mockResolvedValue({
    auth_mode: 'entra',
    platform_roles: ['PlatformAdmin'],
    current_user_project_role: 'Owner',
    can_manage_role_assignments: true,
    can_manage_project_github_identity: true,
    project_role_assignments: [],
    github_identity_override_login: null,
    effective_github_login: 'octocat',
  } as never);
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'not_connected',
    github_login: null,
  });
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
    expect(screen.getByText('Project settings')).toBeDefined();
    expect(screen.getByText('Diagnostics')).toBeDefined();
    expect(screen.getByText('Heartbeat')).toBeDefined();

    // Global destinations are always present (above the project sections).
    expect(screen.getByText('Overview')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
    expect(screen.getByRole('group', { name: 'Sessions' })).toBeDefined();
    expect(screen.getByText('All sessions')).toBeDefined();
    expect(screen.getByRole('link', { name: 'Agents' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByTestId('app-navigation-scroll').getAttribute('data-scrollbar-mode')).toBe('hover');
    expect(screen.getByTestId('app-navigation-scroll').getAttribute('tabindex')).toBe('0');
    expect(getComputedStyle(screen.getByRole('group', { name: 'Operations' })).gap).toBe('2px');
    expect(screen.getByRole('button', { name: 'GitHub identity' }).closest('.aw-rail-footer')).toBeTruthy();

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
    expect(screen.getByRole('group', { name: 'Sessions' })).toBeDefined();
    expect(screen.queryByRole('link', { name: 'Platform settings' })).toBeNull();
    expect(screen.queryByText('Work')).toBeNull();
    expect(screen.queryByText('System')).toBeNull();
  });

  it('shows Platform settings only to platform admins', () => {
    renderShellAt('/overview', true);
    expect(screen.getByRole('link', { name: 'Platform settings' })).toBeDefined();
  });

  it('shows the shared GitHub Copilot connection action for a typed capability requirement', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://api.example.test/api/projects/proj-1/github/copilot/authorizations/redirect',
    } as never);
    renderShellAt('/projects/proj-1/team');

    window.dispatchEvent(new CustomEvent(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, {
      detail: {
        code: 'github_copilot_connection_required',
        message: GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE,
        action: { type: 'connect_project_copilot_app', project_id: 'proj-1' },
      },
    }));

    expect(await screen.findByText(GITHUB_COPILOT_CONNECTION_REQUIRED_MESSAGE)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Connect GitHub account' }));
    await waitFor(() => expect(apiClient.beginProjectCopilotAuthorization).toHaveBeenCalledWith('proj-1'));
    expect(assign).toHaveBeenCalledWith(
      'https://api.example.test/api/projects/proj-1/github/copilot/authorizations/redirect',
    );
    assign.mockRestore();
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
    expect(resolveActiveKey('/sessions', undefined)).toBe('sessions');
    expect(resolveActiveKey('/settings', undefined)).toBe('overview');
    expect(resolveActiveKey('/platform-settings', undefined)).toBe('platform-settings');
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

  it('contains realistic dev version badges inside the footer while keeping the full version in the tooltip', async () => {
    vi.spyOn(useAppVersionModule, 'useAppVersion').mockReturnValue('0.12.2-dev+a100e95');
    vi.mocked(apiClient.getAuthSession).mockResolvedValue({
      authenticated: true,
      auth_mode: 'entra',
      display_name: 'sabbour',
      email: 'sabbour@example.com',
      login: 'sabbour',
      avatar_url: 'https://example.com/sabbour.png',
      entra_object_id: 'entra-1',
      platform_roles: ['PlatformAdmin'],
      ai_configured: true,
    } as never);

    renderShellAt('/overview');

    const badgeText = screen.getByText('v0.12.2-dev+a100e95');
    const badge = badgeText.closest('.aw-rail-footer__version') as HTMLElement | null;
    expect(badge).toBeTruthy();
    expect(badge?.title).toContain('Full version: v0.12.2-dev+a100e95');
    expect(badge?.className).toContain('aw-rail-footer__version');
    expect(badgeText.className).toBe('aw-rail-footer__version-text');
    expect(screen.queryByText('Alpha v0.12.2-dev+a100e95')).toBeNull();
  });

  it('stacks footer identity and version metadata so the badge cannot consume username space', () => {
    expect(shellCss).toMatch(
      /\.aw-rail-footer\s*\{[^}]*flex-direction:\s*column;[^}]*align-items:\s*stretch;/s,
    );
    expect(shellCss).toMatch(
      /\.aw-rail-footer\s*>\s*\.fui-Button,[^}]*\{[^}]*width:\s*100%;[^}]*flex:\s*0 0 auto;/s,
    );
    expect(shellCss).toMatch(
      /\.aw-rail-footer__meta\s*\{[^}]*max-width:\s*100%;[^}]*align-self:\s*flex-end;/s,
    );
    expect(shellCss).toMatch(
      /\.aw-rail-footer__version\s*\{[^}]*max-width:\s*200px;[^}]*overflow:\s*hidden;/s,
    );
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

  // The legacy "Operator dock" (LeftNav trigger -> BrowserConsole sidebar, backed by
  // ConsolePanelContext + the /console/turn facade) was removed in #346. The Sessions
  // page under Projects (#4/#5) and the /assistant route are now the sole entry points
  // to the MCP-driven assistant; the old /console bookmark now redirects straight to
  // /assistant (see App.tsx), so there is no shell-level panel left to exercise here.
});
