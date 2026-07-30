import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    getAuthSession: vi.fn(),
    listLinkedGitHubAccounts: vi.fn(),
    getSandboxPolicy: vi.fn(),
    updateSandboxPolicy: vi.fn(),
    setDefaultLinkedGitHubAccount: vi.fn(),
    unlinkLinkedGitHubAccount: vi.fn(),
  },
}));

afterEach(() => {
  cleanup();
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getServerInfo).mockResolvedValue({
    data_directory: 'C:/data',
    workspace_auto_assigned: false,
    auth_mode: 'entra',
  } as never);
  vi.mocked(apiClient.getAuthSession).mockResolvedValue({
    authenticated: true,
    auth_mode: 'entra',
    display_name: 'Ada Lovelace',
    email: 'ada@example.com',
    login: 'ada',
    avatar_url: null,
    entra_object_id: 'entra-1',
    platform_roles: ['PlatformAdmin', 'ProjectCreator'],
  } as never);
  vi.mocked(apiClient.listLinkedGitHubAccounts).mockResolvedValue([
    {
      login: 'octocat',
      name: 'Octocat',
      avatar_url: 'https://example.com/octocat.png',
      type: 'user',
      is_default: true,
      copilot_entitled: true,
    },
    {
      login: 'altcat',
      name: 'Alt Cat',
      avatar_url: 'https://example.com/altcat.png',
      type: 'user',
      is_default: false,
      copilot_entitled: false,
    },
  ] as never);
});

describe('SettingsPage', () => {
  it('shows auth mode, linked GitHub accounts, MCP URL, and sandbox policy section', async () => {
    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    await waitFor(() => expect(screen.getByText('Authentication')).toBeDefined());

    expect(screen.getByText('Entra ID')).toBeDefined();
    expect(screen.getByText('PlatformAdmin')).toBeDefined();
    expect(screen.getByText('Linked GitHub accounts')).toBeDefined();
    expect(await screen.findByText(/Octocat/)).toBeDefined();
    expect(screen.getByText('Copilot included')).toBeDefined();
    expect(screen.getByDisplayValue(/\/mcp$/)).toBeDefined();
    expect(screen.getByText('Sandbox policy')).toBeDefined();
  });

  it('sets a linked account as default', async () => {
    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    await screen.findByText('Alt Cat');
    fireEvent.click(screen.getByRole('button', { name: 'Set as default' }));

    await waitFor(() => expect(apiClient.setDefaultLinkedGitHubAccount).toHaveBeenCalledWith('altcat'));
  });

  it('confirms before unlinking a GitHub account', async () => {
    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    await screen.findByText(/Octocat/);
    fireEvent.click(screen.getAllByRole('button', { name: 'Unlink' })[0]!);
    expect(await screen.findByText('Unlink GitHub account')).toBeDefined();
    expect(screen.getByText(/shared fallback GitHub token/i)).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm unlink' }));

    await waitFor(() => expect(apiClient.unlinkLinkedGitHubAccount).toHaveBeenCalledWith('octocat'));
  });
});
