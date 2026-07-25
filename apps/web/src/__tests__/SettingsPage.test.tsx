import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {},
}));

afterEach(() => {
  cleanup();
});

describe('SettingsPage', () => {
  it('shows the MCP server URL and the sandbox policy section', () => {
    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    expect(screen.getByText('MCP clients')).toBeDefined();
    expect(screen.getByDisplayValue(/\/mcp$/)).toBeDefined();
    expect(screen.getByText('Sandbox policy')).toBeDefined();
  });
});