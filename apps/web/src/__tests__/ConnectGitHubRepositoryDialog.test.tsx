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
  vi.mocked(apiClient.listProjectRepositoryOwners).mockResolvedValue([
    { login: 'octo', type: 'user' },
  ] as never);
  vi.mocked(apiClient.listGitHubRepositorySelections).mockResolvedValue({
    repositories: [
      { full_name: 'octo/existing-repo', owner_login: 'octo', private: true, default_branch: 'main', pushed_at: null },
      { full_name: 'octo/other-repo', owner_login: 'octo', private: false, default_branch: 'develop', pushed_at: null },
    ],
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
  it('offers a Connect GitHub action when the Repo App is not yet connected', async () => {
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

    await screen.findByRole('button', { name: 'Connect GitHub' });
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
    expect(screen.queryByRole('button', { name: 'Connect GitHub' })).toBeNull();
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
    fireEvent.change(await screen.findByRole('textbox', { name: 'Find repository' }), { target: { value: 'other' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Repository' }), { target: { value: 'octo/other-repo' } });
    fireEvent.click(screen.getByRole('button', { name: 'Connect repository' }));

    await waitFor(() => expect(apiClient.issueGitHubRepositorySelection).toHaveBeenCalledWith('octo/other-repo'));
    expect(apiClient.connectProjectRepository).toHaveBeenCalledWith('proj-1', {
      repository_selection_code: 'opaque-selection-code',
    });
    expect(onConnected).toHaveBeenCalledWith('octo/other-repo', 'https://github.com/octo/other-repo');
    expect(await screen.findByRole('link', { name: 'octo/other-repo' })).toBeDefined();
  });

  it('sends the current page back through the Repo App authorization start', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'github_binding_unavailable' })),
    );
    vi.mocked(apiClient.beginRepoAppAuthorization).mockResolvedValue({
      authorization_url: 'https://github.com/login/oauth/authorize?client_id=repo-app',
      transaction_id: 'txn-1',
      expires_at: '2026-08-28T00:05:00+00:00',
    } as never);
    const assignSpy = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign: assignSpy });

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

    fireEvent.click(await screen.findByRole('button', { name: 'Connect GitHub' }));

    await waitFor(() => expect(apiClient.beginRepoAppAuthorization)
      .toHaveBeenCalledWith('/projects/proj-1/settings?section=repository'));
    await waitFor(() => expect(assignSpy).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?client_id=repo-app'));
    vi.unstubAllGlobals();
  });
});
