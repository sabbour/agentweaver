import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthConfig: vi.fn(),
    getAuthSession: vi.fn(),
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
  } as never);
});

describe('SettingsPage', () => {
  it('shows Entra platform access and MCP configuration', async () => {
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
});
