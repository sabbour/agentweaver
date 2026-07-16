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

function makeDefaultsPreview(overrides: Partial<BlueprintSkillDefaultsPreviewResponse> = {}): BlueprintSkillDefaultsPreviewResponse {
  return {
    digest: 'preview-digest-1',
    blueprint: { id: 'blueprint-software-development', version: '2026.07.16' },
    agent_resolutions: [{
      role_id: 'frontend-engineer',
      agent_name: 'Trinity',
      agent_status: 'active',
      confirmed: true,
    }],
    skill_actions: [{
      role_id: 'frontend-engineer',
      skill_name: 'ui-accessibility',
      action: 'create',
      agent_name: 'Trinity',
      provenance: { source: 'built-in-catalog', source_location: 'skills/ui-accessibility/SKILL.md' },
    }],
    blockers: [],
    provenance: [{ source: 'blueprint-catalog', detail: 'Blueprint binding', source_location: 'blueprints/software-development.json' }],
    ...overrides,
  };
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

describe('SkillsPage — blueprint defaults', () => {
  it('previews resolved active agents, proposed actions, blockers, and provenance accessibly', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    vi.mocked(apiClient.previewBlueprintSkillDefaults).mockResolvedValue(makeDefaultsPreview({
      agent_resolutions: [
        { role_id: 'frontend-engineer', agent_name: 'Trinity', agent_status: 'active', confirmed: true },
        { role_id: 'qa-engineer', agent_name: 'Smith', agent_status: 'inactive', confirmed: true },
      ],
      skill_actions: [
        { role_id: 'frontend-engineer', skill_name: 'ui-accessibility', action: 'create', agent_name: 'Trinity', provenance: { source: 'built-in-catalog' } },
        { role_id: 'qa-engineer', skill_name: 'test-strategy-reproduction', action: 'reactivate', agent_name: 'Smith' },
        { role_id: 'frontend-engineer', skill_name: 'ui-accessibility', action: 'assign', agent_name: 'Trinity' },
        { role_id: 'frontend-engineer', skill_name: 'manual-skill', action: 'block', reason: 'Manual skill collision' },
      ],
      blockers: [{ code: 'manual-collision', message: 'A manually managed skill conflicts with this default.', role_id: 'frontend-engineer', skill_name: 'manual-skill' }],
    }));

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Preview blueprint defaults' }));

    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
    expect(screen.getByText('active confirmed')).toBeTruthy();
    expect(screen.getByText('inactive')).toBeTruthy();
    expect(screen.getByText('create')).toBeTruthy();
    expect(screen.getByText('reactivate')).toBeTruthy();
    expect(screen.getByText('assign')).toBeTruthy();
    expect(screen.getByText('block')).toBeTruthy();
    expect(screen.getByText('A manually managed skill conflicts with this default.')).toBeTruthy();
    expect(screen.getByText(/blueprint-catalog — Blueprint binding/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('clears stale state on close, requires a new preview, then applies exactly the new digest', async () => {
    vi.mocked(apiClient.listSkills).mockResolvedValue([]);
    const initial = makeDefaultsPreview({ digest: 'old-digest', blueprint: { id: 'blueprint-software-development', version: 'v1' } });
    const refreshed = makeDefaultsPreview({ digest: 'new-digest', blueprint: { id: 'blueprint-software-development', version: 'v2' } });
    vi.mocked(apiClient.previewBlueprintSkillDefaults)
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(refreshed);
    vi.mocked(apiClient.applyBlueprintSkillDefaults)
      .mockRejectedValueOnce(new ApiError(409, 'preview digest is stale'))
      .mockResolvedValueOnce({ digest: 'new-digest', applied: true, actions: refreshed.skill_actions, blockers: [] });

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
    expect((screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement).disabled).toBe(true);
    const close = await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Close' }) as HTMLButtonElement;
      expect(button.disabled).toBe(false);
      return button;
    });
    fireEvent.click(close);
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Preview blueprint defaults' }));
    await waitFor(() => expect(screen.getByText('Version v2')).toBeTruthy());
    const secondApply = await waitFor(() => {
      const button = screen.getByRole('button', { name: 'Apply defaults' }) as HTMLButtonElement;
      expect(button.disabled).toBe(false);
      return button;
    });
    fireEvent.click(secondApply);

    await waitFor(() => expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenLastCalledWith('proj-001', 'new-digest'));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByText('Blueprint defaults applied: 1 action.')).toBeTruthy();
    expect(apiClient.previewBlueprintSkillDefaults).toHaveBeenCalledTimes(2);
    expect(apiClient.applyBlueprintSkillDefaults).toHaveBeenCalledTimes(2);
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
