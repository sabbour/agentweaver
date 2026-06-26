import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, waitFor, fireEvent } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { MemoryRouter } from 'react-router-dom';
import { type ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listBlueprints: vi.fn(),
    generateBlueprint: vi.fn(),
    listGitHubRepos: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import type { GitHubRepo, Project } from '../api/types';

function makeProject(id: string, name: string): Project {
  return {
    project_id: id,
    name,
    origin: 'github',
    source_repository: 'owner/repo',
    working_directory: '/data/x',
    default_branch: 'main',
    owner: 'me',
    default_provider: 'github-copilot',
    default_model_github_copilot: null,
    default_model_microsoft_foundry: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

const REPO: GitHubRepo = { fullName: 'octocat/hello-world', defaultBranch: 'main', private: false, description: 'A sample repo' };

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>{children}</MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data' } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue([]);
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([]);
  vi.mocked(apiClient.createProject).mockImplementation(async () => makeProject('new', 'New'));
});

afterEach(() => cleanup());

async function openGitHubDialog() {
  render(<Wrapper><ProjectGalleryPage /></Wrapper>);
  const trigger = await screen.findByRole('button', { name: 'Create from GitHub' });
  fireEvent.click(trigger);
}

describe('ProjectGalleryPage — GitHub repo listing auth', () => {
  it('shows a connect affordance (not a silent empty list) when repos return 401', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();

    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );
    expect(screen.getByRole('button', { name: 'Connect GitHub' })).toBeDefined();
  });

  it('lists repositories when the fetch succeeds', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockResolvedValue([REPO]);

    await openGitHubDialog();

    // No auth/connect message when authenticated.
    await waitFor(() => expect(apiClient.listGitHubRepos).toHaveBeenCalled());
    expect(screen.queryByText(/Connect your GitHub account/)).toBeNull();

    // Opening the combobox surfaces the fetched repo.
    fireEvent.click(screen.getByRole('combobox'));
    await waitFor(() => expect(screen.getByText('octocat/hello-world')).toBeDefined());
  });

  it('still submits a manually typed owner/repo even when repos failed to load', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();
    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );

    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
    const combobox = screen.getByRole('combobox');
    fireEvent.input(combobox, { target: { value: 'me/manual-repo' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });

    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.origin).toBe('github');
    expect(req.source_repository).toBe('me/manual-repo');
  });
});
