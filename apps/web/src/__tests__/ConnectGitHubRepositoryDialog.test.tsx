import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ConnectGitHubRepositoryDialog } from '../components/ConnectGitHubRepositoryDialog';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const originalConsoleError = console.error;
const originalConsoleWarn = console.warn;

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listProjectRepositoryOwners: vi.fn(),
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
      <AzureFluentProvider density="compact">
        <ConnectGitHubRepositoryDialog
          projectId="proj-1"
          projectName="Demo Project"
          open
          onOpenChange={() => {}}
          onConnected={() => {}}
        />
      </AzureFluentProvider>,
    );

    await screen.findByRole('button', { name: 'Connect GitHub' });
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('shows a Retry action for other owner-loading failures', async () => {
    vi.mocked(apiClient.listProjectRepositoryOwners).mockRejectedValue(
      new ApiError(500, JSON.stringify({ error: 'internal_error' })),
    );

    render(
      <AzureFluentProvider density="compact">
        <ConnectGitHubRepositoryDialog
          projectId="proj-1"
          projectName="Demo Project"
          open
          onOpenChange={() => {}}
          onConnected={() => {}}
        />
      </AzureFluentProvider>,
    );

    await screen.findByRole('button', { name: 'Retry' });
    expect(screen.queryByRole('button', { name: 'Connect GitHub' })).toBeNull();
  });
});
