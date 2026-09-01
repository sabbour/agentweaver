import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { ProjectListProvider } from '../hooks/useProjectList';
import { ProjectGalleryPage } from '../pages/ProjectGalleryPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    listProjects: vi.fn(),
    createProject: vi.fn(),
    listBlueprints: vi.fn(),
    generateBlueprint: vi.fn(),
    suggestBlueprint: vi.fn(),
    listGitHubRepositorySelections: vi.fn(),
    issueGitHubRepositorySelection: vi.fn(),
    beginRepoAppAuthorization: vi.fn(),
  },
}));

function Wrapper({ children, initialEntries }: { children: ReactNode; initialEntries?: string[] }) {
  return (
    <AzureFluentProvider density="compact">
      <MemoryRouter initialEntries={initialEntries}>
        <ProjectListProvider>{children}</ProjectListProvider>
      </MemoryRouter>
    </AzureFluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data', workspace_auto_assigned: false } as never);
  vi.mocked(apiClient.listProjects).mockResolvedValue({ items: [], page: 1, page_size: 100, total_count: 0, total_pages: 1 } as never);
  vi.mocked(apiClient.listBlueprints).mockResolvedValue([]);
  vi.mocked(apiClient.suggestBlueprint).mockResolvedValue({ recommended_blueprint: null, rationale: '', confidence: 0, signals: [], fallback: true });
  vi.mocked(apiClient.listGitHubRepositorySelections).mockResolvedValue({
    repositories: [{ full_name: 'octocat/hello-world', owner_login: 'octocat', private: false, default_branch: 'main', pushed_at: null }],
  } as never);
  vi.mocked(apiClient.issueGitHubRepositorySelection).mockResolvedValue({
    selection_code: 'opaque-selection-code',
    expires_at: '2026-08-28T00:05:00+00:00',
  } as never);
  vi.mocked(apiClient.createProject).mockResolvedValue({ project_id: 'new' } as never);
});

afterEach(cleanup);

describe('ProjectGalleryPage repository authorization', () => {
  it('uses the Repo App selection handoff when creating a project', async () => {
    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    fireEvent.click(await screen.findByRole('button', { name: 'Create from GitHub' }));
    await screen.findByText(/available through the Repo App/i);

    fireEvent.change(screen.getByPlaceholderText('My project'), { target: { value: 'Hello World' } });
    fireEvent.input(screen.getByRole('combobox', { name: 'Repository' }), { target: { value: 'octocat/hello-world' } });
    fireEvent.change(screen.getByPlaceholderText('my-repo'), { target: { value: 'hello-world' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(apiClient.issueGitHubRepositorySelection).toHaveBeenCalledWith('octocat/hello-world'));
    await waitFor(() => expect(apiClient.createProject).toHaveBeenCalledWith(expect.objectContaining({
      repository_selection_code: 'opaque-selection-code',
    })));
  });

  it('shows and dismisses a safe Copilot App callback result', async () => {
    render(<Wrapper initialEntries={['/projects?copilot_app_auth=success']}><ProjectGalleryPage /></Wrapper>);

    expect(await screen.findByText(/The Copilot App is connected to this project/)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByText(/The Copilot App is connected to this project/)).toBeNull();
  });

  it('offers a Connect GitHub action when the Repo App is not yet connected', async () => {
    vi.mocked(apiClient.listGitHubRepositorySelections).mockRejectedValue(
      new ApiError(409, JSON.stringify({ error: 'github_binding_unavailable' })),
    );
    vi.mocked(apiClient.beginRepoAppAuthorization).mockResolvedValue({
      authorization_url: 'https://github.com/login/oauth/authorize?client_id=repo-app',
      transaction_id: 'txn-1',
      expires_at: '2026-08-28T00:05:00+00:00',
    });
    const assignSpy = vi.fn();
    vi.stubGlobal('location', { ...window.location, assign: assignSpy });

    render(<Wrapper><ProjectGalleryPage /></Wrapper>);
    fireEvent.click(await screen.findByRole('button', { name: 'Create from GitHub' }));

    const connectButton = await screen.findByRole('button', { name: 'Connect GitHub' });
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();

    fireEvent.click(connectButton);
    await waitFor(() => expect(apiClient.beginRepoAppAuthorization).toHaveBeenCalled());
    await waitFor(() => expect(assignSpy).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?client_id=repo-app'));

    vi.unstubAllGlobals();
  });
});
