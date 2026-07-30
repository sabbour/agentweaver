import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ProjectListProvider } from '../hooks/useProjectList';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest';
import type { GitHubAccount, GitHubRepo, Project } from '../api/types';
import type { ReactNode } from 'react';
vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listBlueprints: vi.fn(),
    generateBlueprint: vi.fn(),
    suggestBlueprint: vi.fn(),
    listLinkedGitHubAccounts: vi.fn(),
    listAccessibleGitHubRepos: vi.fn(),
    listGitHubAccounts: vi.fn(),
    listGitHubRepos: vi.fn(),
  },
}));

// Pagination contract (`.squad/decisions/inbox/niobe-pagination-contract.md`): `listProjects`
// now resolves a `{ items, page, page_size, total_count, total_pages }` envelope.
function projectsPage(items: Project[]) {
  return { items, page: 1, page_size: 100, total_count: items.length, total_pages: 1 } as never;
}

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
    blueprint_generation_model: null,
    workflow_generation_model: null,
    outcome_spec_generation_model: null,
    available: true,
    state: 'active',
    created_at: '',
    updated_at: '',
  };
}

const USER_ACCOUNT: GitHubAccount = { login: 'octocat', name: 'Octocat', avatar_url: 'https://example.com/avatar.png', type: 'user' };
const LINKED_ACCOUNT = {
  login: 'octocat',
  name: 'Octocat',
  avatar_url: 'https://example.com/avatar.png',
  type: 'user',
  is_default: true,
  copilot_entitled: true,
};
const ALT_LINKED_ACCOUNT = {
  login: 'altcat',
  name: 'Alt Cat',
  avatar_url: 'https://example.com/altcat.png',
  type: 'user',
  is_default: false,
  copilot_entitled: false,
};
const REPO: GitHubRepo = { fullName: 'octocat/hello-world', defaultBranch: 'main', private: false, description: 'A sample repo', htmlUrl: 'https://github.com/octocat/hello-world' };
const REPO_B: GitHubRepo = { fullName: 'octocat/aardvark', defaultBranch: 'main', private: false, description: null, htmlUrl: 'https://github.com/octocat/aardvark' };
const REPO_C: GitHubRepo = { fullName: 'octocat/zebra', defaultBranch: 'main', private: false, description: 'Last alphabetically', htmlUrl: 'https://github.com/octocat/zebra' };

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter>
        <ProjectListProvider>
          {children}
        </ProjectListProvider>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data', workspace_auto_assigned: false } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([]);
  vi.mocked(apiClient.suggestBlueprint).mockResolvedValue({
    recommended_blueprint: null,
    rationale: 'No suggestion in test.',
    confidence: 0,
    signals: [],
    fallback: true,
  });
  vi.mocked(apiClient.createProject).mockImplementation(async () => makeProject('new', 'New'));
  vi.mocked(apiClient.listLinkedGitHubAccounts).mockResolvedValue([LINKED_ACCOUNT, ALT_LINKED_ACCOUNT] as never);
  vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([]);
  // Default: legacy fallback endpoints also succeed if cross-account APIs are absent.
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
    vi.mocked(apiClient.listLinkedGitHubAccounts).mockRejectedValue(new ApiError(401, 'unauthorized'));

    await openGitHubDialog();

    await waitFor(() =>
      expect(screen.getByText(/Connect your GitHub account to list repositories/)).toBeDefined(),
    );
    expect(screen.getByRole('button', { name: 'Connect GitHub' })).toBeDefined();
  });

  it('lists repositories across linked GitHub accounts when the cross-account fetch succeeds', async () => {
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([
      { ...REPO, source_login: 'octocat', source_is_default: true },
      { ...REPO_B, source_login: 'altcat', source_is_default: false },
    ] as never);

    await openGitHubDialog();

    await waitFor(() => expect(apiClient.listAccessibleGitHubRepos).toHaveBeenCalled());
    expect(screen.queryByText(/Connect your GitHub account/)).toBeNull();
    await waitFor(() =>
      expect(screen.getByText((content) => content.includes('Showing repositories reachable across all linked GitHub accounts.'))).toBeDefined(),
    );

    // Opening the repo combobox surfaces the fetched repo with an owner/repo label plus source account.
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));
    await waitFor(() => expect(screen.getAllByText('octocat / hello-world').length).toBeGreaterThan(0));
    expect(screen.getAllByText(/via @octocat/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/via @altcat/i).length).toBeGreaterThan(0);
  });

  it('does not render the repo description in the dropdown', async () => {
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([
      { ...REPO, source_login: 'octocat', source_is_default: true },
    ] as never);

    await openGitHubDialog();

    await waitFor(() => expect(apiClient.listAccessibleGitHubRepos).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));
    await waitFor(() => expect(screen.getAllByText('octocat / hello-world').length).toBeGreaterThan(0));
    expect(screen.queryByText('A sample repo')).toBeNull();
  });

  it('sorts repos alphabetically by name (case-insensitive)', async () => {
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([
      { ...REPO_C, source_login: 'octocat', source_is_default: true },
      { ...REPO, source_login: 'octocat', source_is_default: true },
      { ...REPO_B, source_login: 'altcat', source_is_default: false },
    ] as never);

    await openGitHubDialog();

    await waitFor(() => expect(apiClient.listAccessibleGitHubRepos).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('combobox', { name: 'Repository' }));

    await waitFor(() => expect(screen.getAllByText('octocat / aardvark').length).toBeGreaterThan(0));

    const options = screen.getAllByRole('option');
    const labels = options.map(o => o.textContent);
    expect(labels).toEqual(['GHoctocat / aardvarkvia @altcat', 'GHoctocat / hello-worldvia @octocat', 'GHoctocat / zebravia @octocat']);
  });

  it('still submits a manually typed owner/repo when repo browsing fails but a linked account exists', async () => {
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockRejectedValue(new ApiError(500, 'boom'));

    await openGitHubDialog();
    await waitFor(() =>
      expect(screen.getByText(/Could not load repositories/)).toBeDefined(),
    );

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

  it('requires at least one linked GitHub account before creating from GitHub in Entra mode', async () => {
    vi.mocked(apiClient.listLinkedGitHubAccounts).mockResolvedValue([] as never);
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([] as never);

    await openGitHubDialog();

    expect(await screen.findByText(/Link at least one GitHub account before importing a repository/)).toBeDefined();
    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'My Project' } });
    fireEvent.input(screen.getByRole('combobox', { name: 'Repository' }), { target: { value: 'me/manual-repo' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'my-repo' } });

    expect((screen.getByRole('button', { name: 'Create' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('normalizes a manually typed owner/repo to an HTTPS URL on submit', async () => {
    await openGitHubDialog();

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
    vi.mocked(apiClient.listProjects).mockResolvedValue(projectsPage([]));

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);

    await waitFor(() =>
      expect(screen.getByText('No projects yet')).toBeDefined(),
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
    await waitFor(() => expect(apiClient.listLinkedGitHubAccounts).toHaveBeenCalled());
    expect(screen.queryByPlaceholderText('my-repo')).toBeNull();
  });

  it('submits working_directory derived from the repo slug when workspace_auto_assigned is true', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      workspace_auto_assigned: true,
    } as never);
    vi.mocked(apiClient.listAccessibleGitHubRepos).mockResolvedValue([] as never);

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    const trigger = await screen.findByRole('button', { name: 'Create from GitHub' });
    fireEvent.click(trigger);
    await waitFor(() => expect(apiClient.listLinkedGitHubAccounts).toHaveBeenCalled());

    // Fill name and repo manually (linked-account path, no folder field).
    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'Hello World' } });
    const repoCombobox = screen.getByRole('combobox', { name: 'Repository' });
    fireEvent.input(repoCombobox, { target: { value: 'octocat/hello-world' } });
    const createButton = (await screen.findByText('Create project')).closest('button') as HTMLButtonElement | null;
    expect(createButton).toBeTruthy();
    await waitFor(() => expect(createButton?.disabled).toBe(false));

    fireEvent.click(createButton!);

    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalled());
    const req = vi.mocked(apiClient.createProject).mock.calls[0][0];
    expect(req.working_directory).toBe('hello-world');
    expect(req.source_repository).toBe('https://github.com/octocat/hello-world');
  });
});
