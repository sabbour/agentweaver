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
    listGitHubAccounts: vi.fn(),
    listGitHubRepos: vi.fn(),
  },
}));

import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import { ProjectListProvider } from '../hooks/useProjectList';
import type { GitHubAccount, GitHubRepo, Project } from '../api/types';

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

const USER_ACCOUNT: GitHubAccount = { login: 'octocat', name: 'Octocat', avatar_url: 'https://example.com/avatar.png', type: 'user' };
const REPO: GitHubRepo = { fullName: 'octocat/hello-world', defaultBranch: 'main', private: false, description: 'A sample repo', htmlUrl: 'https://github.com/octocat/hello-world' };
const REPO_B: GitHubRepo = { fullName: 'octocat/aardvark', defaultBranch: 'main', private: false, description: null, htmlUrl: 'https://github.com/octocat/aardvark' };
const REPO_C: GitHubRepo = { fullName: 'octocat/zebra', defaultBranch: 'main', private: false, description: 'Last alphabetically', htmlUrl: 'https://github.com/octocat/zebra' };

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter>
        <ProjectListProvider>
          {children}
        </ProjectListProvider>
      </MemoryRouter>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data', workspace_auto_assigned: false } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue([]);
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([]);
  vi.mocked(apiClient.createProject).mockImplementation(async () => makeProject('new', 'New'));
  // Default: accounts succeed (auto-selects first), repos start empty.
  vi.mocked(apiClient.listGitHubAccounts).mockResolvedValue([USER_ACCOUNT] as never);
  vi.mocked(apiClient.listGitHubRepos).mockResolvedValue([]);
});

afterEach(() => cleanup());

async function openGitHubDialog() {
  render(<Wrapper><ProjectGalleryPage /></Wrapper>);
  const trigger = await screen.findByRole('button', { name: 'Create from GitHub' });
  fireEvent.click(trigger);
}

describe('ProjectGalleryPage — GitHub repo listing auth', () => {
  it('shows a connect affordance (not a silent empty list) when accounts return 401', async () => {
    vi.mocked(apiClient.listGitHubAccounts).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();

    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );
    expect(screen.getByRole('button', { name: 'Connect GitHub' })).toBeDefined();
  });

  it('lists repositories when the fetch succeeds', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockResolvedValue([REPO]);

    await openGitHubDialog();

    // Accounts loaded → repos loaded for first account.
    await waitFor(() => expect(apiClient.listGitHubRepos).toHaveBeenCalledWith('octocat'));
    expect(screen.queryByText(/Connect your GitHub account/)).toBeNull();

    // Opening the repo combobox surfaces the fetched repo — shows name only (no owner prefix).
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));
    await waitFor(() => expect(screen.getByText('hello-world')).toBeDefined());
    // Owner prefix must NOT appear as a standalone label.
    expect(screen.queryByText('octocat/hello-world')).toBeNull();
  });

  it('does not render the repo description in the dropdown', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockResolvedValue([REPO]);

    await openGitHubDialog();

    await waitFor(() => expect(apiClient.listGitHubRepos).toHaveBeenCalledWith('octocat'));
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));
    await waitFor(() => expect(screen.getByText('hello-world')).toBeDefined());
    expect(screen.queryByText('A sample repo')).toBeNull();
  });

  it('sorts repos alphabetically by name (case-insensitive)', async () => {
    vi.mocked(apiClient.listGitHubRepos).mockResolvedValue([REPO_C, REPO, REPO_B]);

    await openGitHubDialog();

    await waitFor(() => expect(apiClient.listGitHubRepos).toHaveBeenCalledWith('octocat'));
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));

    await waitFor(() => expect(screen.getByText('aardvark')).toBeDefined());

    const options = screen.getAllByRole('option');
    const labels = options.map(o => o.textContent);
    expect(labels).toEqual(['aardvark', 'hello-world', 'zebra']);
  });

  it('still submits a manually typed owner/repo even when repos failed to load', async () => {
    vi.mocked(apiClient.listGitHubAccounts).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();
    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );

    // When auth is required the Organization picker is hidden; only the repo combobox remains.
    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
    const repoCombobox = screen.getByRole('combobox', { name: 'Repository' });
    fireEvent.input(repoCombobox, { target: { value: 'me/manual-repo' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });

    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.origin).toBe('github');
    expect(req.source_repository).toBe('https://github.com/me/manual-repo');
  });

  it('normalizes a manually typed owner/repo to an HTTPS URL on submit', async () => {
    vi.mocked(apiClient.listGitHubAccounts).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();
    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );

    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Repo' } });
    const repoCombobox = screen.getByRole('combobox', { name: 'Repository' });
    // Already-full URL must pass through unchanged
    fireEvent.input(repoCombobox, { target: { value: 'https://github.com/me/my-repo' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });

    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.source_repository).toBe('https://github.com/me/my-repo');
  });
});

describe('ProjectGalleryPage — listProjects 401', () => {
  it('shows sign-in affordance (not "No projects yet") when listProjects returns 401', async () => {
    vi.mocked(apiClient.listProjects).mockRejectedValue(new ApiError(401, 'Unauthorized'));

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    // Should show sign-in affordance.
    await waitFor(() =>
      expect(screen.getByText(/Sign in with GitHub to see your projects/)).toBeDefined(),
    );
    expect(screen.getByRole('button', { name: 'Sign in with GitHub' })).toBeDefined();

    // Must NOT show "No projects yet" — that's for a genuinely empty account.
    expect(screen.queryByText(/No projects yet/)).toBeNull();
    // Must NOT show a raw API error message.
    expect(screen.queryByText(/API error 401/)).toBeNull();
  });

  it('still shows "No projects yet" when listProjects succeeds with an empty list', async () => {
    vi.mocked(apiClient.listProjects).mockResolvedValue([]);

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    await waitFor(() =>
      expect(screen.getByText('No projects yet. Create one to get started.')).toBeDefined(),
    );
    expect(screen.queryByText(/Sign in with GitHub to see your projects/)).toBeNull();
  });
});

describe('ProjectGalleryPage — GitHub dialog, workspace_auto_assigned', () => {
  it('hides the Repository folder field in the GitHub dialog when workspace_auto_assigned is true', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      workspace_auto_assigned: true,
    } as never);

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create from GitHub' });
    fireEvent.click(trigger);

    // Folder field must not be present.
    await waitFor(() => expect(apiClient.listGitHubAccounts).toHaveBeenCalled());
    expect(screen.queryByPlaceholderText('my-repo')).toBeNull();
  });

  it('submits working_directory derived from the repo slug when workspace_auto_assigned is true', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      workspace_auto_assigned: true,
    } as never);
    vi.mocked(apiClient.listGitHubAccounts).mockRejectedValue(new ApiError(401, 'unauthorized'));

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create from GitHub' });
    fireEvent.click(trigger);

    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );

    // Fill name and repo manually (auth required path, no folder field).
    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'Hello World' } });
    const repoCombobox = screen.getByRole('combobox', { name: 'Repository' });
    fireEvent.input(repoCombobox, { target: { value: 'octocat/hello-world' } });

    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.working_directory).toBe('hello-world');
    expect(req.source_repository).toBe('https://github.com/octocat/hello-world');
  });
});
