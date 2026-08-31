import { apiClient } from '../api/apiClient';
import { GitHubCopilotConnectionPicker } from '../components/GitHubCopilotConnectionPicker';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    beginProjectCopilotAuthorization: vi.fn(),
    getProjectCopilotConnection: vi.fn(),
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  return <AzureFluentProvider density="compact">{children}</AzureFluentProvider>;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
    status: 'not_connected',
    github_login: null,
  });
});

afterEach(cleanup);

describe('GitHubCopilotConnectionPicker', () => {
  it('keeps the new project connection when route navigation finishes an older request late', async () => {
    let resolveOldConnection: (connection: { status: 'connected'; github_login: string }) => void;
    const oldConnection = new Promise<{ status: 'connected'; github_login: string }>((resolve) => {
      resolveOldConnection = resolve;
    });
    vi.mocked(apiClient.getProjectCopilotConnection)
      .mockImplementationOnce(() => oldConnection)
      .mockResolvedValueOnce({ status: 'connected', github_login: 'project-b' });

    const { rerender } = render(
      <Wrapper>
        <GitHubCopilotConnectionPicker projectId="project-a" showConnectionStatus />
      </Wrapper>,
    );
    await waitFor(() => expect(apiClient.getProjectCopilotConnection).toHaveBeenCalledWith('project-a'));

    rerender(
      <Wrapper>
        <GitHubCopilotConnectionPicker projectId="project-b" showConnectionStatus />
      </Wrapper>,
    );
    expect(await screen.findByText(/GitHub Copilot is connected as @project-b/)).toBeDefined();

    await act(async () => {
      resolveOldConnection!({ status: 'connected', github_login: 'project-a' });
      await oldConnection;
    });

    expect(screen.getByText(/GitHub Copilot is connected as @project-b/)).toBeDefined();
    expect(screen.queryByText('GitHub Copilot is connected as @project-a.')).toBeNull();
  });

  it('shows an explicit, accessible connection picker when no account is connected', async () => {
    render(
      <Wrapper>
        <GitHubCopilotConnectionPicker projectId="project-1" showConnectionStatus />
      </Wrapper>,
    );

    expect(await screen.findByText(/No GitHub account is connected to this project for Copilot/)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));

    expect(await screen.findByRole('dialog')).toBeDefined();
    expect(screen.getByRole('heading', { name: 'Connect GitHub Copilot' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Connect GitHub account' })).toBeDefined();
  });

  it('refreshes the selected account after connection and starts the same retryable browser handoff', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://api.example.test/auth/github/copilot-app/authorize',
      transaction_id: 'opaque-transaction',
      expires_at: '2026-08-29T00:00:00Z',
    });
    render(
      <Wrapper>
        <GitHubCopilotConnectionPicker projectId="project-1" showConnectionStatus />
      </Wrapper>,
    );

    await screen.findByText(/No GitHub account is connected to this project for Copilot/);
    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));
    vi.mocked(apiClient.getProjectCopilotConnection).mockResolvedValue({
      status: 'connected',
      github_login: 'octocat',
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText('Connected GitHub account: @octocat')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Switch GitHub account' }));
    await waitFor(() => expect(apiClient.beginProjectCopilotAuthorization).toHaveBeenCalledWith('project-1'));
    expect(assign).toHaveBeenCalledWith('https://api.example.test/auth/github/copilot-app/authorize');
    assign.mockRestore();
  });

  it('keeps the picker usable and shows a clear error when the handoff cannot start', async () => {
    vi.mocked(apiClient.beginProjectCopilotAuthorization).mockRejectedValue(new Error('network'));
    render(
      <Wrapper>
        <GitHubCopilotConnectionPicker projectId="project-1" />
      </Wrapper>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Manage GitHub Copilot' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Connect GitHub account' }));

    expect(await screen.findByText('The GitHub Copilot App connection could not be started. Try again.')).toBeDefined();
    expect((screen.getByRole('button', { name: 'Connect GitHub account' }) as HTMLButtonElement).disabled).toBe(false);
  });
});
