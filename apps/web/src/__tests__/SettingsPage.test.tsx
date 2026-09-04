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

  it('shows client-specific OAuth guidance and copies the exact MCP URL', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });

    render(
      <MemoryRouter>
        <AzureFluentProvider density="compact">
          <SettingsPage />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText(/discovers Agentweaver OAuth automatically/i)).toBeDefined();
    const urlInput = screen.getByRole('textbox', { name: 'MCP server URL' }) as HTMLInputElement;
    expect(urlInput.value).toMatch(/\/mcp$/);
    expect(screen.getByRole('tablist', { name: 'MCP client setup' })).toBeDefined();
    for (const clientName of [
      'Claude Desktop',
      'VS Code',
      'GitHub Copilot CLI',
      'GitHub Copilot desktop',
    ]) {
      expect(screen.getByRole('tab', { name: clientName })).toBeDefined();
    }
    expect(screen.getByRole('tabpanel').textContent).toContain('Settings → Connectors');

    fireEvent.click(screen.getByRole('tab', { name: 'VS Code' }));
    expect(screen.getByRole('tabpanel').textContent).toContain('MCP: Add Server');

    fireEvent.click(screen.getByRole('tab', { name: 'GitHub Copilot CLI' }));
    expect(screen.getByRole('tabpanel').textContent).toContain('/mcp show agentweaver');

    fireEvent.click(screen.getByRole('tab', { name: 'GitHub Copilot desktop' }));
    expect(screen.getByRole('tabpanel').textContent).toContain('Customize → MCP servers');

    fireEvent.click(screen.getByRole('button', { name: 'Copy MCP server URL' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(urlInput.value));
    expect(screen.getByRole('button', { name: 'Copied' })).toBeDefined();

    const agentLink = screen.getByRole('link', { name: 'Open agent definition' });
    const expectedAgentUrl =
      `${new URL(urlInput.value).origin}/agents/agentweaver.agent.md`;
    expect(agentLink.getAttribute('href')).toBe(
      expectedAgentUrl,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Copy Agentweaver Driver URL' }));
    await waitFor(() => expect(writeText).toHaveBeenLastCalledWith(expectedAgentUrl));
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
    expect(screen.getByText(/separate Repo App provides repository access/i)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Connect GitHub Repo App' }));
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

    expect(await screen.findByText(/Connected GitHub login: @sabbour/)).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Connect GitHub Repo App' })).toBeNull();
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
