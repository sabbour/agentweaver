import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { AppShell } from '../components/shell/AppShell';
import {
  MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT,
  MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE,
} from '../api/modelProviderConnectionRequirement';
import { projectIdFromPath } from '../components/shell/projectIdFromPath';
import { resolveActiveKey } from '../components/shell/navConfig';
import * as useAppVersionModule from '../hooks/useAppVersion';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import type { AppShellProps } from '../components/shell/AppShell';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    getProject: vi.fn(),
    checkHealth: vi.fn(),
    getAuthSession: vi.fn(),
    getProjectAccessOverview: vi.fn(),
    getUserAiAccess: vi.fn(),
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

function renderShellAt(
  path: string,
  isPlatformAdmin = false,
  props: Partial<AppShellProps> = {},
) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[path]}>
        <AppShell isPlatformAdmin={isPlatformAdmin} {...props}>
          <Routes>
            <Route path="/" element={<div>Gallery</div>} />
            <Route path="/overview" element={<div>Overview content</div>} />
            <Route path="/sessions" element={<div>Sessions content</div>} />
            <Route path="/projects/:projectId" element={<div>Board content <Link to="/projects/proj-1/team">Go team</Link></div>} />
            <Route path="/projects/:projectId/team" element={<div>Team content <Link to="/projects/proj-1">Go board</Link></div>} />
            <Route path="/settings" element={<div>Account settings page</div>} />
            <Route path="/platform-settings" element={<div>Platform settings page</div>} />
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
    effective_github_login: 'octocat',
  } as never);
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'not_connected',
    github_login: null,
  });
  vi.mocked(apiClient.getUserAiAccess).mockResolvedValue({
    effective_source: 'platform_byok',
    platform_byok: { name: 'Platform provider', type: 'openai', model: 'gpt-4o' },
    preference: 'github_copilot',
    personal_byok: null,
    copilot: {
      connected: false,
      github_login: null,
      reconnect_required: false,
    },
  });
});

afterEach(() => {
  cleanup();
});

describe('AppShell navigation', () => {
  it('renders all section groups when a project is in scope', async () => {
    renderShellAt('/projects/proj-1/team');

    expect(screen.getByText('Work', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Squad', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Operations', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Observability', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('System', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Work' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Squad' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Operations' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Observability' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'System' })).toBeDefined();

    // Existing destinations present, with Team relabelled to Agents.
    expect(screen.getByText('Dashboard')).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    expect(screen.getByText('Flow')).toBeDefined();
    expect(screen.getByText('Orchestrations')).toBeDefined();
    expect(screen.getByText('Agents')).toBeDefined();
    expect(screen.getByText('Memories')).toBeDefined();
    expect(screen.getByText('Workflows')).toBeDefined();
    expect(screen.getByText('Diagnostics')).toBeDefined();
    expect(screen.getByText('Heartbeat')).toBeDefined();

    // Global destinations are always present (above the project sections).
    expect(screen.getByText('Overview')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
    expect(screen.getByRole('group', { name: 'Sessions' })).toBeDefined();
    expect(screen.getByRole('link', { name: 'Sessions' })).toBeDefined();
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
    expect(screen.queryByText('Work')).toBeNull();
    expect(screen.queryByText('System')).toBeNull();
  });

  it('shows the settings gear menu items conditionally', async () => {
    renderShellAt('/overview');

    fireEvent.click(screen.getByTestId('settings-menu-button'));
    let settingsSurface = await screen.findByTestId('settings-menu-popover');
    expect(within(settingsSurface).getByText('Account settings')).toBeDefined();
    expect(within(settingsSurface).queryByText('Platform settings')).toBeNull();
    expect(within(settingsSurface).queryByText('Project settings')).toBeNull();

    cleanup();
    renderShellAt('/overview', true);

    fireEvent.click(screen.getByTestId('settings-menu-button'));
    settingsSurface = await screen.findByTestId('settings-menu-popover');
    expect(within(settingsSurface).getByText('Account settings')).toBeDefined();
    expect(within(settingsSurface).getByText('Platform settings')).toBeDefined();
    expect(within(settingsSurface).queryByText('Project settings')).toBeNull();

    cleanup();
    renderShellAt('/projects/proj-1/team', true);

    fireEvent.click(screen.getByTestId('settings-menu-button'));
    settingsSurface = await screen.findByTestId('settings-menu-popover');
    expect(within(settingsSurface).getByText('Account settings')).toBeDefined();
    expect(within(settingsSurface).getByText('Platform settings')).toBeDefined();
    expect(within(settingsSurface).getByText('Project settings')).toBeDefined();
  });

  it('starts the first-run tour once and lets the user replay it', async () => {
    const onFirstRunTourStarted = vi.fn();
    renderShellAt('/overview', true, {
      startFirstRunTour: true,
      tourUserKey: 'sabbour',
      onFirstRunTourStarted,
    });

    expect(await screen.findByRole('heading', { name: 'Create a project' })).toBeDefined();
    expect(onFirstRunTourStarted).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Skip tour' }));
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Create a project' })).toBeNull());
    expect(localStorage.getItem('agentweaver.firstRunTour.v1.sabbour')).toBe('complete');

    fireEvent.click(screen.getByTestId('settings-menu-button'));
    const settingsSurface = await screen.findByTestId('settings-menu-popover');
    fireEvent.click(within(settingsSurface).getByRole('button', { name: 'Take product tour' }));

    expect(await screen.findByRole('heading', { name: 'Create a project' })).toBeDefined();
  });

  it('shows the shared GitHub Copilot connection action for a typed capability requirement', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://api.example.test/api/projects/proj-1/github/copilot/authorizations/redirect',
    } as never);
    renderShellAt('/projects/proj-1/team');

    window.dispatchEvent(new CustomEvent(MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT, {
      detail: {
        code: 'model_provider_connection_required',
        message: MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE,
        action: { type: 'configure_project_model_provider', project_id: 'proj-1' },
      },
    }));

    expect(await screen.findByText(MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE)).toBeDefined();
    expect(screen.getByText('Action required')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Set up model provider' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Authorize GitHub Copilot' }));
    await waitFor(() => expect(apiClient.beginProjectCopilotAuthorization)
      .toHaveBeenCalledWith('proj-1', '/projects/proj-1/team'));
    expect(assign).toHaveBeenCalledWith(
      'https://api.example.test/api/projects/proj-1/github/copilot/authorizations/redirect',
    );
    assign.mockRestore();
  });

  it('routes a platform-scoped model-provider requirement to Platform Settings, not Account Settings', async () => {
    renderShellAt('/projects/proj-1/team');

    window.dispatchEvent(new CustomEvent(MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT, {
      detail: {
        code: 'model_provider_connection_required',
        message: MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE,
        action: { type: 'configure_platform_model_provider', project_id: '' },
      },
    }));

    expect(await screen.findByText(MODEL_PROVIDER_CONNECTION_REQUIRED_MESSAGE)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Open Platform settings' }));
    expect(await screen.findByText('Platform settings page')).toBeDefined();
  });

  it('routes a personal session provider requirement to AI Access in Account settings', async () => {
    renderShellAt('/sessions');

    window.dispatchEvent(new CustomEvent(MODEL_PROVIDER_CONNECTION_REQUIRED_EVENT, {
      detail: {
        code: 'model_provider_connection_required',
        message: 'Configure a model provider for your personal session chat to continue.',
        action: { type: 'configure_user_model_provider', project_id: '' },
      },
    }));

    expect(await screen.findByText(
      'Configure a model provider for your personal session chat to continue.',
    )).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Open AI Access settings' }));
    expect(await screen.findByText('Account settings page')).toBeDefined();
  });

  it('proactively prompts a user with no effective personal AI access and persists dismissal', async () => {
    vi.mocked(apiClient.getUserAiAccess).mockResolvedValue({
      effective_source: 'none',
      platform_byok: null,
      preference: 'github_copilot',
      personal_byok: null,
      copilot: {
        connected: false,
        github_login: null,
        reconnect_required: false,
      },
    });

    renderShellAt('/sessions', false, { tourUserKey: ' User@Example.COM ' });

    expect(await screen.findByRole('heading', { name: 'Set up personal AI access' })).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss Set up personal AI access' }));
    await waitFor(() =>
      expect(screen.queryByRole('heading', { name: 'Set up personal AI access' })).toBeNull(),
    );
    expect(localStorage.getItem(
      'agentweaver.personalAiAccessPrompt.v1.user%40example.com',
    )).toBe('dismissed');

    cleanup();
    renderShellAt('/sessions', false, { tourUserKey: 'user@example.com' });
    await Promise.resolve();

    expect(screen.queryByRole('heading', { name: 'Set up personal AI access' })).toBeNull();
    expect(apiClient.getUserAiAccess).toHaveBeenCalledTimes(1);
  });

  it('opens Account settings from the proactive personal AI access prompt', async () => {
    vi.mocked(apiClient.getUserAiAccess).mockResolvedValue({
      effective_source: 'none',
      platform_byok: null,
      preference: 'github_copilot',
      personal_byok: null,
      copilot: {
        connected: false,
        github_login: null,
        reconnect_required: false,
      },
    });

    renderShellAt('/sessions', false, { tourUserKey: 'entra-user' });

    fireEvent.click(await screen.findByRole('button', { name: 'Open AI Access settings' }));

    expect(await screen.findByText('Account settings page')).toBeDefined();
    expect(localStorage.getItem(
      'agentweaver.personalAiAccessPrompt.v1.entra-user',
    )).toBe('dismissed');
  });

  it.each([
    'platform_byok',
    'user_byok',
    'user_github_copilot',
  ] as const)('does not prompt when access is available through %s', async (effectiveSource) => {
    vi.mocked(apiClient.getUserAiAccess).mockResolvedValue({
      effective_source: effectiveSource,
      platform_byok: effectiveSource === 'platform_byok'
        ? { name: 'Platform provider', type: 'openai', model: 'gpt-4o' }
        : null,
      preference: effectiveSource === 'user_byok' ? 'byok' : 'github_copilot',
      personal_byok: effectiveSource === 'user_byok'
        ? {
          id: 'personal-provider',
          name: 'Personal provider',
          type: 'openai',
          base_url: 'https://example.test/v1',
          model: 'gpt-4o',
          wire_api: 'responses',
          azure_api_version: null,
          headers: null,
          has_api_key: true,
        }
        : null,
      copilot: {
        connected: effectiveSource === 'user_github_copilot',
        github_login: effectiveSource === 'user_github_copilot' ? 'octocat' : null,
        reconnect_required: false,
      },
    });

    renderShellAt('/sessions', false, { tourUserKey: `user-${effectiveSource}` });

    await waitFor(() => expect(apiClient.getUserAiAccess).toHaveBeenCalled());
    expect(screen.queryByRole('heading', { name: 'Set up personal AI access' })).toBeNull();
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
    expect(resolveActiveKey('/projects/p1/settings', 'p1')).toBe('dashboard');
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
    expect(resolveActiveKey('/platform-settings', undefined)).toBe('overview');
  });

  it('extracts the project id from project-scoped paths', () => {
    expect(projectIdFromPath('/projects/abc/team')).toBe('abc');
    expect(projectIdFromPath('/')).toBeUndefined();
    expect(projectIdFromPath('/projects')).toBeUndefined();
  });

  it('collapses to an icon-only rail and persists the choice', () => {
    renderShellAt('/projects/proj-1');

    // Expanded by default: section groups exist and item text is visible.
    expect(screen.getByText('Work', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Squad', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Operations', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('Observability', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByText('System', { selector: '.aw-nav-section__heading' })).toBeDefined();
    expect(screen.getByRole('group', { name: 'Work' })).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    expect(screen.getByTestId('app-navigation-menu').getAttribute('data-collapsed')).toBe('false');

    const collapse = screen.getByRole('button', { name: 'Collapse navigation' });
    fireEvent.click(collapse);

    // Collapsed: text labels gone, but items remain reachable via aria-label.
    expect(screen.queryByText('Work', { selector: '.aw-nav-section__heading' })).toBeNull();
    expect(screen.queryByText('Squad', { selector: '.aw-nav-section__heading' })).toBeNull();
    expect(screen.queryByText('Operations', { selector: '.aw-nav-section__heading' })).toBeNull();
    expect(screen.queryByText('Observability', { selector: '.aw-nav-section__heading' })).toBeNull();
    expect(screen.queryByText('System', { selector: '.aw-nav-section__heading' })).toBeNull();
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
