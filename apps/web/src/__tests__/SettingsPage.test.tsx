import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthConfig: vi.fn(),
    getAuthSession: vi.fn(),
    beginRepoAppAuthorization: vi.fn(),
    getRepoAppConnectionStatus: vi.fn(),
  },
}));

afterEach(cleanup);

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(apiClient.getAuthConfig).mockResolvedValue({
    mode: 'Entra',
    entra: {
      tenant_id: 'tenant-1',
      client_id: 'client-1',
      enterprise_app_object_id: null,
      authority: 'https://login.microsoftonline.com/tenant-1/v2.0',
    },
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
    ai_configured: true,
  } as never);
  vi.mocked(apiClient.getRepoAppConnectionStatus).mockResolvedValue({
    connected: false,
    github_login: null,
  } as never);
});

describe('SettingsPage', () => {
  it('shows Entra platform access and falls back to the app-roles link when no enterprise app object ID is configured', async () => {
    render(
      <MemoryRouter>
        <AzureFluentProvider density="compact">
          <SettingsPage />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText('Entra ID')).toBeDefined();
    expect(screen.getByText('PlatformAdmin')).toBeDefined();
    expect((await screen.findByRole('link', { name: 'Manage in Microsoft Entra ID' })).getAttribute('href'))
      .toBe('https://entra.microsoft.com/tenant-1/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/AppRoles/appId/client-1/isMSAApp~/false');
    expect(screen.getByText('MCP clients')).toBeDefined();
    expect(screen.getByDisplayValue(/\/mcp$/)).toBeDefined();
    expect(screen.queryByText('Sandbox policy')).toBeNull();
    expect(screen.queryByText(/Linked GitHub accounts/i)).toBeNull();
  });

  it('uses the Azure Portal users blade when the enterprise app object ID is configured', async () => {
    vi.mocked(apiClient.getAuthConfig).mockResolvedValue({
      mode: 'Entra',
      entra: {
        tenant_id: 'tenant-1',
        client_id: 'client-1',
        enterprise_app_object_id: 'enterprise app/object',
        authority: 'https://login.microsoftonline.com/tenant-1/v2.0',
      },
    } as never);

    render(
      <MemoryRouter>
        <AzureFluentProvider density="compact">
          <SettingsPage />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect((await screen.findByRole('link', { name: 'Manage users in Azure Portal' })).getAttribute('href'))
      .toBe('https://ms.portal.azure.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Users/objectId/enterprise%20app%2Fobject/appId/client-1');
  });

  it('explains the two GitHub Apps and starts the Repo App connection', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.beginRepoAppAuthorization).mockResolvedValue({
      authorization_url: 'https://api.example.test/auth/github/repo-app/authorize',
      transaction_id: 'transaction',
      expires_at: '2026-09-01T00:00:00Z',
    });
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <AzureFluentProvider><SettingsPage /></AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText('GitHub connections')).toBeDefined();
    expect(screen.getByText('GitHub Copilot provides AI access. The Repo App provides repository access.')).toBeDefined();
    expect(screen.getAllByText('Optional').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Authorize repository access' }));
    await waitFor(() => expect(apiClient.beginRepoAppAuthorization).toHaveBeenCalledWith('/settings'));
    expect(assign).toHaveBeenCalledWith('https://api.example.test/auth/github/repo-app/authorize');
    assign.mockRestore();
  });

  it('shows the connected Repo App login and refreshes it after a successful callback redirect', async () => {
    vi.mocked(apiClient.getRepoAppConnectionStatus)
      .mockResolvedValueOnce({ connected: false, github_login: null } as never)
      .mockResolvedValueOnce({ connected: true, github_login: 'sabbour' } as never);

    render(
      <MemoryRouter initialEntries={['/settings?repo_app_auth=success']}>
        <AzureFluentProvider density="compact">
          <SettingsPage />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText(/Repository access is ready for @sabbour/)).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Authorize repository access' })).toBeNull();
    await waitFor(() => expect(apiClient.getRepoAppConnectionStatus).toHaveBeenCalledTimes(2));
  });

  it('navigates to the last active project\'s settings page when one is remembered', async () => {
    localStorage.setItem('agentweaver:last-active-project-id', 'proj-a');
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <AzureFluentProvider>
          <Routes>
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/projects/:projectId/settings" element={<div>Project settings route</div>} />
          </Routes>
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Manage Copilot connections in projects' }));
    expect(await screen.findByText('Project settings route')).toBeDefined();
    localStorage.removeItem('agentweaver:last-active-project-id');
  });

  it('navigates to the landing page when no project is remembered', async () => {
    localStorage.removeItem('agentweaver:last-active-project-id');
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <AzureFluentProvider>
          <Routes>
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/" element={<div>Landing route</div>} />
          </Routes>
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Manage Copilot connections in projects' }));
    expect(await screen.findByText('Landing route')).toBeDefined();
  });
});
