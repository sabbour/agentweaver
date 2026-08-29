import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getAuthSession: vi.fn(),
    getSandboxPolicy: vi.fn(),
    updateSandboxPolicy: vi.fn(),
  },
}));

afterEach(cleanup);

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
    platform_roles: ['PlatformAdmin', 'ProjectCreator'],
  } as never);
});

describe('SettingsPage', () => {
  it('shows Entra platform access, MCP configuration, and sandbox policy settings', async () => {
    render(
      <MemoryRouter>
        <AzureFluentProvider density="compact">
          <SettingsPage />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText('Entra ID')).toBeDefined();
    expect(screen.getByText('PlatformAdmin')).toBeDefined();
    expect(screen.getByText('MCP clients')).toBeDefined();
    expect(screen.getByDisplayValue(/\/mcp$/)).toBeDefined();
    expect(screen.getByText('Sandbox policy')).toBeDefined();
    expect(screen.queryByText(/Linked GitHub accounts/i)).toBeNull();
  });
});
