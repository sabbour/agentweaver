import { apiClient } from '../api/apiClient';
import { AzureFluentProvider } from '../copilot-fluent-system';
import { PlatformSettingsPage } from '../pages/PlatformSettingsPage';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../api/apiClient', () => ({
  apiClient: {
    listByokProviders: vi.fn(),
    addByokProvider: vi.fn(),
    updateByokProvider: vi.fn(),
    removeByokProvider: vi.fn(),
    activateByokProvider: vi.fn(),
    deactivateByokProviders: vi.fn(),
    getPlatformDefaultCopilotConnection: vi.fn(),
    beginPlatformDefaultCopilotAuthorization: vi.fn(),
    disconnectPlatformDefaultCopilotConnection: vi.fn(),
  },
}));

function renderPage(initialEntry = '/platform-settings', props: { onRetryAccess?: () => void } = {}) {
  render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <AzureFluentProvider density="compact">
        <PlatformSettingsPage {...props} />
      </AzureFluentProvider>
    </MemoryRouter>,
  );
}

const emptyList = { active_provider_id: null, providers: [] };

const customProvider = {
  id: 'p1',
  name: 'My custom endpoint',
  type: 'openai' as const,
  base_url: 'https://api.example.com/v1',
  model: 'my-model',
  wire_api: 'responses' as const,
  azure_api_version: null,
  headers: null,
  has_api_key: true,
  is_active: false,
};

describe('PlatformSettingsPage', () => {
  beforeEach(() => {
    vi.mocked(apiClient.listByokProviders).mockReset();
    vi.mocked(apiClient.addByokProvider).mockReset();
    vi.mocked(apiClient.updateByokProvider).mockReset();
    vi.mocked(apiClient.removeByokProvider).mockReset();
    vi.mocked(apiClient.activateByokProvider).mockReset();
    vi.mocked(apiClient.deactivateByokProviders).mockReset();
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

  it('always shows GitHub Copilot first as Active by default when no provider is configured', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    renderPage();

    expect(await screen.findByText('Platform settings')).toBeDefined();
    expect(await screen.findByText('GitHub Copilot')).toBeDefined();
    expect(await screen.findAllByText('Authorize GitHub Copilot to use it as the platform model provider.')).toHaveLength(1);
    expect(screen.getByText('Action required')).toBeDefined();
    // The GitHub Copilot card shows "Active" (no BYOK provider is active).
    const copilotHeading = screen.getByText('GitHub Copilot');
    const card = copilotHeading.closest('.fui-Card') ?? copilotHeading.parentElement!.parentElement!;
    expect(within(card as HTMLElement).getByText('Active')).toBeDefined();
  });

  it('lists configured custom providers below GitHub Copilot', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue({
      active_provider_id: null,
      providers: [customProvider],
    });
    renderPage();

    expect(await screen.findByText('My custom endpoint')).toBeDefined();
    expect(screen.getByText(/Custom endpoint · my-model/)).toBeDefined();
    expect(screen.getByRole('button', { name: 'Set active' })).toBeDefined();
  });

  it('identifies an active custom-key provider and its platform scope', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue({
      active_provider_id: 'p1',
      providers: [{ ...customProvider, is_active: true }],
    });
    renderPage();

    expect(await screen.findByText(
      'My custom endpoint (Custom endpoint) supplies AI access. Scope: Platform.',
    )).toBeDefined();
    expect(screen.getAllByText('Ready').length).toBeGreaterThan(0);
  });

  it('does not offer Foundry Local or Microsoft Foundry as addable provider types', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add provider' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText('Custom endpoint')).toBeDefined();
    expect(within(dialog).getByText('Azure OpenAI')).toBeDefined();
    expect(within(dialog).getByText('Anthropic')).toBeDefined();
    expect(within(dialog).queryByText(/Foundry/)).toBeNull();
  });

  it('filters the provider type picker by search text', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add provider' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.change(within(dialog).getByPlaceholderText('Search provider types'), {
      target: { value: 'azure' },
    });

    expect(within(dialog).getByText('Azure OpenAI')).toBeDefined();
    expect(within(dialog).queryByText('Custom endpoint')).toBeNull();
    expect(within(dialog).queryByText('Anthropic')).toBeNull();
  });

  it('adds a new custom endpoint provider through the type picker and inline form', async () => {
    vi.mocked(apiClient.listByokProviders)
      .mockResolvedValueOnce(emptyList)
      .mockResolvedValueOnce({ active_provider_id: null, providers: [customProvider] });
    vi.mocked(apiClient.addByokProvider).mockResolvedValue(customProvider);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add provider' }));
    fireEvent.click(await screen.findByText('Custom endpoint'));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Add Custom endpoint')).toBeDefined();

    fireEvent.change(within(dialog).getByPlaceholderText('My provider'), {
      target: { value: 'My custom endpoint' },
    });
    fireEvent.change(within(dialog).getByPlaceholderText('https://api.example.com/v1'), {
      target: { value: 'https://api.example.com/v1' },
    });
    fireEvent.change(within(dialog).getByPlaceholderText('gpt-4o'), { target: { value: 'my-model' } });

    // API key is optional for a custom endpoint — "Add provider" should already be enabled.
    const addButton = within(dialog).getByRole('button', { name: 'Add provider' });
    expect(addButton).toHaveProperty('disabled', false);
    fireEvent.click(addButton);

    await waitFor(() => expect(apiClient.addByokProvider).toHaveBeenCalledWith(expect.objectContaining({
      name: 'My custom endpoint',
      type: 'openai',
      base_url: 'https://api.example.com/v1',
      model: 'my-model',
      api_key: null,
    })));
    expect(await screen.findByText(/was added/)).toBeDefined();
  });

  it('requires an API key for Azure and Anthropic providers before enabling Add provider', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add provider' }));
    fireEvent.click(await screen.findByText('Azure OpenAI'));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByPlaceholderText('My provider'), { target: { value: 'Azure prod' } });
    fireEvent.change(within(dialog).getByPlaceholderText('https://my-resource.openai.azure.com'), {
      target: { value: 'https://my-resource.openai.azure.com' },
    });
    fireEvent.change(within(dialog).getByPlaceholderText('gpt-4o'), { target: { value: 'gpt-4o-deployment' } });

    expect(within(dialog).getByRole('button', { name: 'Add provider' })).toHaveProperty('disabled', true);

    fireEvent.change(within(dialog).getByLabelText(/^API key/), { target: { value: 'azure-key' } });
    expect(within(dialog).getByRole('button', { name: 'Add provider' })).toHaveProperty('disabled', false);
  });

  it('edits an existing provider pre-filled and keeps the saved key when left blank', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue({
      active_provider_id: null,
      providers: [customProvider],
    });
    vi.mocked(apiClient.updateByokProvider).mockResolvedValue({ ...customProvider, name: 'Renamed endpoint' });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByDisplayValue('My custom endpoint')).toBeDefined();
    expect(within(dialog).getByDisplayValue('https://api.example.com/v1')).toBeDefined();

    fireEvent.change(within(dialog).getByDisplayValue('My custom endpoint'), {
      target: { value: 'Renamed endpoint' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(apiClient.updateByokProvider).toHaveBeenCalledWith('p1', expect.objectContaining({
      name: 'Renamed endpoint',
      api_key: null,
    })));
  });

  it('removes a provider only after confirming', async () => {
    vi.mocked(apiClient.listByokProviders)
      .mockResolvedValueOnce({ active_provider_id: null, providers: [customProvider] })
      .mockResolvedValueOnce(emptyList);
    vi.mocked(apiClient.removeByokProvider).mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Remove provider?')).toBeDefined();

    expect(apiClient.removeByokProvider).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove provider' }));

    await waitFor(() => expect(apiClient.removeByokProvider).toHaveBeenCalledWith('p1'));
  });

  it('sets a configured provider active and shows it as Active afterward', async () => {
    vi.mocked(apiClient.listByokProviders)
      .mockResolvedValueOnce({ active_provider_id: null, providers: [customProvider] })
      .mockResolvedValueOnce({ active_provider_id: 'p1', providers: [{ ...customProvider, is_active: true }] });
    vi.mocked(apiClient.activateByokProvider).mockResolvedValue(undefined);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Set active' }));

    await waitFor(() => expect(apiClient.activateByokProvider).toHaveBeenCalledWith('p1'));
    expect(await screen.findByText(/is now the active AI inference source/)).toBeDefined();
  });

  it('switches back to GitHub Copilot via its own Set active action', async () => {
    const onRetryAccess = vi.fn();
    vi.mocked(apiClient.listByokProviders)
      .mockResolvedValueOnce({ active_provider_id: 'p1', providers: [{ ...customProvider, is_active: true }] })
      .mockResolvedValueOnce(emptyList);
    vi.mocked(apiClient.deactivateByokProviders).mockResolvedValue(undefined);
    renderPage('/platform-settings', { onRetryAccess });

    fireEvent.click(await screen.findByRole('button', { name: 'Set active' }));

    await waitFor(() => expect(apiClient.deactivateByokProviders).toHaveBeenCalled());
    expect(onRetryAccess).toHaveBeenCalled();
  });

  it('starts the platform-default Copilot OAuth redirect', async () => {
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => {});
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    vi.mocked(apiClient.beginPlatformDefaultCopilotAuthorization).mockResolvedValue({
      authorization_url: 'https://github.com/login/oauth/authorize?state=test',
      transaction_id: 'txn',
      expires_at: '2026-08-31T12:00:00Z',
    });
    renderPage();

    fireEvent.click((await screen.findAllByRole('button', { name: 'Authorize GitHub Copilot' }))[0]);

    await waitFor(() => expect(apiClient.beginPlatformDefaultCopilotAuthorization).toHaveBeenCalled());
    expect(assign).toHaveBeenCalledWith('https://github.com/login/oauth/authorize?state=test');
    assign.mockRestore();
  });

  it('shows the callback success notice without echoing the raw query value', async () => {
    vi.mocked(apiClient.listByokProviders).mockResolvedValue(emptyList);
    renderPage('/platform-settings?copilot_app_auth=success');

    expect(await screen.findByText(/platform-default GitHub Copilot account is connected/i)).toBeDefined();
    expect(screen.queryByText('success')).toBeNull();
  });
});
