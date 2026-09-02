import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ProjectSettingsPage } from '../pages/ProjectSettingsPage';
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
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthConfig: vi.fn(),
    getProject: vi.fn(),
    getServerInfo: vi.fn(),
    getSandboxPolicy: vi.fn(),
    getProjectAccessOverview: vi.fn(),
    createProjectRoleAssignment: vi.fn(),
    deleteProjectRoleAssignment: vi.fn(),
    updateProjectProviderSettings: vi.fn(),
    updateProjectPreviewSettings: vi.fn(),
    updateSandboxPolicy: vi.fn(),
    getUnattendedReadiness: vi.fn(),
    getAutomationStatus: vi.fn(),
    activateAutomation: vi.fn(),
    deactivateAutomation: vi.fn(),
    beginProjectCopilotAuthorization: vi.fn(),
    beginProjectRepoAppInstallation: vi.fn(),
    listProjectRepositoryOwners: vi.fn(),
    listGitHubRepositorySelections: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
    getPlatformDefaultCopilotConnection: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId: string, initialEntry = `/projects/${projectId}/settings`) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/projects/:projectId/settings" element={<ProjectSettingsPage />} />
        </Routes>
      </MemoryRouter>
    </Wrapper>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getAuthConfig).mockResolvedValue({
    mode: 'Entra',
    entra: {
      tenant_id: 'tenant-1',
      client_id: 'client-1',
      enterprise_app_object_id: null,
      authority: 'https://login.microsoftonline.com/tenant-1/v2.0',
    },
  });
  vi.mocked(apiClient.getProject).mockResolvedValue({
    project_id: 'proj-1',
    name: 'Demo',
    origin: 'blank',
    source_repository: null,
    working_directory: 'C:/demo',
    default_branch: 'main',
    owner: 'sabbour',
    default_provider: 'github-copilot',
    default_model_github_copilot: 'gpt-4',
    default_model_microsoft_foundry: null,
    blueprint_generation_model: null,
    workflow_generation_model: 'claude-sonnet-4.6',
    outcome_spec_generation_model: null,
    preview_approval_timeout_minutes: 30,
    available: true,
    state: 'active',
    created_at: '2026-07-07T00:00:00Z',
    updated_at: '2026-07-07T00:00:00Z',
  } as never);
  vi.mocked(apiClient.updateProjectProviderSettings).mockResolvedValue(undefined as never);
  vi.mocked(apiClient.updateProjectPreviewSettings).mockResolvedValue({
    approval_timeout_minutes: 45,
  } as never);
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({
    data_directory: 'C:/data',
    repo_app_install_url: 'https://github.com/apps/agentweaver-repo/installations/new',
  } as never);
  vi.mocked(apiClient.getSandboxPolicy).mockResolvedValue({
    repository_path: 'C:/demo',
    shell_enabled: true,
    direct: false,
    network_enabled: true,
    allowed_repository_roots: ['C:/demo'],
    destructive_command_patterns: [],
  } as never);
  vi.mocked(apiClient.getProjectAccessOverview).mockResolvedValue({
    auth_mode: 'entra',
    platform_roles: ['PlatformAdmin'],
    platform_roles_source: 'entra',
    current_user_project_role: 'Owner',
    can_manage_role_assignments: true,
    project_role_assignments: [
      {
        assignment_id: 'assign-1',
        principal_id: 'person@contoso.com',
        display_name: 'Ada Lovelace',
        email: 'person@contoso.com',
        role: 'Owner',
        scope: 'Project:proj-1',
      },
    ],
  } as never);
  vi.mocked(apiClient.createProjectRoleAssignment).mockResolvedValue(undefined as never);
  vi.mocked(apiClient.deleteProjectRoleAssignment).mockResolvedValue(undefined as never);
  vi.mocked(apiClient.getUnattendedReadiness).mockResolvedValue({
    status: 'not_ready',
    reason_code: 'copilot_binding_required',
    message: 'Connect a project Copilot App identity before unattended work can run.',
    repo_app_installation_connected: false,
  } as never);
  vi.mocked(apiClient.getAutomationStatus).mockResolvedValue({
    is_active: false,
    model_provider_source: null,
    activated_at: null,
  } as never);
  vi.mocked(apiClient.listProjectRepositoryOwners).mockResolvedValue([
    { login: 'octo', type: 'user' },
  ] as never);
  vi.mocked(apiClient.listGitHubRepositorySelections).mockResolvedValue({
    repositories: [
      { full_name: 'octo/repo', owner_login: 'octo', private: true, default_branch: 'main', pushed_at: null },
    ],
  } as never);
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'not_connected',
    github_login: null,
    effective_source: 'none',
  });
  vi.mocked(apiClient.getPlatformDefaultCopilotConnection).mockResolvedValue({
    connected: false,
    github_login: null,
  });
});

afterEach(() => {
  cleanup();
});

describe('ProjectSettingsPage', () => {
  it('renders the supported settings sections in the rail', async () => {
    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Project settings')).toBeDefined());

    const rail = screen.getByRole('navigation', { name: 'Settings sections' });
    expect(rail).toBeDefined();
    expect(screen.getByRole('button', { name: /General/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Access/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Sandbox policy/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Danger Zone/i })).toBeDefined();
  });

  it('switches the pane when a rail item is clicked', async () => {
    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Rename project')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    await waitFor(() => expect(screen.getByText('Sandbox enabled')).toBeDefined());
  });

  it('shows the AI source as a deployment-level note on the General section', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');

    expect(screen.getAllByText('AI source')).toHaveLength(2);
    expect(screen.getByText(
      'This project uses the AI source configured for the deployment. Change it in Platform settings.',
    )).toBeDefined();
    expect(screen.getByText('Deployment setting')).toBeDefined();
    expect(screen.queryByText('Default provider')).toBeNull();
  });

  it('shows background readiness alongside a real activation control', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    expect(await screen.findByText('Background automation readiness')).toBeDefined();
    expect(screen.getByText(
      /Authorize GitHub Copilot uses GitHub user OAuth to create a durable project binding/,
    )).toBeDefined();
    expect(screen.getByText(
      'These server checks apply after you add repository access. Local agent work can continue without a repository.',
    )).toBeDefined();
    expect(screen.getByText('model_provider_connection_required')).toBeDefined();
    expect(screen.getByText('not_required')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Manage GitHub Copilot' })).toBeDefined();
    // Automation is currently inactive (per the mocked status), so the activation control shows
    // "Activate", not "Deactivate" — proving this section is no longer purely read-only.
    expect(await screen.findByRole('button', { name: 'Activate automation' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Deactivate automation' })).toBeNull();
  });

  it('activates automation and then shows a Deactivate control once active', async () => {
    vi.mocked(apiClient.activateAutomation).mockResolvedValue({
      is_active: true,
      model_provider_source: 'github_copilot',
      activated_at: '2026-09-01T00:00:00Z',
    } as never);

    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    const activateButton = await screen.findByRole('button', { name: 'Activate automation' });
    fireEvent.click(activateButton);

    expect(await screen.findByRole('button', { name: 'Deactivate automation' })).toBeDefined();
    expect(apiClient.activateAutomation).toHaveBeenCalledWith('proj-1');
    expect(screen.getByText('GitHub Copilot')).toBeDefined();
  });

  it('hides the activation control for non-Owners (status endpoint returns 403)', async () => {
    vi.mocked(apiClient.getAutomationStatus).mockRejectedValue(new ApiError(403, 'Forbidden') as never);

    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    await screen.findByText('Background automation readiness');
    expect(screen.queryByRole('button', { name: /activate automation|deactivate automation/i })).toBeNull();
  });

  it('starts the Repo App installation flow when required', async () => {
    vi.mocked(apiClient.getUnattendedReadiness).mockResolvedValue({
      status: 'not_ready',
      reason_code: 'repo_app_installation_required',
      message: 'Install the Repo App for this project before unattended work can run.',
      repo_app_installation_connected: false,
    } as never);
    vi.mocked(apiClient.beginProjectRepoAppInstallation).mockResolvedValue({
      installation_url: 'https://github.com/apps/agentweaver-repo/installations/new?state=abc',
      transaction_id: 'txn-1',
      expires_at: '2026-07-07T00:10:00Z',
    });

    const assignSpy = vi.spyOn(window.location, 'assign').mockImplementation(() => {});

    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    const installButton = await screen.findByRole('button', { name: 'Install GitHub Repo App' });
    fireEvent.click(installButton);

    await waitFor(() => expect(apiClient.beginProjectRepoAppInstallation).toHaveBeenCalledWith('proj-1'));
    await waitFor(() => expect(assignSpy).toHaveBeenCalledWith('https://github.com/apps/agentweaver-repo/installations/new?state=abc'));
    expect(screen.getByRole('button', { name: 'Refresh status' })).toBeDefined();
    assignSpy.mockRestore();
  });

  it('renders nested model-provider and repository readiness independently', async () => {
    vi.mocked(apiClient.getUnattendedReadiness).mockResolvedValue({
      status: 'not_ready',
      reason_code: 'project_model_provider_reconnect_required',
      message: 'Legacy combined message.',
      repo_app_installation_connected: true,
      model_provider: {
        status: 'not_ready',
        source: 'project',
        reason_code: 'project_model_provider_reconnect_required',
      },
      repository: {
        required: true,
        status: 'ready',
        reason_code: 'ready',
        repo_app_installation_connected: true,
      },
    });
    vi.mocked(apiClient.getProject).mockResolvedValue({
      project_id: 'proj-1',
      name: 'Demo',
      origin: 'github',
      source_repository: 'octo/repo',
      working_directory: 'C:/demo',
      default_branch: 'main',
      owner: 'sabbour',
      default_provider: 'github-copilot',
      default_model_github_copilot: 'gpt-4',
      default_model_microsoft_foundry: null,
      blueprint_generation_model: null,
      workflow_generation_model: 'claude-sonnet-4.6',
      outcome_spec_generation_model: null,
      preview_approval_timeout_minutes: 30,
      available: true,
      state: 'active',
      created_at: '2026-07-07T00:00:00Z',
      updated_at: '2026-07-07T00:00:00Z',
    });

    renderPage('proj-1');
    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    expect(await screen.findByText('project_model_provider_reconnect_required')).toBeDefined();
    expect(screen.getByText('Repository access is ready for background automation.')).toBeDefined();
    expect(screen.queryByText('Legacy combined message.')).toBeNull();
    expect(screen.getByRole('button', { name: 'Manage GitHub Copilot' })).toBeDefined();
  });

  it('shows the platform default Copilot account instead of a broken project warning', async () => {
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'not_connected',
      github_login: 'platform-bot',
      effective_source: 'platform_default',
    });

    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    expect(await screen.findByText(
      'GitHub Copilot (@platform-bot) supplies AI access. Scope: Platform.',
    )).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Manage GitHub Copilot' })).toBeNull();
  });

  it('reopens repository setup after authorization succeeds', async () => {
    renderPage('proj-1', '/projects/proj-1/settings?section=repository&repo_app_auth=success');

    expect(await screen.findByRole('heading', { name: 'Set up repository access' })).toBeDefined();
    await waitFor(() => expect(apiClient.listProjectRepositoryOwners).toHaveBeenCalledWith('proj-1'));
    await waitFor(() => expect(apiClient.listGitHubRepositorySelections).toHaveBeenCalled());
  });

  it.each([
    ['success', null],
    ['human_entra_subject_required', 'Authorize repository access while signed in with your work account.'],
    ['authorization_transaction_invalid', 'Repository authorization could not be completed. Start a new authorization.'],
    ['authorization_transaction_consumed', 'This repository authorization has already been used. Start a new authorization.'],
    ['github_binding_unavailable', 'Repository authorization is currently unavailable. Try again later.'],
    ['rate_limited', 'GitHub is receiving too many authorization requests. Wait a moment and try again.'],
    ['unknown_result', 'Repository authorization could not be completed. Start a new authorization.'],
  ])('reopens repository setup after authorization result %s', async (result, message) => {
    renderPage('proj-1', `/projects/proj-1/settings?section=repository&repo_app_auth=${result}`);

    expect(await screen.findByRole('heading', { name: 'Set up repository access' })).toBeDefined();
    if (message) {
      expect(await screen.findByText(message)).toBeDefined();
    }
  });

  it('uses connected-repository wording for background requirements when a repo is attached', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      project_id: 'proj-1',
      name: 'Demo',
      origin: 'github',
      source_repository: 'sabbour/agentweaver',
      working_directory: 'C:/demo',
      default_branch: 'main',
      owner: 'sabbour',
      default_provider: 'github-copilot',
      default_model_github_copilot: 'gpt-4',
      default_model_microsoft_foundry: null,
      blueprint_generation_model: null,
      workflow_generation_model: 'claude-sonnet-4.6',
      outcome_spec_generation_model: null,
      preview_approval_timeout_minutes: 30,
      available: true,
      state: 'active',
      created_at: '2026-07-07T00:00:00Z',
      updated_at: '2026-07-07T00:00:00Z',
    } as never);

    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    expect(await screen.findByText(
      'These server checks cover branch, push, and pull-request work for the connected repository.',
    )).toBeDefined();
  });

  it('removes legacy identity and webhook controls', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    expect(screen.queryByRole('button', { name: /Webhooks/i })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Access/i }));

    expect(screen.queryByText('GitHub identity for this project')).toBeNull();
    expect(screen.queryByRole('button', { name: /Save GitHub identity/i })).toBeNull();
  });

  it('renders generation model overrides with blank fields inheriting gpt-5.4', async () => {
    renderPage('proj-1');

    const blueprint = await screen.findByRole('textbox', { name: 'Blueprint generation model' }) as HTMLInputElement;
    const workflow = screen.getByRole('textbox', { name: 'Workflow generation model' }) as HTMLInputElement;
    const outcome = screen.getByRole('textbox', { name: 'Outcome spec generation model' }) as HTMLInputElement;

    expect(screen.getByText('Generation models')).toBeDefined();
    expect(screen.getByText('Leave a field blank to inherit the global generation default (gpt-5.4).')).toBeDefined();
    expect(blueprint.value).toBe('');
    expect(blueprint.getAttribute('placeholder')).toBe('Inherit gpt-5.4');
    expect(workflow.value).toBe('claude-sonnet-4.6');
    expect(outcome.value).toBe('');
  });

  it('shows project members on the Access section', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Access/i }));

    expect(await screen.findByText('Platform access')).toBeDefined();
    expect(screen.getByText('Ada Lovelace')).toBeDefined();
  });

  it('links Entra access management when the access overview endpoint is unavailable', async () => {
    vi.mocked(apiClient.getProjectAccessOverview).mockRejectedValue(new ApiError(404, 'Not Found') as never);
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Access/i }));

    expect(await screen.findByText('Access management is handled in Microsoft Entra ID for this deployment.')).toBeDefined();
    expect((await screen.findByRole('link', { name: 'Manage in Microsoft Entra ID' })).getAttribute('href'))
      .toBe('https://entra.microsoft.com/tenant-1/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/AppRoles/appId/client-1/isMSAApp~/false');
  });

  it('uses the auth config mode as the authentication-mode fallback when access overview is unavailable', async () => {
    vi.mocked(apiClient.getProjectAccessOverview).mockRejectedValue(new ApiError(404, 'Not Found') as never);
    renderPage('proj-1');

    await screen.findByText('Rename project');

    expect(await screen.findByText('Entra ID')).toBeDefined();
    expect(screen.queryByText(/^GitHub$/)).toBeNull();
  });

  it('adds a project member through Tank role-assignment contract', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Access/i }));

    fireEvent.change(await screen.findByRole('textbox', { name: 'Add member' }), { target: { value: 'grace@contoso.com' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Display name (optional)' }), { target: { value: 'Grace Hopper' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Role' }), { target: { value: 'Contributor' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add member' }));

    await waitFor(() => expect(apiClient.createProjectRoleAssignment).toHaveBeenCalledWith('proj-1', {
      principal_id: 'grace@contoso.com',
      display_name: 'Grace Hopper',
      email: 'grace@contoso.com',
      role: 'Contributor',
    }));
  });

  it('saves generation model overrides using Tank backend payload shape', async () => {
    renderPage('proj-1');

    const blueprint = await screen.findByRole('textbox', { name: 'Blueprint generation model' });
    const outcome = screen.getByRole('textbox', { name: 'Outcome spec generation model' });

    fireEvent.change(blueprint, { target: { value: ' gpt-5.5 ' } });
    fireEvent.change(outcome, { target: { value: ' claude-opus-4.8 ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save generation models' }));

    await waitFor(() => expect(apiClient.updateProjectProviderSettings).toHaveBeenCalledWith('proj-1', {
      default_provider: 'github-copilot',
      default_model_github_copilot: 'gpt-4',
      default_model_microsoft_foundry: null,
      blueprint_generation_model: 'gpt-5.5',
      workflow_generation_model: 'claude-sonnet-4.6',
      outcome_spec_generation_model: 'claude-opus-4.8',
    }));
    expect(await screen.findByText('Generation model settings saved.')).toBeDefined();
  });

  it('resets generation models to inherit the global default without filling fields', async () => {
    vi.mocked(apiClient.getProject).mockResolvedValue({
      project_id: 'proj-1',
      name: 'Demo',
      origin: 'blank',
      source_repository: null,
      working_directory: 'C:/demo',
      default_branch: 'main',
      owner: 'sabbour',
      default_provider: 'github-copilot',
      default_model_github_copilot: 'gpt-4',
      default_model_microsoft_foundry: 'foundry-model',
      blueprint_generation_model: 'gpt-5.5',
      workflow_generation_model: 'claude-sonnet-4.6',
      outcome_spec_generation_model: 'claude-opus-4.8',
      available: true,
      state: 'active',
      created_at: '2026-07-07T00:00:00Z',
      updated_at: '2026-07-07T00:00:00Z',
    } as never);

    renderPage('proj-1');

    const blueprint = await screen.findByRole('textbox', { name: 'Blueprint generation model' }) as HTMLInputElement;
    const workflow = screen.getByRole('textbox', { name: 'Workflow generation model' }) as HTMLInputElement;
    const outcome = screen.getByRole('textbox', { name: 'Outcome spec generation model' }) as HTMLInputElement;
    expect(blueprint.value).toBe('gpt-5.5');

    fireEvent.click(screen.getByRole('button', { name: 'Reset to inherit defaults' }));

    await waitFor(() => expect(apiClient.updateProjectProviderSettings).toHaveBeenCalledWith('proj-1', {
      default_provider: 'github-copilot',
      default_model_github_copilot: 'gpt-4',
      default_model_microsoft_foundry: 'foundry-model',
      blueprint_generation_model: null,
      workflow_generation_model: null,
      outcome_spec_generation_model: null,
    }));
    expect(blueprint.value).toBe('');
    expect(workflow.value).toBe('');
    expect(outcome.value).toBe('');
  });

  it('shows an inverted "Sandbox enabled" toggle and gates the network switch on it', async () => {
    vi.mocked(apiClient.getSandboxPolicy).mockResolvedValue({
      repository_path: 'C:/demo',
      shell_enabled: true,
      direct: true, // sandbox OFF
      network_enabled: false,
      allowed_repository_roots: ['C:/demo'],
      destructive_command_patterns: ['rm -rf'],
    } as never);

    renderPage('proj-1');
    await waitFor(() => expect(screen.getByText('Project settings')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    // Inverted label present; the old "Direct execution" label is gone.
    await waitFor(() => expect(screen.getByText('Sandbox enabled')).toBeDefined());
    expect(screen.queryByText(/Direct execution/i)).toBeNull();

    // Switch order: Shell execution, Sandbox enabled, Outbound network.
    let switches = screen.getAllByRole('switch') as HTMLInputElement[];
    expect(switches).toHaveLength(3);
    // direct=true => "Sandbox enabled" is unchecked (inverted).
    expect(switches[1].checked).toBe(false);
    // Network toggle is disabled while the sandbox is off, with a hint.
    expect(switches[2].disabled).toBe(true);
    expect(screen.getByText('Only applies when the sandbox is enabled.')).toBeDefined();

    // Turning the sandbox ON sends direct=false and re-enables the network toggle.
    fireEvent.click(switches[1]);
    switches = screen.getAllByRole('switch') as HTMLInputElement[];
    expect(switches[1].checked).toBe(true);
    expect(switches[2].disabled).toBe(false);
  });

  it('saves the project-scoped preview approval timeout', async () => {
    renderPage('proj-1');
    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    const input = await screen.findByRole('spinbutton', {
      name: 'Preview approval timeout in minutes',
    });
    expect((input as HTMLInputElement).value).toBe('30');
    fireEvent.change(input, { target: { value: '45' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save preview approval' }));

    await waitFor(() => expect(apiClient.updateProjectPreviewSettings).toHaveBeenCalledWith('proj-1', {
      approval_timeout_minutes: 45,
    }));
    expect(await screen.findByText('Preview approval timeout saved.')).toBeDefined();
  });

  it('validates the preview approval timeout before calling the API', async () => {
    renderPage('proj-1');
    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    const input = await screen.findByRole('spinbutton', {
      name: 'Preview approval timeout in minutes',
    });
    fireEvent.change(input, { target: { value: '0' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save preview approval' }));

    expect(await screen.findByText(
      'Approval timeout must be a whole number between 1 and 1440 minutes.',
    )).toBeDefined();
    expect(apiClient.updateProjectPreviewSettings).not.toHaveBeenCalled();
  });
});
