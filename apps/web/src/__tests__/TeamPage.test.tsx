import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { TeamPage } from '../pages/TeamPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { SkillDto, TeamDto, TeamTemplateDto } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getTeam: vi.fn(),
    getTemplates: vi.fn(),
    getProject: vi.fn(),
    getMemberCharter: vi.fn(),
    updateMemberCharter: vi.fn(),
    addMember: vi.fn(),
    removeMember: vi.fn(),
    reroleMember: vi.fn(),
    getSyncStatus: vi.fn(),
    commitSync: vi.fn(),
    getMemberHistory: vi.fn(),
    listSkills: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderWithRouter(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/team`]}>
        <Routes>
          <Route path="/projects/:projectId/team" element={<TeamPage />} />
          <Route path="/projects/:projectId/team/:agentName/memory" element={<div>Agent memory page</div>} />
          <Route path="/projects/:projectId/team/cast" element={<div>Cast page</div>} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

const getTeamMock = () => vi.mocked(apiClient.getTeam);
const getTemplatesMock = () => vi.mocked(apiClient.getTemplates);
const getProjectMock = () => vi.mocked(apiClient.getProject);
const getMemberHistoryMock = () => vi.mocked(apiClient.getMemberHistory);
const listSkillsMock = () => vi.mocked(apiClient.listSkills);

beforeEach(() => {
  vi.clearAllMocks();
  getTemplatesMock().mockResolvedValue([] as TeamTemplateDto[]);
  getProjectMock().mockRejectedValue(new Error('not needed'));
  getMemberHistoryMock().mockResolvedValue({ member_name: 'Alice', content: '' });
  listSkillsMock().mockResolvedValue([] as SkillDto[]);
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
    // Status is reflected in the filter tabs, not as raw text in the card
    expect(screen.getByRole('tab', { name: /Active/ })).toBeDefined();
    expect(screen.getByRole('tab', { name: /Retired/ })).toBeDefined();
  });

  const singleMemberTeam: TeamDto = {
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
    ],
  };

  const makeSkill = (over: Partial<SkillDto>): SkillDto => ({
    id: 'skill-1',
    name: 'Example Skill',
    description: 'Does something useful',
    provenance: 'manual',
    source_repository: null,
    source_location: null,
    status: 'active',
    content_hash: 'abc',
    resource_count: 0,
    assigned_agents: [],
    created_at: '',
    updated_at: '',
    ...over,
  });

  it('shows assigned skills for an agent on the overview tab', async () => {
    getTeamMock().mockResolvedValue(singleMemberTeam);
    listSkillsMock().mockResolvedValue([
      makeSkill({ id: 's1', name: 'Research Skill', description: 'Searches the web', assigned_agents: ['Alice'] }),
      makeSkill({ id: 's2', name: 'Unrelated Skill', description: 'For someone else', assigned_agents: ['Bob'] }),
    ]);

    renderWithRouter('proj-003');

    await waitFor(() => expect(screen.getByText('Alice')).toBeDefined());
    fireEvent.click(screen.getByText('Alice'));

    await waitFor(() => {
      expect(screen.getByText('Research Skill')).toBeDefined();
    });
    expect(screen.getByText('Searches the web')).toBeDefined();
    // Skill assigned only to Bob must not appear on Alice's panel.
    expect(screen.queryByText('Unrelated Skill')).toBeNull();
  });

  it('shows an empty state when the agent has no assigned skills', async () => {
    getTeamMock().mockResolvedValue(singleMemberTeam);
    listSkillsMock().mockResolvedValue([
      makeSkill({ id: 's2', name: 'Unrelated Skill', assigned_agents: ['Bob'] }),
    ]);

    renderWithRouter('proj-004');

    await waitFor(() => expect(screen.getByText('Alice')).toBeDefined());
    fireEvent.click(screen.getByText('Alice'));

    await waitFor(() => {
      expect(screen.getByText('No skills assigned')).toBeDefined();
    });
  });

  it('navigates to the selected agent memory view from the drawer', async () => {
    getTeamMock().mockResolvedValue(singleMemberTeam);

    renderWithRouter('proj-005');

    await waitFor(() => expect(screen.getByText('Alice')).toBeDefined());
    fireEvent.click(screen.getByText('Alice'));

    await waitFor(() => expect(screen.getByRole('button', { name: 'View memory' })).toBeDefined());
    fireEvent.click(screen.getByRole('button', { name: 'View memory' }));

    await waitFor(() => expect(screen.getByText('Agent memory page')).toBeDefined());
  });
});
