import App from '../App';
import { apiClient } from '../api/apiClient';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getServerInfo: vi.fn(),
    getAuthSession: vi.fn(),
    listLinkedGitHubAccounts: vi.fn(),
  },
}));

afterEach(() => cleanup());

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getAuthSession).mockResolvedValue({
    authenticated: false,
    auth_mode: 'entra',
    display_name: null,
    email: null,
    login: null,
    avatar_url: null,
    entra_object_id: null,
    platform_roles: [],
  } as never);
});

describe('AuthGate sign-in mode detection', () => {
  it('renders the Entra sign-in button when /api/server/info reports Entra mode', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      auth_mode: 'entra',
      auth_mode_label: 'Entra ID',
      auth_mode_recommended: true,
    } as never);

    render(<App />);

    expect(await screen.findByRole('button', { name: 'Sign in with Microsoft Entra ID' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Sign in with GitHub' })).toBeNull();
  });

  it('renders the GitHub sign-in button when /api/server/info reports GitHub-legacy mode', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({
      data_directory: '/data',
      auth_mode: 'github-legacy',
      auth_mode_label: 'GitHub',
      auth_mode_recommended: false,
    } as never);

    render(<App />);

    expect(await screen.findByRole('button', { name: 'Sign in with GitHub' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Sign in with Microsoft Entra ID' })).toBeNull();
  });

  it('falls back to GitHub only when the server omits auth_mode entirely', async () => {
    vi.mocked(apiClient.getServerInfo).mockResolvedValue({ data_directory: '/data' } as never);

    render(<App />);

    expect(await screen.findByRole('button', { name: 'Sign in with GitHub' })).toBeDefined();
  });
});
