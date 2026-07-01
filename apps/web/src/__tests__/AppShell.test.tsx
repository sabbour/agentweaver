import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
    checkHealth: vi.fn(),
    getGitHubAuthStatus: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { AppShell, projectIdFromPath } from '../components/shell/AppShell';
import { resolveActiveKey } from '../components/shell/navConfig';
import type { Project } from '../api/types';

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
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderShellAt(path: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[path]}>
        <AppShell>
          <Routes>
            <Route path="/" element={<div>Gallery</div>} />
            <Route path="/overview" element={<div>Overview content</div>} />
            <Route path="/projects/:projectId" element={<div>Board content</div>} />
            <Route path="/projects/:projectId/team" element={<div>Team content</div>} />
          </Routes>
        </AppShell>
      </MemoryRouter>
    </Wrapper>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.mocked(apiClient.listProjects).mockResolvedValue([]);
  vi.mocked(apiClient.checkHealth).mockResolvedValue(true);
  vi.mocked(apiClient.getGitHubAuthStatus).mockResolvedValue({ status: 'signed_in' } as never);
});

afterEach(() => {
  cleanup();
});

describe('AppShell navigation', () => {
  it('renders all section groups when a project is in scope', async () => {
    renderShellAt('/projects/proj-1');

    expect(screen.getByText('WORK')).toBeDefined();
    expect(screen.getByText('SQUAD')).toBeDefined();
    expect(screen.getByText('OPERATIONS')).toBeDefined();
    expect(screen.getByText('SYSTEM')).toBeDefined();

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

    // The top bar exposes the project switcher and an API status indicator.
    expect(screen.getByLabelText('Project switcher')).toBeDefined();
    await waitFor(() => expect(screen.getByLabelText('API reachable')).toBeDefined());
  });

  it('hides project-scoped groups at the app root (no project selected)', () => {
    renderShellAt('/');
    expect(screen.getByText('Overview')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
    expect(screen.queryByText('WORK')).toBeNull();
    expect(screen.queryByText('SYSTEM')).toBeNull();
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
    // Deep run pages fall back to the Board; orchestration detail keeps Orchestrations active.
    expect(resolveActiveKey('/projects/p1/runs/r1/workflow', 'p1')).toBe('board');
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

    // Expanded by default: section labels and item text are visible.
    expect(screen.getByText('WORK')).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();

    const collapse = screen.getByRole('button', { name: 'Collapse navigation' });
    fireEvent.click(collapse);

    // Collapsed: text labels gone, but items remain reachable via aria-label.
    expect(screen.queryByText('WORK')).toBeNull();
    expect(screen.queryByText('Board')).toBeNull();
    expect(screen.getByRole('button', { name: 'Expand navigation' })).toBeDefined();
    expect(localStorage.getItem('aw.nav.collapsed')).toBe('1');
  });

  it('keeps the persisted project in context on the global Overview route', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([makeProject('proj-9', 'Persisted Proj')]);
    localStorage.setItem(LAST_ACTIVE_KEY, 'proj-9');

    renderShellAt('/overview');

    // The switcher still shows the loaded project (not ejected to empty)…
    await waitFor(() =>
      expect((screen.getByLabelText('Project switcher') as HTMLInputElement).value).toBe(
        'Persisted Proj',
      ),
    );
    // …and the project-scoped sections render so their nav targets resolve to it.
    expect(screen.getByText('WORK')).toBeDefined();
    expect(screen.getByText('Board')).toBeDefined();
    // Overview is still the active item (global content stays global).
    expect(resolveActiveKey('/overview', undefined)).toBe('overview');
  });

  it('clears a deleted persisted project gracefully on a global route', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([]);
    localStorage.setItem(LAST_ACTIVE_KEY, 'gone');

    renderShellAt('/overview');

    // Once the project list loads and the persisted id is absent, it is cleared…
    await waitFor(() => expect(localStorage.getItem(LAST_ACTIVE_KEY)).toBeNull());
    // …and the shell falls back to the no-project state without crashing.
    expect(screen.queryByText('WORK')).toBeNull();
  });
});
