import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { ProjectPage } from '../pages/ProjectPage';
import { makeBoard } from './fixtures/board';
import type { Project } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getProject: vi.fn(),
    listProjectRuns: vi.fn(),
    getBoard: vi.fn(),
    getBacklogSettings: vi.fn(),
    getTeam: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderPage(projectId = 'proj-1') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/board`]}>
        <Routes>
          <Route path="/projects/:projectId/board" element={<ProjectPage />} />
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
} as unknown as Project;

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProject).mockResolvedValue(project);
  vi.mocked(apiClient.listProjectRuns).mockResolvedValue([]);
  vi.mocked(apiClient.getBoard).mockResolvedValue(makeBoard({}));
  vi.mocked(apiClient.getBacklogSettings).mockResolvedValue({
    max_ready_per_heartbeat: 3,
    pickup_autopilot: false,
    pickup_auto_approve_tools: false,
  });
  vi.mocked(apiClient.getTeam).mockResolvedValue({ members: [] } as never);
});

afterEach(() => {
  cleanup();
});

describe('ProjectPage board (board-dedupe)', () => {
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

    await waitFor(() => expect(screen.getByText('Runs')).toBeTruthy());
    expect(screen.getByText('No runs yet. Start one above.')).toBeTruthy();
  });
});
