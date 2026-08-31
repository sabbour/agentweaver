import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { GitHubIdentityBadge } from '../components/GitHubIdentityBadge';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthSession: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
    getProjectAccessOverview: vi.fn(),
    signOutSession: vi.fn(),
  },
}));

function renderBadge(props: { projectId?: string; collapsed?: boolean } = {}) {
  return render(
    <AzureFluentProvider density="compact">
      <GitHubIdentityBadge {...props} />
    </AzureFluentProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getAuthSession).mockResolvedValue({
    authenticated: true,
    auth_mode: 'entra',
    display_name: 'Ada Lovelace',
    email: 'ada@example.com',
    login: 'ada',
    avatar_url: 'https://example.com/ada.png',
    entra_object_id: 'entra-1',
    platform_roles: [],
    ai_configured: true,
  } as never);
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'connected',
    github_login: 'octocat',
  });
  vi.mocked(apiClient.getProjectAccessOverview).mockResolvedValue({
    effective_github_login: 'octocat',
  } as never);
  vi.mocked(apiClient.signOutSession).mockResolvedValue(undefined as never);
});

afterEach(() => cleanup());

describe('GitHubIdentityBadge', () => {
  it('shows the signed-in identity and read-only project GitHub status links', async () => {
    renderBadge({ projectId: 'proj-1' });

    fireEvent.click(screen.getByRole('button', { name: 'GitHub identity' }));

    await screen.findByText('Signed in');
    expect(screen.getAllByText('Ada Lovelace')).toHaveLength(2);
    expect(await screen.findByText('Repository access: @octocat')).toBeDefined();
    expect(screen.getByText('AI source: GitHub Copilot — Connected as @octocat')).toBeDefined();
    expect(apiClient.getProjectCopilotConnection).toHaveBeenCalledWith('proj-1');
    expect(apiClient.getProjectAccessOverview).toHaveBeenCalledWith('proj-1');
    expect(screen.getByRole('link', { name: 'Manage project connections' }).getAttribute('href')).toBe('/projects/proj-1/settings');
    expect(screen.getByRole('link', { name: 'Manage account GitHub connections' }).getAttribute('href')).toBe('/settings');
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeDefined();
  });

  it('omits the project connection section without an active project', async () => {
    renderBadge();

    fireEvent.click(screen.getByRole('button', { name: 'GitHub identity' }));

    expect(await screen.findByText('Signed in')).toBeDefined();
    expect(screen.queryByText('Project GitHub status')).toBeNull();
    expect(apiClient.getProjectCopilotConnection).not.toHaveBeenCalled();
  });

  it('signs out and returns to the app root', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    renderBadge();

    fireEvent.click(screen.getByRole('button', { name: 'GitHub identity' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    await waitFor(() => expect(apiClient.signOutSession).toHaveBeenCalled());
    expect(assign).toHaveBeenCalledWith('/');
    assign.mockRestore();
  });

  it('keeps the trigger icon-only when the navigation rail is collapsed', async () => {
    renderBadge({ collapsed: true });

    await waitFor(() => expect(apiClient.getAuthSession).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'GitHub identity' }).textContent).toBe('');
  });
});
