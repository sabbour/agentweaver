import { apiClient } from '../api/apiClient';
import { API_URL, resolvePublicApiOrigin } from '../config';
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
    getProject: vi.fn(),
    getServerInfo: vi.fn(),
    getSandboxPolicy: vi.fn(),
    autoCreateProjectWebhook: vi.fn(),
    rotateProjectWebhookSecret: vi.fn(),
    updateProjectProviderSettings: vi.fn(),
    updateSandboxPolicy: vi.fn(),
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
    available: true,
    state: 'active',
    created_at: '2026-07-07T00:00:00Z',
    updated_at: '2026-07-07T00:00:00Z',
  } as never);
  vi.mocked(apiClient.updateProjectProviderSettings).mockResolvedValue(undefined as never);
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: 'C:/data' } as never);
  vi.mocked(apiClient.getSandboxPolicy).mockResolvedValue({
    repository_path: 'C:/demo',
    shell_enabled: true,
    direct: false,
    network_enabled: true,
    allowed_repository_roots: ['C:/demo'],
    destructive_command_patterns: [],
  } as never);
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
    expect(screen.getByRole('button', { name: /Sandbox policy/i })).toBeDefined();
    expect(screen.getByRole('button', { name: /Danger Zone/i })).toBeDefined();
  });

  it('switches the pane when a rail item is clicked', async () => {
    renderPage('proj-1');

    await waitFor(() => expect(screen.getByText('Rename project')).toBeDefined());

    fireEvent.click(screen.getByRole('button', { name: /Sandbox policy/i }));

    await waitFor(() => expect(screen.getByText('Sandbox enabled')).toBeDefined());
  });

  it('reveals a rotated GitHub webhook secret once', async () => {
    vi.mocked(apiClient.rotateProjectWebhookSecret).mockResolvedValue({ secret: 'new-secret' } as never);
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Webhooks/i }));
    fireEvent.click(await screen.findByRole('button', { name: 'Generate secret' }));

    await waitFor(() => expect(apiClient.rotateProjectWebhookSecret).toHaveBeenCalledWith('proj-1'));
    expect(await screen.findByText('Copy this secret now. You won’t be able to see it again.')).toBeDefined();
    expect(screen.getByDisplayValue('new-secret')).toBeDefined();
  });

  it('uses the configured API origin for the GitHub webhook URL', async () => {
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Webhooks/i }));

    expect(screen.getByDisplayValue(
      `${resolvePublicApiOrigin(API_URL)}/api/projects/proj-1/webhooks/github`,
    )).toBeDefined();
  });

  it('shows a coming-soon message for automatic webhook creation', async () => {
    const { ApiError } = await import('../api/client');
    vi.mocked(apiClient.autoCreateProjectWebhook).mockRejectedValue(new ApiError(501, 'Automatic GitHub webhook creation is not implemented yet.'));
    renderPage('proj-1');

    await screen.findByText('Rename project');
    fireEvent.click(screen.getByRole('button', { name: /Webhooks/i }));
    fireEvent.click(await screen.findByRole('button', { name: 'Create webhook automatically' }));

    await waitFor(() => expect(apiClient.autoCreateProjectWebhook).toHaveBeenCalledWith('proj-1'));
    expect(await screen.findByText('Automatic webhook creation is coming soon. Use the manual setup steps below for now.')).toBeDefined();
  });

  it('uses the browser origin for public URLs when API_URL is the same-origin sentinel', () => {
    expect(resolvePublicApiOrigin('')).toBe(window.location.origin);
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
});
