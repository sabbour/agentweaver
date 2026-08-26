import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { GitHubSignIn } from '../components/GitHubSignIn';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthSession: vi.fn(),
    listLinkedGitHubAccounts: vi.fn(),
    setDefaultLinkedGitHubAccount: vi.fn(),
    signOutSession: vi.fn(),
    beginLinkGitHubAccount: vi.fn(),
  },
}));

afterEach(() => cleanup());

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getAuthSession).mockResolvedValue({
    authenticated: true,
    auth_mode: 'entra',
    display_name: 'Ada Lovelace',
    email: 'ada@example.com',
    login: 'ada',
    avatar_url: null,
    entra_object_id: 'entra-1',
    platform_roles: ['PlatformAdmin'],
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
  vi.mocked(apiClient.setDefaultLinkedGitHubAccount).mockResolvedValue(undefined as never);
  vi.mocked(apiClient.beginLinkGitHubAccount).mockResolvedValue({
    authorize_url: 'https://github.com/login/oauth/authorize?state=abc',
  } as never);
});

describe('GitHubSignIn', () => {
  it('shows current account and lets the user switch the current linked account', async () => {
    vi.mocked(apiClient.listLinkedGitHubAccounts)
      .mockResolvedValueOnce([
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
      ] as never)
      .mockResolvedValueOnce([
        {
          login: 'octocat',
          name: 'Octocat',
          avatar_url: 'https://example.com/octocat.png',
          type: 'user',
          is_default: false,
          copilot_entitled: true,
        },
        {
          login: 'altcat',
          name: 'Alt Cat',
          avatar_url: 'https://example.com/altcat.png',
          type: 'user',
          is_default: true,
          copilot_entitled: false,
        },
      ] as never);

    render(
      <AzureFluentProvider density="compact">
        <GitHubSignIn projectId="proj-1" />
      </AzureFluentProvider>,
    );

    expect(await screen.findByRole('button', { name: 'GitHub account switcher' })).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'GitHub account switcher' }));

    expect(await screen.findByText('Current GitHub account')).toBeDefined();
    expect(screen.getByText('@octocat')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: /Alt Cat/ }));

    await waitFor(() => expect(apiClient.setDefaultLinkedGitHubAccount).toHaveBeenCalledWith('altcat'));
    expect(await screen.findByText('@altcat')).toBeDefined();
    expect(apiClient.listLinkedGitHubAccounts).toHaveBeenCalledTimes(2);
  });

  it('starts the real link flow via beginLinkGitHubAccount when "Add account" is clicked', async () => {
    const originalLocation = window.location;
    // jsdom's window.location.href setter doesn't actually navigate, but redefining it lets
    // us assert the redirect target without triggering a real "not implemented" navigation error.
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...originalLocation, href: '' },
    });

    try {
      render(
        <AzureFluentProvider density="compact">
          <GitHubSignIn projectId="proj-1" />
        </AzureFluentProvider>,
      );

      fireEvent.click(await screen.findByRole('button', { name: 'GitHub account switcher' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Add account' }));

      await waitFor(() => expect(apiClient.beginLinkGitHubAccount).toHaveBeenCalled());
      await waitFor(() => expect(window.location.href).toBe('https://github.com/login/oauth/authorize?state=abc'));
    } finally {
      Object.defineProperty(window, 'location', { configurable: true, value: originalLocation });
    }
  });
});
