import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthConfig: vi.fn(),
    getAuthSession: vi.fn(),
    beginRepoAppAuthorization: vi.fn(),
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
    render(<MemoryRouter><AzureFluentProvider><SettingsPage /></AzureFluentProvider></MemoryRouter>);

    expect(await screen.findByText('GitHub connections')).toBeDefined();
    expect(screen.getByText(/separate Repo App provides repository access/i)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Repo App' }));
    await waitFor(() => expect(apiClient.beginRepoAppAuthorization).toHaveBeenCalled());
    expect(assign).toHaveBeenCalledWith('https://api.example.test/auth/github/repo-app/authorize');
    assign.mockRestore();
  });
});
