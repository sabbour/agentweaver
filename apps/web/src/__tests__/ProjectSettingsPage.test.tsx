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
    beginProjectCopilotAuthorization: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
    getPlatformDefaultCopilotConnection: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

function renderPage(projectId: string) {
  return render(
    <Wrapper>
      <MemoryRouter initialEntries={[`/projects/${projectId}/settings`]}>
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
  } as never);
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
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: 'C:/data' } as never);
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
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'not_connected',
    github_login: null,
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

  it('shows read-only background readiness without an activation control', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Background/i }));

    expect(await screen.findByText('Background automation readiness')).toBeDefined();
    expect(screen.getByText(
      'This controls the GitHub Copilot account used for this project’s background AI and other Copilot-powered generation. It does not control repository access.',
    )).toBeDefined();
    expect(screen.getByText(
      'These server-verified prerequisites apply after you connect a GitHub repository. They cover repository access for background branch, push, and pull-request work and are separate from the GitHub Copilot AI access shown above.',
    )).toBeDefined();
    expect(screen.getByText('copilot_binding_required')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Manage GitHub Copilot' })).toBeDefined();
    expect(screen.queryByRole('button', { name: /activate|enable|start automation/i })).toBeNull();
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
      'These server-verified prerequisites cover repository access for background branch, push, and pull-request work on this project’s connected GitHub repository. They are separate from the GitHub Copilot AI access shown above.',
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
