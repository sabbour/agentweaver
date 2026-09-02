import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { ProjectModelProviderSettings } from '../components/ProjectModelProviderSettings';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    beginProjectCopilotAuthorization: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
    getPlatformDefaultCopilotConnection: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <MemoryRouter initialEntries={['/projects/project-1/team']}>
      <AzureFluentProvider density="compact">{children}</AzureFluentProvider>
    </MemoryRouter>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
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

afterEach(cleanup);

describe('ProjectModelProviderSettings', () => {
  it('keeps the new project connection when route navigation finishes an older request late', async () => {
    let resolveOldConnection: (connection: { status: 'connected'; github_login: string }) => void;
    const oldConnection = new Promise<{ status: 'connected'; github_login: string }>((resolve) => {
      resolveOldConnection = resolve;
    });
    vi.mocked(apiClient.getProjectCopilotConnection)
      .mockImplementationOnce(() => oldConnection)
      .mockResolvedValueOnce({ status: 'connected', github_login: 'project-b' });

    const { rerender } = render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-a" showConnectionStatus />
      </Wrapper>,
    );
    await waitFor(() => expect(apiClient.getProjectCopilotConnection).toHaveBeenCalledWith('project-a'));

    rerender(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-b" showConnectionStatus />
      </Wrapper>,
    );
    expect(await screen.findByText('GitHub Copilot (@project-b) supplies AI access. Scope: Project.')).toBeDefined();

    await act(async () => {
      resolveOldConnection!({ status: 'connected', github_login: 'project-a' });
      await oldConnection;
    });

    expect(screen.getByText('GitHub Copilot (@project-b) supplies AI access. Scope: Project.')).toBeDefined();
    expect(screen.queryByText('GitHub Copilot (@project-a) supplies AI access. Scope: Project.')).toBeNull();
  });

  it('shows an explicit, accessible connection picker when no account is connected', async () => {
    render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-1" showConnectionStatus />
      </Wrapper>,
    );

    expect(await screen.findByText('Choose a model provider before this project starts AI work.')).toBeDefined();
    expect(screen.getByText('Action required')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));

    expect(await screen.findByRole('dialog')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Set up the project model provider' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Authorize GitHub Copilot' })).toBeDefined();
  });

  it('refreshes the selected account after connection and starts the same retryable browser handoff', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://api.example.test/auth/github/copilot-app/authorize',
      transaction_id: 'opaque-transaction',
      expires_at: '2026-08-29T00:00:00Z',
    });
    render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-1" showConnectionStatus />
      </Wrapper>,
    );

    await screen.findByText('Choose a model provider before this project starts AI work.');
    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'connected',
      github_login: 'octocat',
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Reload status' }));
    expect(await screen.findByText('GitHub Copilot (@octocat) is ready. Scope: Project.')).toBeDefined();
    fireEvent.click(await screen.findByRole('button', { name: 'Switch GitHub Copilot account' }));
    await waitFor(() => expect(apiClient.beginProjectCopilotAuthorization)
      .toHaveBeenCalledWith('project-1', '/projects/project-1/team'));
    expect(assign).toHaveBeenCalledWith('https://api.example.test/auth/github/copilot-app/authorize');
    assign.mockRestore();
  });

  it('shows the platform-configured account and hides project override controls when requested', async () => {
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'not_connected',
      github_login: 'platform-bot',
      effective_source: 'platform_default',
    });
    render(
      <Wrapper>
        <ProjectModelProviderSettings
          projectId="project-1"
          showConnectionStatus
          suppressProjectOverrideWhenPlatformDefault
        />
      </Wrapper>,
    );

    expect(await screen.findByText(
      'GitHub Copilot (@platform-bot) supplies AI access. Scope: Platform.',
    )).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Manage GitHub Copilot' })).toBeNull();
  });

  it('shows that background AI uses the deployment custom key when BYOK is active', async () => {
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'not_connected',
      github_login: null,
      effective_source: 'byok',
    });
    render(
      <Wrapper>
        <ProjectModelProviderSettings
          projectId="project-1"
          showConnectionStatus
          suppressProjectOverrideWhenPlatformDefault
        />
      </Wrapper>,
    );

    expect(await screen.findByText(
      'A custom-key model provider supplies AI access. Scope: Platform.',
    )).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Manage GitHub Copilot' })).toBeNull();
  });

  it('keeps the picker usable and shows a clear error when the handoff cannot start', async () => {
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockRejectedValue(new Error('network'));
    render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Authorize GitHub Copilot' }));

    expect(await screen.findByText('The GitHub Copilot authorization did not start. Try again.')).toBeDefined();
    expect((screen.getByRole('button', { name: 'Authorize GitHub Copilot' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('surfaces actionable API errors while loading the current connection', async () => {
    vi.mocked(apiClient.getProjectCopilotConnection).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'github_binding_unavailable' })),
    );
    render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-1" showConnectionStatus />
      </Wrapper>,
    );

    expect(await screen.findByText('The GitHub authorization status is unavailable. Try again later.')).toBeDefined();
    expect(screen.queryByText(/repository access/i)).toBeNull();
  });

  it('explains that Copilot authorization creates a durable user OAuth binding without installing an app', async () => {
    render(
      <Wrapper>
        <ProjectModelProviderSettings projectId="project-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));
    expect(await screen.findByText(/creates a durable project binding for unattended AI work/i)).toBeDefined();
    expect(screen.getByText(/does not install a GitHub App or grant repository access/i)).toBeDefined();
  });

  it('offers project binding repair even when platform BYOK is active', async () => {
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'not_connected',
      github_login: null,
      effective_source: 'byok',
    });
    render(
      <Wrapper>
        <ProjectModelProviderSettings
          projectId="project-1"
          showConnectionStatus
          suppressProjectOverrideWhenPlatformDefault
          repairRequired
        />
      </Wrapper>,
    );

    expect(await screen.findByText(
      'Reconnect the project GitHub Copilot authorization used for unattended AI work.',
    )).toBeDefined();
    expect(screen.getByRole('button', { name: 'Manage GitHub Copilot' })).toBeDefined();
  });
});
