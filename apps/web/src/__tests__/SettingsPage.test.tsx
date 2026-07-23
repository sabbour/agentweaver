import { AzureFluentProvider } from '../copilot-fluent-system';
import { SettingsPage } from '../pages/SettingsPage';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {},
}));

afterEach(() => {
  cleanup();
});

describe('SettingsPage', () => {
  it('shows a masked MCP configuration for each supported client', () => {
    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    expect(screen.getByText('MCP clients')).toBeDefined();
    expect(screen.getByDisplayValue(/\/mcp$/)).toBeDefined();
    expect((screen.getByRole('textbox', { name: /Claude Desktop/i }) as HTMLTextAreaElement).value)
      .toContain('Bearer ${AGENTWEAVER_TOKEN}');
    expect((screen.getByRole('textbox', { name: /VS Code/i }) as HTMLTextAreaElement).value)
      .toContain('${input:agentweaver-token}');
    expect((screen.getByRole('textbox', { name: /GitHub Copilot CLI/i }) as HTMLTextAreaElement).value)
      .toContain('Bearer ${AGENTWEAVER_TOKEN}');
    expect(screen.getAllByRole('button', { name: 'Copy config' })).toHaveLength(3);
  });

  it('copies the selected MCP client configuration', async () => {
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined);

    render(
      <AzureFluentProvider density="compact">
        <SettingsPage />
      </AzureFluentProvider>,
    );

    fireEvent.click(screen.getAllByRole('button', { name: 'Copy config' })[0]);

    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"mcpServers"'));
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeDefined();
  });
});
