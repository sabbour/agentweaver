import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { PlatformSettingsPage } from '../pages/PlatformSettingsPage';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getByokProviderConfig: vi.fn(),
    setByokProviderConfig: vi.fn(),
    clearByokProviderConfig: vi.fn(),
  },
}));

function renderPage() {
  render(
    <AzureFluentProvider density="compact">
      <PlatformSettingsPage />
    </AzureFluentProvider>,
  );
}

describe('PlatformSettingsPage', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getByokProviderConfig).mockReset();
    vi.mocked(apiClient.setByokProviderConfig).mockReset();
    vi.mocked(apiClient.clearByokProviderConfig).mockReset();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('defaults to GitHub Copilot mode when no BYOK config is saved', async () => {
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue(null);
    renderPage();

    expect(await screen.findByText('Platform settings')).toBeDefined();
    const copilotRadio = (await screen.findByLabelText(
      /GitHub Copilot mode/,
    )) as HTMLInputElement;
    expect(copilotRadio.checked).toBe(true);
  });

  it('shows the BYOK form pre-filled when a custom key is already configured', async () => {
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue({
      type: 'openai',
      base_url: 'https://api.example.com',
      model: 'gpt-4o',
      configured: true,
    });
    renderPage();

    const byokRadio = (await screen.findByLabelText(/Custom key mode/)) as HTMLInputElement;
    expect(byokRadio.checked).toBe(true);
    expect(screen.getByDisplayValue('https://api.example.com')).toBeDefined();
    expect(screen.getByDisplayValue('gpt-4o')).toBeDefined();
  });

  it('saves a new custom key configuration', async () => {
    vi.mocked(apiClient.getByokProviderConfig)
      .mockResolvedValueOnce(null)
      .mockResolvedValueOnce({
        type: 'openai',
        base_url: 'https://api.example.com',
        model: 'gpt-4o',
        configured: true,
      });
    vi.mocked(apiClient.setByokProviderConfig).mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(await screen.findByLabelText(/Custom key mode/));
    fireEvent.change(screen.getByPlaceholderText('https://api.example.com'), {
      target: { value: 'https://api.example.com' },
    });
    fireEvent.change(screen.getByPlaceholderText('gpt-4o'), { target: { value: 'gpt-4o' } });
    fireEvent.change(screen.getByLabelText(/^API key/), { target: { value: 'sk-test' } });
    fireEvent.click(screen.getByRole('button', { name: /Save custom key configuration/ }));

    await waitFor(() => expect(apiClient.setByokProviderConfig).toHaveBeenCalledWith({
      type: 'openai',
      base_url: 'https://api.example.com',
      model: 'gpt-4o',
      api_key: 'sk-test',
    }));
    expect(await screen.findByText('Configuration saved.')).toBeDefined();
  });

  it('switches back to GitHub Copilot mode by clearing the saved configuration', async () => {
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue({
      type: 'openai',
      base_url: 'https://api.example.com',
      model: 'gpt-4o',
      configured: true,
    });
    vi.mocked(apiClient.clearByokProviderConfig).mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(await screen.findByLabelText(/GitHub Copilot mode/));
    fireEvent.click(screen.getByRole('button', { name: /Switch to GitHub Copilot mode/ }));

    await waitFor(() => expect(apiClient.clearByokProviderConfig).toHaveBeenCalled());
  });
});
