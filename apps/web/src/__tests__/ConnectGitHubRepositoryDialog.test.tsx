import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ConnectGitHubRepositoryDialog } from '../components/ConnectGitHubRepositoryDialog';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const originalConsoleError = console.error;
const originalConsoleWarn = console.warn;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjectRepositoryOwners: vi.fn(),
    listGitHubRepositorySelections: vi.fn(),
    issueGitHubRepositorySelection: vi.fn(),
    connectProjectRepository: vi.fn(),
    createProjectRepository: vi.fn(),
    beginRepoAppAuthorization: vi.fn(),
  },
}));

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({
    data_directory: '/data',
    repo_app_install_url: 'https://github.com/apps/agentweaver/installations/new',
  } as never);
  vi.mocked(apiClient.listProjectRepositoryOwners).mockResolvedValue([
    { login: 'octo', type: 'user' },
  ] as never);
  vi.mocked(apiClient.listGitHubRepositorySelections).mockResolvedValue({
    repositories: [
      { full_name: 'octo/existing-repo', owner_login: 'octo', private: true, default_branch: 'main', pushed_at: null },
      { full_name: 'octo/other-repo', owner_login: 'octo', private: false, default_branch: 'develop', pushed_at: null },
    ],
    installations: [{
      account_login: 'octo',
      account_type: 'user',
      repository_selection: 'selected',
      management_url: 'https://github.com/settings/installations/123',
    }],
  } as never);
  vi.spyOn(console, 'error').mockImplementation((...args) => {
    if (typeof args[0] === 'string' && args[0].includes('Keyborg instance')) return;
    originalConsoleError(...args);
  });
  vi.spyOn(console, 'warn').mockImplementation((...args) => {
    if (typeof args[0] === 'string' && args[0].includes('Keyborg instance')) return;
    originalConsoleWarn(...args);
  });
});

describe('ConnectGitHubRepositoryDialog', () => {
  it('offers repository authorization when the Repo App is not yet connected', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'github_binding_unavailable' })),
    );

    render(
      <MemoryRouter initialEntries={['/projects/proj-1/settings?section=repository']}>
        <AzureFluentProvider density="compact">
          <ConnectGitHubRepositoryDialog
            projectId="proj-1"
            projectName="Demo Project"
            open
            onOpenChange={() => {}}
            onConnected={() => {}}
          />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByRole('heading', { name: 'Set up repository access' })).toBeDefined();
    expect(screen.getByText(/Local agent work can continue without a repository/)).toBeDefined();
    await screen.findByRole('button', { name: 'Authorize repository access' });
    expect(screen.getByTestId('connect-github-repository-owners-error').getAttribute('data-intent')).toBe('warning');
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('shows a Retry action for other owner-loading failures', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockRejectedValue(
      new ApiError(500, JSON.stringify({ error: 'internal_error' })),
    );

    render(
      <MemoryRouter initialEntries={['/projects/proj-1/settings?section=repository']}>
        <AzureFluentProvider density="compact">
          <ConnectGitHubRepositoryDialog
            projectId="proj-1"
            projectName="Demo Project"
            open
            onOpenChange={() => {}}
            onConnected={() => {}}
          />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    await screen.findByRole('button', { name: 'Retry' });
    expect(screen.getByTestId('connect-github-repository-owners-error').getAttribute('data-intent')).toBe('error');
    expect(screen.queryByRole('button', { name: 'Authorize repository access' })).toBeNull();
  });

  it('offers GitHub App installation from the default create tab when no installations exist', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockResolvedValue([]);
    vi.mocked(apiClient.listGitHubRepositorySelections).mockResolvedValue({
      repositories: [],
      installations: [],
    } as never);

    render(
      <MemoryRouter initialEntries={['/projects/proj-1/settings?section=repository']}>
        <AzureFluentProvider density="compact">
          <ConnectGitHubRepositoryDialog
            projectId="proj-1"
            projectName="Demo Project"
            open
            onOpenChange={() => {}}
            onConnected={() => {}}
          />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText(
      'The Agentweaver GitHub App is not installed for an account you can access.',
    )).toBeDefined();
    const installLinks = screen.getAllByRole('link', { name: 'Install Agentweaver GitHub App' });
    expect(installLinks).toHaveLength(1);
    expect(installLinks[0].getAttribute('href')).toBe('https://github.com/apps/agentweaver/installations/new');
    expect(installLinks[0].getAttribute('target')).toBe('_blank');
    expect(installLinks[0].getAttribute('rel')).toBe('noopener noreferrer');
    expect(screen.getByRole('button', { name: 'Create repository' }).hasAttribute('disabled')).toBe(true);
  });

  it('connects an existing repository from the new tab', async () => {
    vi.mocked(apiClient.issueGitHubRepositorySelection).mockResolvedValue({
      selection_code: 'opaque-selection-code',
      expires_at: '2026-08-31T00:00:00Z',
    } as never);
    vi.mocked(apiClient.connectProjectRepository).mockResolvedValue({
      source_repository: 'octo/other-repo',
      html_url: 'https://github.com/octo/other-repo',
    } as never);
    const onConnected = vi.fn();

    render(
      <MemoryRouter initialEntries={['/projects/proj-1/settings?section=repository']}>
        <AzureFluentProvider density="compact">
          <ConnectGitHubRepositoryDialog
            projectId="proj-1"
            projectName="Demo Project"
            open
            onOpenChange={() => {}}
            onConnected={onConnected}
          />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Connect existing repository' }));
    expect(screen.getByText('Agentweaver has access only to selected repositories for octo.')).toBeDefined();
    const managementLink = screen.getByRole('link', { name: 'Open GitHub installation settings for octo' });
    expect(managementLink.getAttribute('href')).toBe('https://github.com/settings/installations/123');
    expect(managementLink.getAttribute('target')).toBe('_blank');
    expect(managementLink.getAttribute('rel')).toBe('noopener noreferrer');
    fireEvent.change(await screen.findByRole('textbox', { name: 'Find repository' }), { target: { value: 'other' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Repository' }), { target: { value: 'octo/other-repo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Connect repository' }));

    await waitFor(() => expect(apiClient.issueGitHubRepositorySelection).toHaveBeenCalledWith('octo/other-repo'));
    expect(apiClient.connectProjectRepository).toHaveBeenCalledWith('proj-1', {
      repository_selection_code: 'opaque-selection-code',
    });
    expect(onConnected).toHaveBeenCalledWith('octo/other-repo', 'https://github.com/octo/other-repo');
    expect(await screen.findByRole('link', { name: 'octo/other-repo' })).toBeDefined();
    expect(screen.getByText(/Agentweaver pushed the existing project history/)).toBeDefined();
  });

  it('removes a failed callback result before retrying repository authorization', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'github_binding_unavailable' })),
    );
    vi.mocked(apiClient.beginRepoAppAuthorization).mockResolvedValue({
      authorization_url: 'https://github.com/login/oauth/authorize?client_id=repo-app',
      transaction_id: 'txn-1',
      expires_at: '2026-08-28T00:05:00+00:00',
    } as never);
    const assignSpy = vi.spyOn(window.location, 'assign').mockImplementation(() => {});

    render(
      <MemoryRouter initialEntries={['/projects/proj-1/settings?section=repository&repo_app_auth=rate_limited']}>
        <AzureFluentProvider density="compact">
          <ConnectGitHubRepositoryDialog
            projectId="proj-1"
            projectName="Demo Project"
            open
            onOpenChange={() => {}}
            onConnected={() => {}}
            authorizationResult="rate_limited"
          />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Authorize repository access' }));

    await waitFor(() => expect(apiClient.beginRepoAppAuthorization)
      .toHaveBeenCalledWith('/projects/proj-1/settings?section=repository'));
    expect(assignSpy).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?client_id=repo-app');
  });
});
