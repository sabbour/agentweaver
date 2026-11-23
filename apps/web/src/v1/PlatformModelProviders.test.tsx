import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { PlatformModelProviders } from './PlatformModelProviders';

const api = vi.hoisted(() => ({
  listPlatformByokProviderDrafts: vi.fn(),
  getPlatformByokProviderDraft: vi.fn(),
  createPlatformByokProviderDraft: vi.fn(),
  updatePlatformByokProviderDraft: vi.fn(),
  savePlatformManualRuntimeRateCard: vi.fn(),
  setPlatformByokProviderCredential: vi.fn(),
  activatePlatformByokProvider: vi.fn(),
  deactivatePlatformByokProvider: vi.fn(),
}));
const auth = vi.hoisted(() => ({
  session: { accessToken: 'token-1', binding: null },
  active: true,
  authSessionGeneration: 1,
  authSessionEpoch: 1,
  unboundSessionGeneration: 1,
  tenant: 'tenant-1' as string | null,
  authorizationContext: { tenantId: 'tenant-1' } as { tenantId: string } | null,
  apiCall: vi.fn(async (operation: (token: string, tenant: string | null) => unknown) =>
    operation('token-1', 'tenant-1')),
  isActiveSession: vi.fn(() => auth.active),
  ensureSession: vi.fn().mockResolvedValue(true),
  authorize: vi.fn().mockResolvedValue(undefined),
  resolveAuthorizationContext: vi.fn(async () => ({
    issuer: 'issuer', actorId: 'actor', tenantId: auth.tenant, membershipRevision: 1,
  })),
}));
vi.mock('./AuthContext', () => ({ useAuth: () => auth }));
vi.mock('./api', async (importActual) => ({
  ...(await importActual<typeof import('./api')>()),
  gatewayClient: api,
}));

function provider(overrides: Record<string, unknown> = {}) {
  return {
    id: 'provider-1',
    name: 'Primary',
    type: 'openai',
    base_url: 'https://api.example.test/v1',
    model: 'model-1',
    wire_api: 'responses',
    azure_api_version: null,
    reasoning_efforts: ['high'],
    prompt_capacity_tokens: 128000,
    rate_card_id: 'card-1',
    rate_card_version: '1',
    scope: 'platform',
    projectId: null,
    state: 'Available',
    revision: 4,
    configurationHash: 'hash',
    has_api_key: false,
    is_active: true,
    hasCredential: true,
    ...overrides,
  };
}

function setPage(pageOverrides: Record<string, unknown> = {}) {
  api.listPlatformByokProviderDrafts.mockResolvedValue({
    switchMode: 'active',
    activeProviderId: 'provider-1',
    settingsRevision: 9,
    providers: [provider()],
    nextAfterProviderId: null,
    ...pageOverrides,
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  auth.active = true;
  auth.session = { accessToken: 'token-1', binding: null };
  auth.authSessionGeneration = 1;
  auth.authSessionEpoch = 1;
  auth.unboundSessionGeneration = 1;
  auth.tenant = 'tenant-1';
  auth.authorizationContext = { tenantId: 'tenant-1' };
  setPage();
  api.savePlatformManualRuntimeRateCard.mockResolvedValue({});
  api.createPlatformByokProviderDraft.mockResolvedValue(provider({ state: 'Draft', revision: 1, is_active: false }));
  api.setPlatformByokProviderCredential.mockResolvedValue({
    operationId: 'op', status: 'published', scope: 'platform', projectId: null,
    providerId: 'provider-1', providerRevision: 1, credentialRevision: 1,
    credentialVersion: 'secret-version', configurationHash: 'hash', providerState: 'Available',
  });
  api.activatePlatformByokProvider.mockResolvedValue({
    mode: 'active', activeProviderId: 'provider-1', settingsRevision: 10,
  });
  api.deactivatePlatformByokProvider.mockResolvedValue({
    mode: 'explicitHosted', activeProviderId: null, settingsRevision: 10,
  });
});

describe('PlatformModelProviders', () => {
  it('shows provider, key, and default states separately and gates untouched switching', async () => {
    setPage({ switchMode: 'untouched', settingsRevision: 0, activeProviderId: null });
    render(<PlatformModelProviders />);
    expect(await screen.findByText('Primary')).toBeTruthy();
    expect(screen.getByText('Available')).toBeTruthy();
    expect(screen.getByText('Key saved')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Use for new runs' })).toHaveProperty('disabled', true);
    expect(screen.getByText(/Initialize platform model settings/)).toBeTruthy();
  });

  it('saves exact manual prices, metadata, then the first write-only key', async () => {
    api.savePlatformManualRuntimeRateCard
      .mockRejectedValueOnce(new GatewayError(409, { code: 'rate_card_conflict' }, 'Conflict'))
      .mockResolvedValueOnce({});
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'New provider' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Save manual prices'));
    fireEvent.change(screen.getByLabelText('Input price per 1M tokens (USD)'), { target: { value: '0.123456789123456789' } });
    fireEvent.change(screen.getByLabelText('Output price per 1M tokens (USD)'), { target: { value: '1.000000000000000001' } });
    fireEvent.change(screen.getByLabelText('Price source URL'), { target: { value: 'https://prices.example.test/model-2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    await waitFor(() => expect(api.setPlatformByokProviderCredential).toHaveBeenCalledOnce());
    const rawPriceBody = api.savePlatformManualRuntimeRateCard.mock.calls[0][3] as string;
    expect(rawPriceBody).toContain(':0.123456789123456789');
    expect(rawPriceBody).toContain(':1.000000000000000001');
    expect(JSON.parse(rawPriceBody).provenance.retrievedOn).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(api.savePlatformManualRuntimeRateCard.mock.calls.map((call) => call[2]))
      .toEqual(['1', '2']);
    expect(api.savePlatformManualRuntimeRateCard.mock.invocationCallOrder[0])
      .toBeLessThan(api.createPlatformByokProviderDraft.mock.invocationCallOrder[0]);
    expect(api.createPlatformByokProviderDraft.mock.invocationCallOrder[0])
      .toBeLessThan(api.setPlatformByokProviderCredential.mock.invocationCallOrder[0]);
    expect(api.setPlatformByokProviderCredential.mock.calls[0][2]).toMatchObject({
      expectedProviderRevision: 1,
      expectedCredentialRevision: 0,
      apiKey: 'secret-key',
    });
  });

  it('replays pending publication with its original operation and revisions', async () => {
    api.setPlatformByokProviderCredential
      .mockRejectedValueOnce(new GatewayError(503, {
        code: 'credential_publication_pending',
        credentialRevision: 2,
      }, 'Pending'))
      .mockResolvedValueOnce({
        operationId: 'op', status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-1', providerRevision: 1, credentialRevision: 2,
        credentialVersion: 'version', configurationHash: 'hash', providerState: 'Available',
      });
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Pending' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));
    expect(await screen.findByRole('button', { name: 'Retry publication' })).toBeTruthy();

    const first = api.setPlatformByokProviderCredential.mock.calls[0][2];
    fireEvent.click(screen.getByRole('button', { name: 'Retry publication' }));
    await waitFor(() => expect(api.setPlatformByokProviderCredential).toHaveBeenCalledTimes(2));
    expect(api.setPlatformByokProviderCredential.mock.calls[1][2]).toEqual({
      operationId: first.operationId,
      expectedProviderRevision: first.expectedProviderRevision,
      expectedCredentialRevision: 0,
      recheckExistingVersion: true,
    });
  });

  it('rejects a manual rate that System.Decimal would round', async () => {
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Rounded' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Save manual prices'));
    fireEvent.change(screen.getByLabelText('Input price per 1M tokens (USD)'),
      { target: { value: '1.00000000000000000000000000001' } });
    fireEvent.change(screen.getByLabelText('Output price per 1M tokens (USD)'),
      { target: { value: '1.000000000000000001' } });
    fireEvent.change(screen.getByLabelText('Price source URL'),
      { target: { value: 'https://prices.example.test/model-2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    expect(await screen.findByText(/Enter positive exact input and output rates/)).toBeTruthy();
    expect(api.savePlatformManualRuntimeRateCard).not.toHaveBeenCalled();
  });

  it('rechecks a saved Draft key after pricing is corrected without another key upload', async () => {
    const savedDraft = provider({
      id: 'provider-2', name: 'Saved Draft', state: 'Draft', revision: 1,
      is_active: false, has_api_key: true, hasCredential: true,
    });
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce({
        switchMode: 'active', activeProviderId: 'provider-1', settingsRevision: 9,
        providers: [provider()], nextAfterProviderId: null,
      })
      .mockResolvedValue({
        switchMode: 'active', activeProviderId: 'provider-1', settingsRevision: 9,
        providers: [{ ...savedDraft, revision: 2 }], nextAfterProviderId: null,
      });
    api.createPlatformByokProviderDraft.mockResolvedValue(savedDraft);
    api.getPlatformByokProviderDraft.mockResolvedValue(savedDraft);
    api.updatePlatformByokProviderDraft.mockResolvedValue({
      ...savedDraft,
      revision: 2,
      rate_card_id: 'fixed-card',
      rate_card_version: '2',
    });
    api.setPlatformByokProviderCredential
      .mockResolvedValueOnce({
        operationId: 'initial', status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-2', providerRevision: 1, credentialRevision: 1,
        credentialVersion: 'version-1', configurationHash: 'hash-1', providerState: 'Draft',
      })
      .mockResolvedValueOnce({
        operationId: 'recheck', status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-2', providerRevision: 3, credentialRevision: 2,
        credentialVersion: 'version-1', configurationHash: 'hash-2', providerState: 'Available',
      });
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Saved Draft' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'fixed-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
    fireEvent.click(await screen.findByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Recheck saved key' }));

    await waitFor(() => expect(api.setPlatformByokProviderCredential).toHaveBeenCalledTimes(2));
    expect(api.setPlatformByokProviderCredential.mock.calls[1][2]).toMatchObject({
      expectedProviderRevision: 2,
      expectedCredentialRevision: 1,
      recheckExistingVersion: true,
    });
    expect(api.setPlatformByokProviderCredential.mock.calls[1][2]).not.toHaveProperty('apiKey');
    expect(await screen.findByText(/Provider state: Available/)).toBeTruthy();
  });

  it('stops before credential upload when the confirmed session changes', async () => {
    api.createPlatformByokProviderDraft.mockImplementationOnce(async () => {
      auth.active = false;
      return provider({ state: 'Draft', revision: 1, is_active: false });
    });
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Stale' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));
    await waitFor(() => expect(api.createPlatformByokProviderDraft).toHaveBeenCalledOnce());
    expect(api.setPlatformByokProviderCredential).not.toHaveBeenCalled();
  });

  it('discards a metadata result after the confirmed tenant changes', async () => {
    let finish!: (value: ReturnType<typeof provider>) => void;
    api.createPlatformByokProviderDraft.mockImplementationOnce(
      () => new Promise((resolve) => { finish = resolve; }),
    );
    const view = render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Tenant change' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));
    await waitFor(() => expect(api.createPlatformByokProviderDraft).toHaveBeenCalledOnce());

    auth.authorizationContext = { tenantId: 'tenant-2' };
    view.rerender(<PlatformModelProviders />);
    finish(provider({ state: 'Draft', revision: 1, is_active: false }));

    await waitFor(() => expect(screen.queryByText('Tenant change')).toBeNull());
    expect(api.setPlatformByokProviderCredential).not.toHaveBeenCalled();
  });

  it('clears editor and recovery state while tenant confirmation is lost', async () => {
    api.setPlatformByokProviderCredential.mockRejectedValueOnce(new GatewayError(503, {
      code: 'credential_publication_pending',
      credentialRevision: 1,
    }, 'Pending'));
    const view = render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Pending' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));
    expect(await screen.findByRole('button', { name: 'Retry publication' })).toBeTruthy();

    auth.authorizationContext = null;
    auth.tenant = null;
    view.rerender(<PlatformModelProviders />);
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Retry publication' })).toBeNull());
    auth.authorizationContext = { tenantId: 'tenant-2' };
    auth.tenant = 'tenant-2';
    view.rerender(<PlatformModelProviders />);

    await waitFor(() => expect(screen.queryByText('Pending')).toBeNull());
    expect(screen.queryByRole('button', { name: 'Retry publication' })).toBeNull();
  });

  it('shows a quarantined recovery handoff without resending the key', async () => {
    api.setPlatformByokProviderCredential.mockRejectedValueOnce(
      new GatewayError(409, {
        code: 'credential_write_quarantined',
        credentialRevision: 3,
      }, 'Quarantined'));
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Quarantine' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));
    expect(await screen.findByText('Key status: quarantined. Provider state: Draft.')).toBeTruthy();
    expect(screen.getByText(/Recovery handoff:/)).toBeTruthy();
    expect(api.setPlatformByokProviderCredential).toHaveBeenCalledOnce();
  });

  it('sends revision fences when switching and refreshes after a conflict', async () => {
    api.deactivatePlatformByokProvider.mockRejectedValueOnce(
      new GatewayError(409, { code: 'model_provider_draft_conflict' }, 'Conflict'));
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Use Copilot for new runs' }));
    await waitFor(() => expect(api.deactivatePlatformByokProvider).toHaveBeenCalled());
    expect(api.deactivatePlatformByokProvider.mock.calls[0][1]).toEqual({
      expectedSettingsRevision: 9,
    });
    await waitFor(() => expect(api.listPlatformByokProviderDrafts).toHaveBeenCalledTimes(2));
  });

  it('sends the selected Available provider revision when activating a default', async () => {
      setPage({
        switchMode: 'copilot',
        activeProviderId: null,
        settingsRevision: 9,
        providers: [provider({ is_active: false })],
      });
      render(<PlatformModelProviders />);
      await screen.findByText('Primary');
      fireEvent.click(screen.getByRole('button', { name: 'Use for new runs' }));
      await waitFor(() => expect(api.activatePlatformByokProvider).toHaveBeenCalledOnce());
      expect(api.activatePlatformByokProvider.mock.calls[0].slice(1, 3)).toEqual([
        'provider-1',
        { expectedSettingsRevision: 9, expectedProviderRevision: 4 },
      ]);
  });

  it('requires explicit pricing selection and clear confirmation when reopening a Draft', async () => {
      const draft = provider({ state: 'Draft', is_active: false });
      setPage({ activeProviderId: null, providers: [draft] });
      api.getPlatformByokProviderDraft.mockResolvedValue(draft);
      api.updatePlatformByokProviderDraft.mockResolvedValue({
        ...draft,
        revision: 5,
        rate_card_id: null,
        rate_card_version: null,
      });
      render(<PlatformModelProviders />);
      await screen.findByText('Primary');
      fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }));
      expect(await screen.findByText(/Choose whether to save manual prices/)).toBeTruthy();
      fireEvent.click(screen.getByLabelText('Clear the saved rate card'));
      fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
      expect(await screen.findByText(/Confirm that the saved rate-card link/)).toBeTruthy();
      fireEvent.click(screen.getByRole('checkbox', { name: /Clear the saved rate-card link/ }));
      fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
      await waitFor(() => expect(api.updatePlatformByokProviderDraft).toHaveBeenCalledOnce());
      expect(api.updatePlatformByokProviderDraft.mock.calls[0][2]).toMatchObject({
        rate_card_id: null,
        rate_card_version: null,
      });
  });

  it('isolates a 403 to provider controls', async () => {
    api.listPlatformByokProviderDrafts.mockRejectedValue(
      new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    render(<PlatformModelProviders />);
    expect(await screen.findByText(/rest of Platform Settings remains available/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add provider' })).toHaveProperty('disabled', true);
  });
});
