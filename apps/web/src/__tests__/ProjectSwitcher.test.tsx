import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjects: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { ProjectSwitcher, projectSwitchTarget } from '../components/shell/ProjectSwitcher';
import type { Project } from '../api/types';

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

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});

afterEach(() => cleanup());

describe('projectSwitchTarget — preserve page category across projects', () => {
  // From each project-scoped page, switching to project B lands on the equivalent page.
  it('preserves the same category page under the target project', () => {
    expect(projectSwitchTarget('/projects/A', 'A', 'B')).toBe('/projects/B');
    expect(projectSwitchTarget('/projects/A/board', 'A', 'B')).toBe('/projects/B/board');
    expect(projectSwitchTarget('/projects/A/flow', 'A', 'B')).toBe('/projects/B/flow');
    expect(projectSwitchTarget('/projects/A/orchestrations', 'A', 'B')).toBe('/projects/B/orchestrations');
    expect(projectSwitchTarget('/projects/A/settings', 'A', 'B')).toBe('/projects/B/settings');
    expect(projectSwitchTarget('/projects/A/team', 'A', 'B')).toBe('/projects/B/team');
    expect(projectSwitchTarget('/projects/A/memories', 'A', 'B')).toBe('/projects/B/memories');
    expect(projectSwitchTarget('/projects/A/workflows', 'A', 'B')).toBe('/projects/B/workflows');
    expect(projectSwitchTarget('/projects/A/diagnostics', 'A', 'B')).toBe('/projects/B/diagnostics');
    expect(projectSwitchTarget('/projects/A/heartbeat', 'A', 'B')).toBe('/projects/B/heartbeat');
  });

  it('maps sub-resource detail pages to their category root under the target project', () => {
    // Orchestration detail → orchestrations list (id cannot cross projects).
    expect(projectSwitchTarget('/projects/A/orchestrations/run-123', 'A', 'B')).toBe('/projects/B/orchestrations');
    // Casting wizard → Agents (team).
    expect(projectSwitchTarget('/projects/A/team/cast', 'A', 'B')).toBe('/projects/B/team');
    // Deep run / execution routes map to Board via matchSegments.
    expect(projectSwitchTarget('/projects/A/runs/run-9/workflow', 'A', 'B')).toBe('/projects/B/board');
    expect(projectSwitchTarget('/projects/A/runs/run-9/execution/ex-1', 'A', 'B')).toBe('/projects/B/board');
  });

  it('lands on the project home when there is no current project context', () => {
    expect(projectSwitchTarget('/overview', undefined, 'B')).toBe('/projects/B');
    expect(projectSwitchTarget('/', undefined, 'B')).toBe('/projects/B');
  });
});

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="location">{loc.pathname}</div>;
}

function renderSwitcherAt(pathname: string, projectId: string) {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[pathname]}>
        <ProjectSwitcher projectId={projectId} pathname={pathname} />
        <Routes>
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>,
  );
}

describe('ProjectSwitcher — switching navigates to the equivalent page', () => {
  it('navigates to the same category under the selected project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('A', 'Alpha'),
      makeProject('B', 'Bravo'),
    ]);

    renderSwitcherAt('/projects/A/flow', 'A');

    const combo = screen.getByLabelText('Project switcher');
    fireEvent.click(combo);

    const option = await screen.findByText('Bravo');
    fireEvent.click(option);

    await waitFor(() =>
      expect(screen.getByTestId('location').textContent).toBe('/projects/B/flow'),
    );
  });

  it('maps a deep run page to the Board category under the selected project', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([
      makeProject('A', 'Alpha'),
      makeProject('B', 'Bravo'),
    ]);

    renderSwitcherAt('/projects/A/runs/run-9/workflow', 'A');

    const combo = screen.getByLabelText('Project switcher');
    fireEvent.click(combo);

    const option = await screen.findByText('Bravo');
    fireEvent.click(option);

    await waitFor(() =>
      expect(screen.getByTestId('location').textContent).toBe('/projects/B/board'),
    );
  });
});

describe('ProjectSwitcher — 401 auth error', () => {
  it('shows sign-in affordance and never "No projects yet" on a 401', async () => {
    vi.mocked(apiClient.listProjects).mockRejectedValue(new ApiError(401, 'Unauthorized'));

    renderSwitcherAt('/projects/A/board', 'A');

    // Wait for the rejected promise to be processed.
    const combo = screen.getByLabelText('Project switcher') as HTMLInputElement;
    await waitFor(() => expect(combo.placeholder).toBe('Sign in to view projects'));

    // The combobox must NOT be disabled on auth error (user needs to interact with it).
    expect(combo.disabled).toBe(false);

    // Opening the combobox must show the sign-in option, not "No projects yet".
    fireEvent.click(combo);
    const signInOption = await screen.findByText('Sign in with GitHub');
    expect(signInOption).toBeDefined();
    expect(screen.queryByText('No projects yet')).toBeNull();
  });

  it('still shows "Projects unavailable" (disabled) for non-auth errors', async () => {
    vi.mocked(apiClient.listProjects).mockRejectedValue(new Error('Network failure'));

    renderSwitcherAt('/projects/A/board', 'A');

    const combo = screen.getByLabelText('Project switcher') as HTMLInputElement;
    await waitFor(() => expect(combo.placeholder).toBe('Projects unavailable'));
    expect(combo.disabled).toBe(true);
  });
});
