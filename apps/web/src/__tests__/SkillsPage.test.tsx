import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SkillsPage } from '../pages/SkillsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import userEvent from '@testing-library/user-event';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type {
  ApplyBlueprintSkillDefaultsResponse,
  BlueprintSkillDefaultsPreviewResponse,
  Project,
  SkillAcquisitionResponse,
  SkillDto,
  SkillMarketplaceBrowseResponse,
  SkillMarketplaceDto,
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
    listSkillMarketplaces: vi.fn(),
    browseSkillMarketplace: vi.fn(),
    importMarketplaceSkills: vi.fn(),
    addSkillMarketplaceSource: vi.fn(),
    removeSkillMarketplaceSource: vi.fn(),
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

function ProjectNavigation() {
  const navigate = useNavigate();
  return (
    <>
      <button onClick={() => navigate('/projects/proj-001/skills')}>Navigate to project A</button>
      <button onClick={() => navigate('/projects/proj-002/skills')}>Navigate to project B</button>
    </>
  );
}

function renderNavigablePage() {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={['/projects/proj-001/skills']}>
        <ProjectNavigation />
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

function deferred<T>() {
  let resolve: (value: T | PromiseLike<T>) => void;
  let reject: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve: resolve!, reject: reject! };
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

  it('renders built-in catalog skill provenance emitted by the server', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([makeSkill({ provenance: 'built-in' })]);

    renderPage();

    expect(await screen.findByText('built-in')).toBeTruthy();
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
  it('previews available defaults for a blank manually cast project without requiring source blueprint metadata', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockResolvedValue(makeProject({
      origin: 'blank',
      source_blueprint_id: null,
      source_blueprint_type: null,
    }));
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledWith(
      'proj-001',
      'blueprint-software-development',
    ));
    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.queryByText(/was not created from a predefined blueprint/)).toBeNull();
  });

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

  it('renders a structured 422 preview as blockers, actions, and provenance instead of raw API text', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const blockedPreview = makeDefaultsPreview({
      can_apply: false,
      errors: ['A confirmed team is required before defaults can be applied.'],
      assignments: [{
        role_id: 'frontend-engineer',
        agent_name: 'Trinity',
        skill_name: 'ui-accessibility',
        action: 'blocked',
      }],
    });
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockRejectedValue(
      new ApiError(422, JSON.stringify(blockedPreview)),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(screen.getByText('Defaults are blocked. Resolve the listed blockers before applying.')).toBeTruthy());
    expect(screen.getByText('Blockers')).toBeTruthy();
    expect(screen.getByText('A confirmed team is required before defaults can be applied.')).toBeTruthy();
    expect(screen.getByText('blocked')).toBeTruthy();
    expect(screen.getByText(/Source: predefined blueprint/)).toBeTruthy();
    expect(screen.queryByText(/API error 422:/)).toBeNull();
    expect((screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('renders the exact structured apply 422 response with its nested preview', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const blockedPreview = makeDefaultsPreview({
      can_apply: false,
      errors: ['The confirmed team changed while defaults were being applied.'],
      assignments: [{
        role_id: 'frontend-engineer',
        agent_name: 'Trinity',
        skill_name: 'ui-accessibility',
        action: 'blocked',
      }],
    });
    const response: ApplyBlueprintSkillDefaultsResponse = {
      outcome: 'invalid',
      errors: ['The defaults request is no longer valid.'],
      preview: blockedPreview,
    };
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockRejectedValue(
      new ApiError(422, JSON.stringify(response)),
    );

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Apply defaults' }));

    await waitFor(() => expect(screen.getByText(/The defaults request is no longer valid/)).toBeTruthy());
    expect(screen.getByText('Defaults are blocked. Resolve the listed blockers before applying.')).toBeTruthy();
    expect(screen.getByText('The confirmed team changed while defaults were being applied.')).toBeTruthy();
    expect(screen.getByText('blocked')).toBeTruthy();
    expect(screen.getByText(/Source: predefined blueprint/)).toBeTruthy();
    expect(screen.queryByText(/API error 422:/)).toBeNull();
  });

  it('disables defaults for inline projects without sending the inline source id', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockResolvedValue(makeProject({
      source_blueprint_id: 'inline',
      source_blueprint_type: 'inline',
    }));

    renderPage();

    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' }) as HTMLButtonElement;
    await waitFor(() => expect(trigger.disabled).toBe(true));
    expect(screen.getByText(/unavailable because this project uses an inline blueprint/)).toBeTruthy();
    expect(apiClient.previewBlueprintSkillDefaults).not.toHaveBeenCalled();
  });

  it('disables defaults for custom projects with an accessible explanation', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockResolvedValue(makeProject({
      source_blueprint_id: 'custom-blueprint',
      source_blueprint_type: 'custom',
    }));

    renderPage();

    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' }) as HTMLButtonElement;
    await waitFor(() => expect(trigger.disabled).toBe(true));
    expect(screen.getByText(/unavailable because this project uses a custom blueprint/)).toBeTruthy();
    expect(trigger.getAttribute('aria-describedby')).toBe('blueprint-defaults-availability');
    expect(apiClient.previewBlueprintSkillDefaults).not.toHaveBeenCalled();
  });

  it('uses the predefined source id for projects created from a predefined blueprint', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockResolvedValue(makeProject({
      source_blueprint_id: 'blueprint-platform-engineering',
      source_blueprint_type: 'predefined',
    }));
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview({
      blueprint_id: 'blueprint-platform-engineering',
    }));

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledWith(
      'proj-001',
      'blueprint-platform-engineering',
    ));
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

  it('preserves the last preview when an invalid 422 response has no replacement preview', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview({ digest: 'still-visible', assignments: [{
      role_id: 'frontend-engineer',
      agent_name: 'Trinity',
      skill_name: 'ui-accessibility',
      action: 'create',
    }] });
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockRejectedValue(new ApiError(422, JSON.stringify({
      outcome: 'invalid',
      errors: ['The selected role must be confirmed.'],
      preview: null,
    })));

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Apply defaults' }));

    await waitFor(() => expect(screen.getByText(/The selected role must be confirmed/)).toBeTruthy());
    expect(screen.getByText('ui-accessibility')).toBeTruthy();
    expect(screen.getByText(/preview still-visible/)).toBeTruthy();
    expect(screen.queryByText(/This preview is stale/)).toBeNull();
    expect(screen.queryByText('Preview latest defaults')).toBeNull();
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

  it('issues one apply for immediate double-clicks and preserves the successful result', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview();
    const apply = deferred<{
      outcome: 'applied';
      errors: string[];
      preview: BlueprintSkillDefaultsPreviewResponse;
    }>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(apply.promise);

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    const applyButton = await screen.findByRole('button', { name: 'Apply defaults' });
    fireEvent.click(applyButton);
    fireEvent.click(applyButton);

    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledTimes(1);
    apply.resolve({ outcome: 'applied', errors: [], preview });

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy();
  });

  it('keeps a dismissed apply in flight, blocks a second apply, and reconciles a late success', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview();
    const firstApply = deferred<{
      outcome: 'applied';
      errors: string[];
      preview: BlueprintSkillDefaultsPreviewResponse;
    }>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(firstApply.promise);
    const user = userEvent.setup();

    renderPage();
    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' });
    await user.click(trigger);
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledTimes(1);

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
      expect((trigger as HTMLButtonElement).disabled).toBe(false);
    });

    await user.click(trigger);
    expect(await screen.findByText(/This request will continue if you close this dialog/)).toBeTruthy();
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledTimes(1);

    firstApply.resolve({ outcome: 'applied', errors: [], preview });
    await waitFor(() => expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy());
    await waitFor(() => expect(apiClient.listSkills).toHaveBeenCalledTimes(2));
    expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText('Version 2026.07.16')).toBeNull();
  });

  it('renders a late dismissed 422 response in the reopened dialog', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview();
    const firstApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(firstApply.promise);
    const user = userEvent.setup();

    renderPage();
    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' });
    await user.click(trigger);
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await user.keyboard('{Escape}');
    await user.click(trigger);
    await screen.findByText(/This request will continue if you close this dialog/);

    firstApply.reject(new ApiError(422, JSON.stringify({
      outcome: 'invalid',
      errors: ['Late validation response.'],
      preview: makeDefaultsPreview({ can_apply: false, errors: ['Late preview blocker.'] }),
    })));

    await waitFor(() => expect(screen.getByText(/Late validation response/)).toBeTruthy());
    expect(screen.getByText('Late preview blocker.')).toBeTruthy();
    expect(screen.getByText('create')).toBeTruthy();
  });

  it('renders a late dismissed 409 response in the reopened dialog and requires re-preview', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const preview = makeDefaultsPreview({ digest: 'stale-digest' });
    const firstApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(firstApply.promise);
    const user = userEvent.setup();

    renderPage();
    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' });
    await user.click(trigger);
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await user.keyboard('{Escape}');
    await user.click(trigger);
    await screen.findByText(/This request will continue if you close this dialog/);

    firstApply.reject(new ApiError(409, JSON.stringify({
      outcome: 'stale',
      errors: ['The project changed after this preview was generated.'],
      preview: makeDefaultsPreview({
        can_apply: false,
        errors: ['Late stale preview blocker.'],
        assignments: [{
          role_id: 'frontend-engineer',
          agent_name: 'Trinity',
          skill_name: 'ui-accessibility',
          action: 'blocked',
        }],
      }),
    })));

    await waitFor(() => expect(screen.getByText(/This preview is stale/)).toBeTruthy());
    expect(screen.getByText('ui-accessibility')).toBeTruthy();
    expect(screen.getByText('Late stale preview blocker.')).toBeTruthy();
    expect(screen.getByText('blocked')).toBeTruthy();
    expect(screen.getByText('Preview latest defaults')).toBeTruthy();
  });

  it('isolates a late project A success from project B while allowing project B to apply', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockImplementation((id) => Promise.resolve(makeProject({ project_id: id })));
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());
    const projectAApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    const projectBApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockImplementation((id) =>
      id === 'proj-001' ? projectAApply.promise : projectBApply.promise,
    );
    const user = userEvent.setup();

    renderNavigablePage();
    await user.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await user.click(screen.getByRole('button', { name: 'Navigate to project B' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    await user.click(screen.getByRole('button', { name: 'Preview blueprint defaults' }));
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledWith(
      'proj-002',
      'blueprint-software-development',
      'preview-digest-1',
    );

    const projectBLoadsBeforeAResult = vi.mocked(apiClient.listSkills).mock.calls
      .filter(([id]) => id === 'proj-002').length;
    projectAApply.resolve({ outcome: 'applied', errors: [], preview: makeDefaultsPreview() });

    await waitFor(() => expect(vi.mocked(apiClient.listSkills).mock.calls
      .filter(([id]) => id === 'proj-002').length).toBe(projectBLoadsBeforeAResult));
    expect(screen.queryByText('Blueprint defaults applied.')).toBeNull();

    projectBApply.resolve({ outcome: 'applied', errors: [], preview: makeDefaultsPreview() });
    await waitFor(() => expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy());
    await waitFor(() => expect(vi.mocked(apiClient.listSkills).mock.calls
      .filter(([id]) => id === 'proj-002').length).toBe(projectBLoadsBeforeAResult + 1));
  });

  it('isolates a late project A error from project B', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockImplementation((id) => Promise.resolve(makeProject({ project_id: id })));
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());
    const projectAApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(projectAApply.promise);
    const user = userEvent.setup();

    renderNavigablePage();
    await user.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await user.click(screen.getByRole('button', { name: 'Navigate to project B' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    const projectBLoadsBeforeAResult = vi.mocked(apiClient.listSkills).mock.calls
      .filter(([id]) => id === 'proj-002').length;
    projectAApply.reject(new ApiError(422, JSON.stringify({
      outcome: 'invalid',
      errors: ['Project A validation error.'],
      preview: null,
    })));

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByText(/Project A validation error/)).toBeNull();
    expect(screen.queryByText('Blueprint defaults applied.')).toBeNull();
    expect(vi.mocked(apiClient.listSkills).mock.calls
      .filter(([id]) => id === 'proj-002').length).toBe(projectBLoadsBeforeAResult);
  });

  it('clears a project A success notice while navigating to B and back to A', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockImplementation((id) => Promise.resolve(makeProject({ project_id: id })));
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockResolvedValue({
      outcome: 'applied',
      errors: [],
      preview: makeDefaultsPreview(),
    });
    const user = userEvent.setup();

    renderNavigablePage();
    await user.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await screen.findByText('Blueprint defaults applied.');

    await user.click(screen.getByRole('button', { name: 'Navigate to project B' }));
    await waitFor(() => expect(screen.queryByText('Blueprint defaults applied.')).toBeNull());
    await user.click(screen.getByRole('button', { name: 'Navigate to project A' }));
    await waitFor(() => expect(screen.queryByText('Blueprint defaults applied.')).toBeNull());
  });

  it('restores project A’s in-flight apply dialog after navigating A to B to A', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.getProject).mockImplementation((id) => Promise.resolve(makeProject({ project_id: id })));
    const preview = makeDefaultsPreview({ digest: 'project-a-digest' });
    const projectAApply = deferred<ApplyBlueprintSkillDefaultsResponse>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(preview);
    vi.mocked(apiClient.applyBlueprintSkillDefaults).mockReturnValue(projectAApply.promise);
    const user = userEvent.setup();

    renderNavigablePage();
    await user.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));
    await user.click(await screen.findByRole('button', { name: 'Apply defaults' }));
    await user.click(screen.getByRole('button', { name: 'Navigate to project B' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    await user.click(screen.getByRole('button', { name: 'Navigate to project A' }));
    await screen.findByText(/This request will continue if you close this dialog/);
    expect(screen.getByText(/preview project-a-digest/)).toBeTruthy();
    const applying = screen.getByRole('button', { name: /Applying/ }) as HTMLButtonElement;
    expect(applying.disabled).toBe(true);
    await user.click(applying);
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledTimes(1);

    projectAApply.resolve({ outcome: 'applied', errors: [], preview });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('Blueprint defaults applied.')).toBeTruthy();
  });

  it('ignores a late preview failure after a newer preview has completed', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const firstPreview = deferred<BlueprintSkillDefaultsPreviewResponse>();
    const secondPreview = deferred<BlueprintSkillDefaultsPreviewResponse>();
    vi.mocked(apiClient.previewBlueprintSkillDefaults)
      .mockReturnValueOnce(firstPreview.promise)
      .mockReturnValueOnce(secondPreview.promise);

    renderPage();
    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' });
    fireEvent.click(trigger);
    await waitFor(() => expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect((trigger as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(trigger);
    await waitFor(() => expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledTimes(2));

    secondPreview.resolve(makeDefaultsPreview({ blueprint_version: 'newer-version' }));
    await waitFor(() => expect(screen.getByText('Version newer-version')).toBeTruthy());
    firstPreview.reject(new ApiError(500, 'late preview failure'));

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.getByText('Version newer-version')).toBeTruthy();
    expect(screen.queryByText('API error 500: late preview failure')).toBeNull();
  });

  it('dismisses the preview dialog through Escape and backdrop clicks and restores trigger focus', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview());
    const user = userEvent.setup();

    renderPage();
    const trigger = await screen.findByRole('button', { name: 'Preview blueprint defaults' });
    await user.click(trigger);
    // A longer timeout guards against full-suite parallel-worker CPU
    // contention delaying Fluent UI's dialog mount/focus-trap wiring (same
    // rationale as the CoordinatorRunPage dialog tests).
    await screen.findByRole('dialog', {}, { timeout: 4000 });

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
      expect((trigger as HTMLButtonElement).disabled).toBe(false);
    }, { timeout: 4000 });

    await user.click(trigger);
    await screen.findByRole('dialog', {}, { timeout: 4000 });
    const backdrop = document.querySelector<HTMLElement>('[class*="fui-DialogSurface__backdrop"]');
    expect(backdrop).toBeTruthy();
    fireEvent.click(backdrop!);

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
    }, { timeout: 4000 });
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

describe('SkillsPage — curated marketplaces', () => {
  const marketplace: SkillMarketplaceDto = {
    name: 'GitHub Awesome Copilot',
    repository: 'github/awesome-copilot',
    subpath: 'skills',
    layout_note: null,
  };

  it('shows a loading state then renders candidates while browsing a marketplace', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces).mockResolvedValue([marketplace]);
    const browse = deferred<SkillMarketplaceBrowseResponse>();
    vi.mocked(apiClient.browseSkillMarketplace).mockReturnValue(browse.promise);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    fireEvent.click(await screen.findByRole('button', { name: 'GitHub Awesome Copilot' }));

    // While the browse request is in-flight the dialog must not silently freeze: a loading
    // indicator is shown (regression guard for the marketplace-browse hang).
    await waitFor(() => expect(screen.getByLabelText('Loading')).toBeTruthy());

    browse.resolve({
      marketplace: 'GitHub Awesome Copilot',
      candidates: [{ location: 'skills/pr-review', name: 'pr-review', description: 'Reviews PRs.', valid: true, resource_count: 0, errors: [] }],
      total: 1,
      page: 1,
      page_size: 25,
      has_more: false,
    });

    expect(await screen.findByText('pr-review')).toBeTruthy();
    expect(screen.getByText('Reviews PRs.')).toBeTruthy();
  });

  it('paginates the browse: Load more requests the next page and appends candidates', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces).mockResolvedValue([marketplace]);
    vi.mocked(apiClient.browseSkillMarketplace)
      .mockResolvedValueOnce({
        marketplace: 'GitHub Awesome Copilot',
        candidates: [{ location: 'skills/a', name: 'skill-a', description: 'First.', valid: true, resource_count: 0, errors: [] }],
        total: 2,
        page: 1,
        page_size: 25,
        has_more: true,
      })
      .mockResolvedValueOnce({
        marketplace: 'GitHub Awesome Copilot',
        candidates: [{ location: 'skills/b', name: 'skill-b', description: 'Second.', valid: true, resource_count: 0, errors: [] }],
        total: 2,
        page: 2,
        page_size: 25,
        has_more: false,
      });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    fireEvent.click(await screen.findByRole('button', { name: 'GitHub Awesome Copilot' }));

    expect(await screen.findByText('skill-a')).toBeTruthy();
    expect(screen.getByText('Showing 1 of 2')).toBeTruthy();

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    // Page 2 candidates are appended (page 1 remains visible), and the Load more control is gone.
    expect(await screen.findByText('skill-b')).toBeTruthy();
    expect(screen.getByText('skill-a')).toBeTruthy();
    expect(screen.getByText('Showing 2 of 2')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();

    expect(vi.mocked(apiClient.browseSkillMarketplace)).toHaveBeenNthCalledWith(1, expect.any(String), 'GitHub Awesome Copilot', undefined, 1, 25);
    expect(vi.mocked(apiClient.browseSkillMarketplace)).toHaveBeenNthCalledWith(2, expect.any(String), 'GitHub Awesome Copilot', undefined, 2, 25);
  });

  it('surfaces a browse error inside the dialog instead of freezing', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces).mockResolvedValue([marketplace]);
    vi.mocked(apiClient.browseSkillMarketplace).mockRejectedValue(
      new ApiError(422, JSON.stringify({ error: 'Timed out while reading the marketplace source. Please try again in a moment.' })),
    );

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    fireEvent.click(await screen.findByRole('button', { name: 'GitHub Awesome Copilot' }));

    await waitFor(() => expect(screen.getByText(/Timed out while reading the marketplace source/)).toBeTruthy());
    // The loading indicator must be gone once the error is shown.
    expect(screen.queryByLabelText('Loading')).toBeNull();
  });
});

describe('SkillsPage — add/remove a marketplace source by URL', () => {
  const configMarketplace: SkillMarketplaceDto = {
    name: 'GitHub Awesome Copilot',
    repository: 'github/awesome-copilot',
    subpath: 'skills',
    layout_note: null,
  };
  const projectSource: SkillMarketplaceDto = {
    name: 'my-org/my-skills',
    repository: 'my-org/my-skills',
    branch: 'main',
    subpath: null,
    auto_detect: true,
    parse_strategy: 'auto',
    project_source: true,
  };

  it('adds a source by URL, refreshes the list, and browses it', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces)
      .mockResolvedValueOnce([configMarketplace])
      .mockResolvedValueOnce([configMarketplace, projectSource]);
    vi.mocked(apiClient.addSkillMarketplaceSource).mockResolvedValue(projectSource);
    vi.mocked(apiClient.browseSkillMarketplace).mockResolvedValue({
      marketplace: 'my-org/my-skills',
      candidates: [],
      total: 0,
      page: 1,
      page_size: 25,
      has_more: false,
    });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    const repoInput = await screen.findByPlaceholderText('https://github.com/org/skills-repo');
    fireEvent.change(repoInput, { target: { value: 'https://github.com/my-org/my-skills' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add source' }));

    await waitFor(() => expect(apiClient.addSkillMarketplaceSource).toHaveBeenCalledWith(
      expect.any(String),
      expect.objectContaining({ repository: 'https://github.com/my-org/my-skills', parseStrategy: 'auto' }),
    ));

    // The list is refreshed and the new source shows up alongside the config one.
    expect(await screen.findByRole('button', { name: 'my-org/my-skills' })).toBeTruthy();
    // The newly added source is auto-browsed.
    await waitFor(() => expect(apiClient.browseSkillMarketplace).toHaveBeenCalledWith(
      expect.any(String), 'my-org/my-skills', undefined, 1, 25,
    ));
  });

  it('surfaces a friendly 409 conflict message when adding a duplicate source', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces).mockResolvedValue([configMarketplace]);
    vi.mocked(apiClient.addSkillMarketplaceSource).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'A marketplace named "GitHub Awesome Copilot" already exists.' })),
    );

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    const repoInput = await screen.findByPlaceholderText('https://github.com/org/skills-repo');
    fireEvent.change(repoInput, { target: { value: 'github/awesome-copilot' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add source' }));

    await waitFor(() => expect(screen.getByText('A marketplace named "GitHub Awesome Copilot" already exists.')).toBeTruthy());
  });

  it('surfaces a friendly 422 message when the repository is not public', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces).mockResolvedValue([configMarketplace]);
    vi.mocked(apiClient.addSkillMarketplaceSource).mockRejectedValue(new ApiError(422, JSON.stringify({})));

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    const repoInput = await screen.findByPlaceholderText('https://github.com/org/skills-repo');
    fireEvent.change(repoInput, { target: { value: 'https://github.com/private/repo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add source' }));

    await waitFor(() => expect(screen.getByText('That repository is not public or is unavailable right now.')).toBeTruthy());
  });

  it('shows a remove affordance only for project-added sources and removes on click', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.listSkillMarketplaces)
      .mockResolvedValueOnce([configMarketplace, projectSource])
      .mockResolvedValueOnce([configMarketplace]);
    vi.mocked(apiClient.removeSkillMarketplaceSource).mockResolvedValue(undefined);

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Browse marketplaces' }));
    await screen.findByRole('button', { name: 'my-org/my-skills' });

    // Built-in config sources have no remove affordance.
    expect(screen.queryByRole('button', { name: 'Remove GitHub Awesome Copilot' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Remove my-org/my-skills' }));

    await waitFor(() => expect(apiClient.removeSkillMarketplaceSource).toHaveBeenCalledWith(expect.any(String), 'my-org/my-skills'));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'my-org/my-skills' })).toBeNull());
  });
});
