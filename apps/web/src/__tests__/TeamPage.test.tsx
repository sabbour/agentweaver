import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { type ReactNode } from 'react';
import { TeamPage } from '../pages/TeamPage';
import type { TeamDto, TeamTemplateDto } from '../api/types';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getTeam: vi.fn(),
    getTemplates: vi.fn(),
    getMemberCharter: vi.fn(),
    updateMemberCharter: vi.fn(),
    addMember: vi.fn(),
    removeMember: vi.fn(),
    reroleMember: vi.fn(),
    getSyncStatus: vi.fn(),
    commitSync: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

function renderWithRouter(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/team`]}>
        <Routes>
          <Route path="/projects/:projectId/team" element={<TeamPage />} />
          <Route path="/projects/:projectId/team/cast" element={<div>Cast page</div>} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getTeamMock = () => vi.mocked(apiClient.getTeam);
const getTemplatesMock = () => vi.mocked(apiClient.getTemplates);

beforeEach(() => {
  vi.clearAllMocks();
  getTemplatesMock().mockResolvedValue([] as TeamTemplateDto[]);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('TeamPage', () => {
  it('renders empty state when no team exists', async () => {
    const { ApiError } = await import('../api/client');
    getTeamMock().mockRejectedValue(new ApiError(404, 'Not found'));

    renderWithRouter('proj-001');

    await waitFor(() => {
      expect(screen.getByText('No team yet')).toBeDefined();
    });

    expect(screen.getAllByText('Cast team').length).toBeGreaterThanOrEqual(1);
  });

  it('renders roster table when team exists', async () => {
    const team: TeamDto = {
      project_name: 'Test Project',
      universe: 'default',
      layout: 'canonical',
      migration_available: false,
      members: [
        {
          name: 'Alice',
          role_title: 'Backend Engineer',
          charter_path: '.squad/alice.md',
          status: 'active',
          default_model: 'gpt-4o',
          is_named: true,
          is_built_in: false,
        },
        {
          name: 'Bob',
          role_title: 'Frontend Engineer',
          charter_path: '.squad/bob.md',
          status: 'retired',
          default_model: 'gpt-4o',
          is_named: true,
          is_built_in: false,
        },
      ],
    };
    getTeamMock().mockResolvedValue(team);

    renderWithRouter('proj-002');

    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeDefined();
      expect(screen.getByText('Bob')).toBeDefined();
    });

    expect(screen.getByText('Backend Engineer')).toBeDefined();
    expect(screen.getByText('Frontend Engineer')).toBeDefined();
    expect(screen.getByText('active')).toBeDefined();
    expect(screen.getByText('retired')).toBeDefined();
  });
});
