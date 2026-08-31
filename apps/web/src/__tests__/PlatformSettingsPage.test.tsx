import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { PlatformSettingsPage } from '../pages/PlatformSettingsPage';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    getByokProviderConfig: vi.fn(),
    setByokProviderConfig: vi.fn(),
    clearByokProviderConfig: vi.fn(),
    getPlatformDefaultCopilotConnection: vi.fn(),
    beginPlatformDefaultCopilotAuthorization: vi.fn(),
    disconnectPlatformDefaultCopilotConnection: vi.fn(),
  },
}));

function renderPage(initialEntry = '/platform-settings') {
  render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <AzureFluentProvider density="compact">
        <PlatformSettingsPage />
      </AzureFluentProvider>
    </MemoryRouter>,
  );
}

describe('PlatformSettingsPage', () => {
  beforeEach(() => {
    vi.mocked(apiClient.getByokProviderConfig).mockReset();
    vi.mocked(apiClient.setByokProviderConfig).mockReset();
    vi.mocked(apiClient.clearByokProviderConfig).mockReset();
    vi.mocked(apiClient.getPlatformDefaultCopilotConnection).mockReset();
    vi.mocked(apiClient.beginPlatformDefaultCopilotAuthorization).mockReset();
    vi.mocked(apiClient.disconnectPlatformDefaultCopilotConnection).mockReset();
    vi.mocked(apiClient.getPlatformDefaultCopilotConnection).mockResolvedValue({
      connected: false,
      github_login: null,
    });
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
    expect(await screen.findByText(/No platform-default GitHub Copilot account is connected yet/)).toBeDefined();
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
    fireEvent.click(screen.getByLabelText(/OpenAI-compatible/));
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
    const onRetryAccess = vi.fn();
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue({
      type: 'openai',
      base_url: 'https://api.example.com',
      model: 'gpt-4o',
      configured: true,
    });
    vi.mocked(apiClient.clearByokProviderConfig).mockResolvedValue(undefined);
    render(
      <MemoryRouter initialEntries={['/platform-settings']}>
        <AzureFluentProvider density="compact">
          <PlatformSettingsPage onRetryAccess={onRetryAccess} />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByLabelText(/GitHub Copilot mode/));
    fireEvent.click(screen.getByRole('button', { name: /Switch to GitHub Copilot mode/ }));

    await waitFor(() => expect(apiClient.clearByokProviderConfig).toHaveBeenCalled());
    expect(onRetryAccess).toHaveBeenCalled();
  });

  it('starts the platform-default Copilot OAuth redirect', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue(null);
    vi.mocked(apiClient.beginPlatformDefaultCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://github.com/login/oauth/authorize?state=test',
      transaction_id: 'txn',
      expires_at: '2026-08-31T12:00:00Z',
    });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Connect GitHub Copilot' }));

    await waitFor(() => expect(apiClient.beginPlatformDefaultCopilotAuthorization).toHaveBeenCalled());
    expect(assign).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?state=test');
    assign.mockRestore();
  });

  it('shows the connected platform-default GitHub login and disconnects it', async () => {
    const onRetryAccess = vi.fn();
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue(null);
    vi.mocked(apiClient.getPlatformDefaultCopilotConnection)
      .mockResolvedValueOnce({ connected: true, github_login: 'octocat' })
      .mockResolvedValueOnce({ connected: false, github_login: null });
    vi.mocked(apiClient.disconnectPlatformDefaultCopilotConnection).mockResolvedValue(undefined);
    render(
      <MemoryRouter initialEntries={['/platform-settings']}>
        <AzureFluentProvider density="compact">
          <PlatformSettingsPage onRetryAccess={onRetryAccess} />
        </AzureFluentProvider>
      </MemoryRouter>,
    );

    expect(await screen.findByText(/Connected GitHub login: @octocat/)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Disconnect' }));

    await waitFor(() => expect(apiClient.disconnectPlatformDefaultCopilotConnection).toHaveBeenCalled());
    expect(await screen.findByText('Configuration saved.')).toBeDefined();
    expect(onRetryAccess).toHaveBeenCalled();
  });

  it('shows the callback success notice without echoing the raw query value', async () => {
    vi.mocked(apiClient.getByokProviderConfig).mockResolvedValue(null);
    renderPage('/platform-settings?copilot_app_auth=success');

    expect(await screen.findByText(/platform-default GitHub Copilot account is connected/i)).toBeDefined();
    expect(screen.queryByText('success')).toBeNull();
  });
});
