import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SkillsPage } from '../pages/SkillsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
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
    expect(screen.queryByText('Version 2026.07.16')).toBeNull();
  });

  it('does not let a late dismissed stale response overwrite a reopened dialog', async () => {
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
      errors: ['Late stale response.'],
      preview: makeDefaultsPreview({ can_apply: false, errors: ['Late preview blocker.'] }),
    })));

    await waitFor(() => expect((trigger as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByText(/Late stale response/)).toBeNull();
    expect(screen.queryByText(/Late preview blocker/)).toBeNull();
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
    await screen.findByRole('dialog');

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
      expect((trigger as HTMLButtonElement).disabled).toBe(false);
    });

    await user.click(trigger);
    await screen.findByRole('dialog');
    const backdrop = document.querySelector<HTMLElement>('[class*="fui-DialogSurface__backdrop"]');
    expect(backdrop).toBeTruthy();
    fireEvent.click(backdrop!);

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
    });
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
