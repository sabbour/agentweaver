import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
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
import type {
  BlueprintSkillDefaultsPreviewResponse,
  Project,
  SkillAcquisitionResponse,
  SkillDto,
  TeamDto,
  TeamMemberDto,
} from '../api/types';
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
    getProject: vi.fn(),
    previewBlueprintSkillDefaults: vi.fn(),
    applyBlueprintSkillDefaults: vi.fn(),
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

function makeProject(overrides: Partial<Project> = {}): Project {
  return {
    project_id: 'proj-001',
    name: 'Project',
    origin: 'blank',
    source_repository: null,
    working_directory: 'C:\\workspace\\project',
    default_branch: 'main',
    owner: 'owner',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
    available: true,
    state: 'active',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    source_blueprint_id: 'blueprint-software-development',
    source_blueprint_type: 'predefined',
    allowed_workflow_ids: null,
    ...overrides,
  };
}

function makeDefaultsPreview(overrides: Partial<BlueprintSkillDefaultsPreviewResponse> = {}): BlueprintSkillDefaultsPreviewResponse {
  return {
    blueprint_id: 'blueprint-software-development',
    blueprint_version: '2026.07.16',
    digest: 'preview-digest-1',
    can_apply: true,
    errors: [],
    assignments: [{
      role_id: 'frontend-engineer',
      agent_name: 'Trinity',
      skill_name: 'ui-accessibility',
      action: 'create',
    }],
    ...overrides,
  };
}

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(apiClient.getTeam).mockResolvedValue(makeTeam([makeMember('Smith', 'Lead PM'), makeMember('Neo', 'Lead Architect')]));
  vi.mocked(apiClient.getProject).mockResolvedValue(makeProject());
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

describe('SkillsPage — blueprint defaults', () => {
  it('renders the backend preview assignments, including blocked manual collisions and reactivations', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview({
      assignments: [
        { role_id: 'frontend-engineer', skill_name: 'ui-accessibility', action: 'create', agent_name: 'Trinity' },
        { role_id: 'qa-engineer', skill_name: 'test-strategy-reproduction', action: 'reactivate', agent_name: 'Smith' },
        { role_id: 'frontend-engineer', skill_name: 'ui-accessibility', action: 'assign', agent_name: 'Trinity' },
        { role_id: 'frontend-engineer', skill_name: 'manual-skill', action: 'blocked', agent_name: 'Trinity' },
      ],
    }));

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
    expect(screen.getByText('create')).toBeTruthy();
    expect(screen.getByText('reactivate')).toBeTruthy();
    expect(screen.getByText('assign')).toBeTruthy();
    expect(screen.getByText('blocked')).toBeTruthy();
    expect(screen.getByText('A manually managed skill has the same name and will not be changed.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('requires a new preview after stale state, then applies exactly the new digest', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const initial = makeDefaultsPreview({ digest: 'old-digest', blueprint_version: 'v1' });
    const refreshed = makeDefaultsPreview({ digest: 'new-digest', blueprint_version: 'v2' });
    vi.mocked(apiClient.previewBlueprintSkillDefaults)
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(refreshed);
    vi.mocked(apiClient.applyBlueprintSkillDefaults)
      .mockRejectedValueOnce(new ApiError(409, 'preview digest is stale'))
      .mockResolvedValueOnce({ outcome: 'applied', errors: [], preview: refreshed });

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await waitFor(() => expect(screen.getByText('Version v1')).toBeTruthy());
    const firstApply = await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement;
      expect(button.disabled).toBe(false);
      return button;
    });
    fireEvent.click(firstApply);

    await waitFor(() => expect(screen.getByText(/This preview is stale/)).toBeTruthy());
    await waitFor(() => expect(screen.getByText('Preview latest defaults')).toBeTruthy());
    fireEvent.click(screen.getByText('Preview latest defaults'));
    await waitFor(() => expect(screen.getByText('Version v2')).toBeTruthy());
    expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenLastCalledWith('proj-001', 'blueprint-software-development');
    expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledTimes(2);
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledWith('proj-001', 'blueprint-software-development', 'old-digest');
  });

  it('applies a matching preview only after sending its blueprint id and digest', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview({ digest: 'matching-digest' });
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockResolvedValue({
      outcome: 'applied',
      errors: [],
      preview,
    });

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    const apply = await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement;
      expect(button.disabled).toBe(false);
      return button;
    });
    fireEvent.click(apply);

    await waitFor(() => expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledWith(
      'proj-001',
      'blueprint-software-development',
      'matching-digest',
    ));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy();
  });

  it('cancels an in-flight preview when the dialog closes', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    let resolveProject: ((project: Project) => void) | undefined;
    vi.mocked(apiClient.getProject).mockImplementation(
      () => new Promise((resolve) => { resolveProject = resolve; }),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await waitFor(() => expect(resolveProject).toEqual(expect.any(Function)));

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    resolveProject!(makeProject());
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(apiClient.previewBlueprintSkillDefaults).not.toHaveBeenCalled();
    expect(screen.queryByText('Blueprint defaults applied.')).toBeNull();
  });

  it('renders preview API errors without presenting a successful default application', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockRejectedValue(new ApiError(500, 'preview unavailable'));

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(screen.getByText('API error 500: preview unavailable')).toBeTruthy());
    expect(screen.queryByText(/Blueprint defaults applied/)).toBeNull();
  });
});
