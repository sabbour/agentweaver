import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SkillsPage } from '../pages/SkillsPage';
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
import type { SkillAcquisitionResponse, SkillDto, TeamDto, TeamMemberDto } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    listSkills: vi.fn(),
    getTeam: vi.fn(),
    getSkill: vi.fn(),
    deleteSkill: vi.fn(),
    syncSkills: vi.fn(),
    previewSkillImport: vi.fn(),
    importSkills: vi.fn(),
    uploadSkills: vi.fn(),
    assignSkill: vi.fn(),
    unassignSkill: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId = 'proj-001') {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/skills`]}>
        <Routes>
          <Route path="/projects/:projectId/skills" element={<SkillsPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

function makeSkill(overrides: Partial<SkillDto> = {}): SkillDto {
  return {
    id: 's1',
    name: 'code-review',
    description: 'Reviews code for correctness.',
    provenance: 'connected-repo-sync',
    source_repository: null,
    source_location: '.github/skills/code-review',
    status: 'active',
    content_hash: 'abc123',
    resource_count: 0,
    assigned_agents: [],
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function makeMember(name: string, roleTitle = 'Engineer'): TeamMemberDto {
  return {
    name,
    role_title: roleTitle,
    charter_path: `charters/${name}.md`,
    status: 'active',
    default_model: 'gpt',
    is_named: true,
    is_built_in: false,
  };
}

function makeTeam(members: TeamMemberDto[]): TeamDto {
  return { project_name: 'p', universe: 'u', members, layout: 'canonical', migration_available: false };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getTeam).mockResolvedValue(makeTeam([makeMember('Smith', 'Lead PM'), makeMember('Neo', 'Lead Architect')]));
});

afterEach(() => {
  cleanup();
});

describe('SkillsPage — catalog', () => {
  it('lists catalog skills with status and provenance', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([makeSkill()]);

    renderPage();

    await waitFor(() => expect(screen.getByText('code-review')).toBeTruthy());
    expect(screen.getByText('Reviews code for correctness.')).toBeTruthy();
    expect(screen.getByText('active')).toBeTruthy();
    expect(screen.getByText('connected-repo-sync')).toBeTruthy();
  });

  it('shows the empty state when the catalog is empty', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);

    renderPage();

    await waitFor(() => expect(screen.getByText(/No skills in the catalog yet/)).toBeTruthy());
  });

  it('runs a connected-repo sync and surfaces the result summary', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const result: SkillAcquisitionResponse = {
      results: [{ location: '.github/skills/x', name: 'x', kind: 'Added', skill_id: 's9', errors: [] }],
      marked_missing: [],
    };
    vi.mocked(apiClient.syncSkills).mockResolvedValue(result);

    renderPage();

    await waitFor(() => expect(screen.getByText(/No skills in the catalog yet/)).toBeTruthy());
    fireEvent.click(screen.getByText('Sync connected repo'));

    await waitFor(() => expect(apiClient.syncSkills).toHaveBeenCalledWith('proj-001'));
    await waitFor(() => expect(screen.getByText(/1 added/)).toBeTruthy());
  });
});

describe('SkillsPage — assignments', () => {
  it('renders an agent checkbox per team member showing "Name — Role" and assigns on toggle', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([makeSkill()]);
    vi.mocked(apiClient.assignSkill).mockResolvedValue(undefined as unknown as void);

    renderPage();

    await waitFor(() => expect(screen.getByText('code-review')).toBeTruthy());
    fireEvent.click(screen.getByRole('tab', { name: 'Assignments' }));

    await waitFor(() => expect(screen.getByText('Smith — Lead PM')).toBeTruthy());
    expect(screen.getByText('Neo — Lead Architect')).toBeTruthy();

    fireEvent.click(screen.getByText('Smith — Lead PM'));
    await waitFor(() => expect(apiClient.assignSkill).toHaveBeenCalledWith('proj-001', 's1', 'Smith'));
  });

  it('shows the role on assigned-agent chips in the catalog and falls back to the bare name for unknown agents', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([
      makeSkill({ assigned_agents: ['Smith', 'Ghost'] }),
    ]);

    renderPage();

    await waitFor(() => expect(screen.getByText('Smith — Lead PM')).toBeTruthy());
    // 'Ghost' has no matching team member, so it renders as just the name.
    expect(screen.getByText('Ghost')).toBeTruthy();
  });
});
