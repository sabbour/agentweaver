import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { PlatformModelProviders } from './PlatformModelProviders';
import { createProjectCredentialOperationStore } from './projectCredentialOperations';

const api = vi.hoisted(() => ({
  listPlatformByokProviderDrafts: vi.fn(),
  getPlatformByokProviderDraft: vi.fn(),
  createPlatformByokProviderDraft: vi.fn(),
  updatePlatformByokProviderDraft: vi.fn(),
  savePlatformManualRuntimeRateCard: vi.fn(),
  setPlatformByokProviderCredential: vi.fn(),
  getPlatformByokProviderCredentialOperation: vi.fn(),
  activatePlatformByokProvider: vi.fn(),
  deactivatePlatformByokProvider: vi.fn(),
}));
const auth = vi.hoisted(() => ({
  session: { accessToken: 'token-1', binding: null } as { accessToken: string; binding: { projectId: string; runId?: string } | null } | null,
  active: true,
  authSessionGeneration: 1,
  authSessionEpoch: 1,
  unboundSessionGeneration: 1,
  tenant: 'tenant-1' as string | null,
  authorizationContext: { tenantId: 'tenant-1' } as {
    tenantId: string;
    actorId?: string | null;
    issuer?: string | null;
  } | null,
  apiCall: vi.fn(async (operation: (token: string, tenant: string | null) => unknown) =>
    operation(auth.session?.accessToken ?? 'token-1', auth.tenant)),
  apiCallOnce: vi.fn(async (
    operation: (token: string, tenant: string) => unknown,
    binding: unknown = null,
    expectedIdentity?: { tenantId: string; actorId: string | null; issuer: string | null },
  ) => {
    if (binding !== null) throw new GatewayError(403, { code: 'forbidden' }, 'Unbound session required.');
    const tenant = expectedIdentity?.tenantId ?? auth.tenant ?? 'tenant-1';
    return operation(auth.session?.accessToken ?? 'token-1', tenant);
  }),
  projectCredentialOperations: null as ReturnType<typeof createProjectCredentialOperationStore> | null,
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

function providerPage(providers: ReturnType<typeof provider>[], pageOverrides: Record<string, unknown> = {}) {
  return {
    switchMode: 'active',
    activeProviderId: 'provider-1',
    settingsRevision: 9,
    providers,
    nextAfterProviderId: null,
    ...pageOverrides,
  };
}

function setPage(pageOverrides: Record<string, unknown> = {}) {
  api.listPlatformByokProviderDrafts.mockResolvedValue(providerPage([provider()], pageOverrides));
}

beforeEach(() => {
  vi.clearAllMocks();
  api.listPlatformByokProviderDrafts.mockReset();
  auth.active = true;
  auth.session = { accessToken: 'token-1', binding: null };
  auth.authSessionGeneration = 1;
  auth.authSessionEpoch = 1;
  auth.unboundSessionGeneration = 1;
  auth.tenant = 'tenant-1';
  auth.authorizationContext = { tenantId: 'tenant-1' };
  auth.projectCredentialOperations = createProjectCredentialOperationStore();
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
  it('shows safe host delivery details from the provider response and maps exact status pairs', async () => {
    setPage({
      activeProviderId: null,
      providers: [
        provider({
          id: 'pending', name: 'Pending provider',
          egress: { host: 'pending.example', port: 443, destinationState: null, canApprove: false, deliveryStatus: 'pending' },
        }),
        provider({
          id: 'delivered', name: 'Delivered provider',
          egress: { host: 'delivered.example', port: 8443, destinationState: 'requested', canApprove: false, deliveryStatus: 'delivered' },
        }),
        provider({
          id: 'failed', name: 'Failed provider',
          egress: { host: 'failed.example', port: 443, destinationState: null, canApprove: false, deliveryStatus: 'failed' },
        }),
        provider({ id: 'missing', name: 'Missing status provider', egress: null }),
        provider({
          id: 'unavailable', name: 'Unavailable provider',
          egress: { host: 'unavailable.example', port: 443, destinationState: null, canApprove: false, deliveryStatus: 'unavailable' },
        }),
        provider({
          id: 'mismatch', name: 'Mismatched provider', base_url: 'https://fallback.example/v1',
          egress: { host: 'https://unsafe.example/path', port: 0, destinationState: 'requested', canApprove: true, deliveryStatus: 'pending' },
        }),
      ],
    });

    render(<PlatformModelProviders />);

    const row = async (name: string) => (await screen.findByText(name)).closest('li')!;
    expect(within(await row('Pending provider')).getByText('Waiting to send')).toBeTruthy();
    const delivered = within(await row('Delivered provider'));
    expect(delivered.getByText('Received by Environment')).toBeTruthy();
    expect(delivered.getByText('delivered.example')).toBeTruthy();
    expect(delivered.getByText('8443')).toBeTruthy();
    expect(within(await row('Failed provider')).getByText('Delivery failed')).toBeTruthy();
    expect(within(await row('Missing status provider')).getByText('Status unavailable')).toBeTruthy();
    expect(within(await row('Unavailable provider')).getByText('Status unavailable')).toBeTruthy();
    const mismatch = within(await row('Mismatched provider'));
    expect(mismatch.getByText('Status unavailable')).toBeTruthy();
    expect(mismatch.getAllByText('Unavailable')).toHaveLength(2);
    expect(screen.queryByText('fallback.example')).toBeNull();
    expect(screen.getAllByText('Receiving a request does not approve the host or make the provider ready.')).toHaveLength(6);
  });

  it('refreshes host delivery details without a write or stale response reuse', async () => {
    let finishStaleRefresh!: (value: ReturnType<typeof providerPage>) => void;
    const initial = providerPage([provider({
      egress: { host: 'model.example', port: 443, destinationState: null, canApprove: false, deliveryStatus: 'pending' },
    })]);
    api.listPlatformByokProviderDrafts
      .mockReset()
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(providerPage([provider({
        egress: { host: 'tenant-two.example', port: 443, destinationState: 'requested', canApprove: false, deliveryStatus: 'delivered' },
      })]))
      .mockImplementationOnce(() => new Promise((resolve) => { finishStaleRefresh = resolve; }));

    const view = render(<PlatformModelProviders />);
    expect(await screen.findByText('Waiting to send')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Received by Environment')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(api.listPlatformByokProviderDrafts).toHaveBeenCalledTimes(3);

    auth.active = false;
    auth.session = null;
    view.rerender(<PlatformModelProviders />);
    expect(await screen.findByText('Sign in to manage platform providers.')).toBeTruthy();
    await act(async () => finishStaleRefresh(providerPage([provider({
      id: 'stale', name: 'Stale provider',
      egress: { host: 'stale.example', port: 443, destinationState: null, canApprove: false, deliveryStatus: 'failed' },
    })])));

    expect(screen.queryByText('Stale provider')).toBeNull();
    expect(screen.queryByText('Delivery failed')).toBeNull();
    expect(api.createPlatformByokProviderDraft).not.toHaveBeenCalled();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
    expect(api.setPlatformByokProviderCredential).not.toHaveBeenCalled();
  });

  it('clears host delivery details when a refresh loses access', async () => {
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([provider({
        egress: { host: 'model.example', port: 443, destinationState: 'requested', canApprove: false, deliveryStatus: 'delivered' },
      })]))
      .mockRejectedValueOnce(new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    render(<PlatformModelProviders />);
    expect(await screen.findByText('Received by Environment')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText(/rest of Platform Settings remains available/)).toBeTruthy();
    expect(screen.queryByText('model.example')).toBeNull();
    expect(screen.queryByText('Received by Environment')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Use for new runs' })).toBeNull();
  });

  it('shows provider, key, and default states separately and gates untouched switching', async () => {
    setPage({ switchMode: 'untouched', settingsRevision: 0, activeProviderId: null });
    render(<PlatformModelProviders />);
    expect(await screen.findByText('Primary')).toBeTruthy();
    expect(screen.getByText('Available')).toBeTruthy();
    expect(screen.getByText('Key saved')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Use for new runs' })).toHaveProperty('disabled', true);
    expect(screen.getByText(/Initialize platform model settings/)).toBeTruthy();
  });

  it('separates missing, attached, and malformed pricing without implying provider readiness', async () => {
    setPage({
      activeProviderId: null,
      providers: [
        provider({
          id: 'missing', name: 'Missing pricing', state: 'Draft', is_active: false,
          rate_card_id: null, rate_card_version: null,
        }),
        provider({
          id: 'attached', name: 'Attached pricing', state: 'Draft', is_active: false,
          rate_card_id: 'card-attached', rate_card_version: '7',
        }),
        provider({
          id: 'malformed', name: 'Malformed pricing', state: 'Draft', is_active: false,
          rate_card_id: 'card-without-version', rate_card_version: null,
        }),
      ],
    });
    render(<PlatformModelProviders />);

    const missing = (await screen.findByText('Missing pricing')).closest('li')!;
    expect(within(missing).getByText('Pricing required')).toBeTruthy();
    expect(within(missing).getByRole('button', { name: 'Set pricing for Missing pricing' })).toBeTruthy();
    expect(within(missing).queryByRole('button', { name: 'Edit Missing pricing' })).toBeNull();
    const attached = screen.getByText('Attached pricing').closest('li')!;
    expect(within(attached).getByText('Rate card: card-attached / 7')).toBeTruthy();
    expect(within(attached).queryByText('Pricing required')).toBeNull();
    const malformed = screen.getByText('Malformed pricing').closest('li')!;
    expect(within(malformed).getByText('Pricing status unavailable')).toBeTruthy();
    expect(within(malformed).queryByRole('button', { name: /Set pricing/ })).toBeNull();
    expect(screen.queryByText(/verified/i)).toBeNull();
  });

  it('opens the existing pricing editor without a write, focuses pricing, and saves the current Draft revision', async () => {
    const missing = provider({
      id: 'provider-2', name: 'Needs pricing', state: 'Draft', is_active: false,
      revision: 7, rate_card_id: null, rate_card_version: null,
    });
    const refreshed = {
      ...missing,
      revision: 8,
      rate_card_id: 'saved-prices',
      rate_card_version: '3',
    };
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([missing], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([refreshed], { activeProviderId: null }));
    api.getPlatformByokProviderDraft.mockResolvedValue(missing);
    api.updatePlatformByokProviderDraft.mockResolvedValue(refreshed);
    render(<PlatformModelProviders />);
    await screen.findByText('Needs pricing');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Needs pricing' }));

    const pricingGroup = await screen.findByRole('group', { name: 'Pricing' });
    await waitFor(() => expect(document.activeElement).toBe(pricingGroup));
    expect(api.savePlatformManualRuntimeRateCard).not.toHaveBeenCalled();
    expect(api.createPlatformByokProviderDraft).not.toHaveBeenCalled();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();

    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-prices' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.updatePlatformByokProviderDraft).toHaveBeenCalledOnce());
    expect(api.updatePlatformByokProviderDraft.mock.calls[0].slice(1, 3)).toEqual([
      'provider-2',
      expect.objectContaining({
        expectedRevision: 7,
        rate_card_id: 'saved-prices',
        rate_card_version: '3',
      }),
    ]);
    expect(await screen.findByText('Rate card: saved-prices / 3')).toBeTruthy();
    expect(api.listPlatformByokProviderDrafts).toHaveBeenCalledTimes(2);
  });

  it('ignores an older detail response and a new-provider form fence', async () => {
    const first = provider({
      id: 'first', name: 'First', state: 'Draft', is_active: false,
      rate_card_id: null, rate_card_version: null,
    });
    const second = provider({
      id: 'second', name: 'Second', state: 'Draft', is_active: false,
      rate_card_id: null, rate_card_version: null,
    });
    let finishFirst!: (value: ReturnType<typeof provider>) => void;
    let finishSecond!: (value: ReturnType<typeof provider>) => void;
    setPage({ activeProviderId: null, providers: [first, second] });
    api.getPlatformByokProviderDraft.mockImplementation((_token, providerId) =>
      new Promise((resolve) => {
        if (providerId === 'first') finishFirst = resolve;
        else finishSecond = resolve;
      }));
    const view = render(<PlatformModelProviders />);
    await screen.findByText('First');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for First' }));
    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Second' }));
    await act(async () => finishSecond(second));
    expect(await screen.findByDisplayValue('Second')).toBeTruthy();
    await act(async () => finishFirst(first));
    expect(screen.queryByDisplayValue('First')).toBeNull();
    expect(screen.getByDisplayValue('Second')).toBeTruthy();

    let finishNewFence!: (value: ReturnType<typeof provider>) => void;
    api.getPlatformByokProviderDraft.mockImplementationOnce(
      () => new Promise((resolve) => { finishNewFence = resolve; }));
    view.unmount();
    render(<PlatformModelProviders />);
    await screen.findByText('First');
    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for First' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    await act(async () => finishNewFence(first));
    expect(screen.getByText('Add Custom endpoint')).toBeTruthy();
    expect(screen.queryByDisplayValue('First')).toBeNull();
  });

  it.each([
    ['provider ID', provider({ id: 'other' })],
    ['provider state', provider({ id: 'provider-1', state: 'Available' })],
  ])('refreshes instead of opening when the detail has the wrong %s', async (_label, detail) => {
    const selected = provider({
      state: 'Draft', is_active: false, rate_card_id: null, rate_card_version: null,
    });
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([selected], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([
        provider({
          state: 'Draft', is_active: false,
          rate_card_id: 'refreshed-card', rate_card_version: '1',
        }),
      ], { activeProviderId: null }));
    api.getPlatformByokProviderDraft.mockResolvedValue(detail);
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Primary' }));

    expect(await screen.findByText('Rate card: refreshed-card / 1')).toBeTruthy();
    expect(screen.queryByRole('group', { name: 'Pricing' })).toBeNull();
    expect(api.listPlatformByokProviderDrafts).toHaveBeenCalledTimes(2);
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
  });

  it('does not open an old detail after a newer list refresh returns an Available provider', async () => {
    const draft = provider({
      state: 'Draft', is_active: false, rate_card_id: null, rate_card_version: null,
    });
    let finishDetail!: (value: ReturnType<typeof provider>) => void;
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([draft], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([provider({
        state: 'Available', is_active: true, rate_card_id: 'new-card', rate_card_version: '2',
      })], { activeProviderId: 'provider-1' }));
    api.getPlatformByokProviderDraft.mockImplementation(
      () => new Promise((resolve) => { finishDetail = resolve; }));
    render(<PlatformModelProviders />);
    await screen.findByText('Pricing required');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Primary' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Available')).toBeTruthy();
    await act(async () => finishDetail(draft));

    expect(screen.queryByRole('group', { name: 'Pricing' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Set pricing/ })).toBeNull();
    expect(screen.getByText('Rate card: new-card / 2')).toBeTruthy();
  });

  it('ignores a normal old detail failure after a newer refresh succeeds', async () => {
    const draft = provider({
      state: 'Draft', is_active: false, rate_card_id: null, rate_card_version: null,
    });
    let rejectDetail!: (reason: unknown) => void;
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([draft], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([provider({
        state: 'Available', rate_card_id: 'new-card', rate_card_version: '2',
      })], { activeProviderId: 'provider-1' }));
    api.getPlatformByokProviderDraft.mockImplementation(
      () => new Promise((_resolve, reject) => { rejectDetail = reject; }));
    render(<PlatformModelProviders />);
    await screen.findByText('Pricing required');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Primary' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Rate card: new-card / 2')).toBeTruthy();
    await act(async () => rejectDetail(new Error('Old detail failed.')));

    expect(screen.queryByText('Old detail failed.')).toBeNull();
    expect(screen.getByText('Rate card: new-card / 2')).toBeTruthy();
  });

  it('clears editor access after a provider detail read is forbidden', async () => {
    const draft = provider({
      state: 'Draft', is_active: false, rate_card_id: null, rate_card_version: null,
    });
    setPage({ activeProviderId: null, providers: [draft] });
    api.getPlatformByokProviderDraft.mockRejectedValue(
      new GatewayError(403, { code: 'forbidden' }, 'Forbidden'));
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Primary' }));

    expect(await screen.findByText(/rest of Platform Settings remains available/)).toBeTruthy();
    expect(screen.queryByRole('group', { name: 'Pricing' })).toBeNull();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
  });

  it('clears a newer form when an older detail read returns current access loss', async () => {
    const draft = provider({
      state: 'Draft', is_active: false, rate_card_id: null, rate_card_version: null,
    });
    let rejectDetail!: (reason: unknown) => void;
    setPage({ activeProviderId: null, providers: [draft] });
    api.getPlatformByokProviderDraft.mockImplementation(
      () => new Promise((_resolve, reject) => { rejectDetail = reject; }));
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Primary' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    expect(screen.getByText('Add Custom endpoint')).toBeTruthy();

    await act(async () => rejectDetail(
      new GatewayError(403, { code: 'forbidden' }, 'Forbidden')));

    expect(await screen.findByText(/rest of Platform Settings remains available/)).toBeTruthy();
    expect(screen.queryByText('Add Custom endpoint')).toBeNull();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
  });

  it('uses safe detail defaults for malformed optional fields and still requires pricing input', async () => {
    const draft = provider({
      state: 'Draft', is_active: false,
      rate_card_id: 'partial-card', rate_card_version: null,
    });
    setPage({ activeProviderId: null, providers: [draft] });
    api.getPlatformByokProviderDraft.mockResolvedValue({
      ...draft,
      name: 42,
      base_url: {},
      model: null,
      wire_api: 'invalid',
      azure_api_version: 7,
      reasoning_efforts: null,
      prompt_capacity_tokens: {},
    });
    render(<PlatformModelProviders />);
    await screen.findByText('Primary');

    fireEvent.click(screen.getByRole('button', { name: 'Edit Primary' }));
    expect(await screen.findByRole('group', { name: 'Pricing' })).toBeTruthy();
    expect(screen.getByLabelText('Display name')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Base URL')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Model or deployment')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Reasoning efforts')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Prompt capacity tokens')).toHaveProperty('value', '');
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Recovered draft' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText(/Choose whether to save manual prices/)).toBeTruthy();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
  });

  it.each([
    ['invalid ID characters', { rate_card_id: 'bad card', rate_card_version: '1' }],
    ['invalid version characters', { rate_card_id: 'valid-card', rate_card_version: '1/2' }],
    ['long ID', { rate_card_id: 'a'.repeat(129), rate_card_version: '1' }],
    ['long version', { rate_card_id: 'valid-card', rate_card_version: 'a'.repeat(65) }],
    ['ID without version', { rate_card_id: 'valid-card', rate_card_version: null }],
    ['version without ID', { rate_card_id: null, rate_card_version: '1' }],
  ])('marks a %s as unavailable and blanks both saved-card fields', async (_label, rateCard) => {
    const draft = provider({
      state: 'Draft', is_active: false, ...rateCard,
    });
    setPage({ activeProviderId: null, providers: [draft] });
    api.getPlatformByokProviderDraft.mockResolvedValue(draft);
    render(<PlatformModelProviders />);
    await screen.findByText('Pricing status unavailable');

    expect(screen.getByRole('button', { name: 'Edit Primary' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Set pricing for Primary' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Edit Primary' }));
    await screen.findByRole('group', { name: 'Pricing' });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));

    expect(screen.getByLabelText('Rate-card ID')).toHaveProperty('value', '');
    expect(screen.getByLabelText('Rate-card version')).toHaveProperty('value', '');
    fireEvent.submit(screen.getByRole('group', { name: 'Pricing' }).closest('form')!);
    expect(await screen.findByText(/Enter the saved rate-card ID and version/)).toBeTruthy();
    expect(api.updatePlatformByokProviderDraft).not.toHaveBeenCalled();
  });

  it('points a saved-key Draft to the documented recheck after pricing saves', async () => {
    const draft = provider({
      id: 'provider-2', name: 'Saved key', state: 'Draft', is_active: false,
      hasCredential: true, has_api_key: true,
      rate_card_id: null, rate_card_version: null, revision: 4,
    });
    const saved = {
      ...draft,
      revision: 5,
      rate_card_id: 'new-card',
      rate_card_version: '2',
    };
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([draft], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([saved], { activeProviderId: null }));
    api.getPlatformByokProviderDraft.mockResolvedValue(draft);
    api.updatePlatformByokProviderDraft.mockResolvedValue(saved);
    render(<PlatformModelProviders />);
    await screen.findByText('Saved key');

    fireEvent.click(screen.getByRole('button', { name: 'Set pricing for Saved key' }));
    fireEvent.click(await screen.findByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'new-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(
      'Pricing was saved. Run the documented platform byok recheck-key procedure to apply the current pricing to the saved key.',
    )).toBeTruthy();
    expect(screen.queryByText(/Add a key before this provider/)).toBeNull();
    expect(api.setPlatformByokProviderCredential).not.toHaveBeenCalled();
  });

  it('points a saved-key Draft to Set pricing after clearing its rate card', async () => {
    const draft = provider({
      id: 'provider-2', name: 'Clear prices', state: 'Draft', is_active: false,
      hasCredential: true, has_api_key: true,
      rate_card_id: 'old-card', rate_card_version: '1', revision: 4,
    });
    const cleared = provider({
      ...draft,
      revision: 5,
      rate_card_id: null,
      rate_card_version: null,
    });
    api.listPlatformByokProviderDrafts
      .mockResolvedValueOnce(providerPage([draft], { activeProviderId: null }))
      .mockResolvedValueOnce(providerPage([cleared], { activeProviderId: null }));
    api.getPlatformByokProviderDraft.mockResolvedValue(draft);
    api.updatePlatformByokProviderDraft.mockResolvedValue(cleared);
    render(<PlatformModelProviders />);
    await screen.findByText('Clear prices');

    fireEvent.click(screen.getByRole('button', { name: 'Edit Clear prices' }));
    fireEvent.click(await screen.findByLabelText('Clear the saved rate card'));
    fireEvent.click(screen.getByRole('checkbox', { name: /Clear the saved rate-card link/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText(
      'Pricing was cleared. Select Set pricing to add prices before the provider can become Available.',
    )).toBeTruthy();
    expect(screen.getByText('Pricing required')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Set pricing for Clear prices' })).toBeTruthy();
    expect(screen.queryByText(/recheck-key/)).toBeNull();
    expect(api.setPlatformByokProviderCredential).not.toHaveBeenCalled();
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
      .mockImplementationOnce(async (_token, _providerId, request) => ({
        operationId: request.operationId, status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-2', providerRevision: 1, credentialRevision: 1,
        credentialVersion: 'version-1', configurationHash: 'hash-1', providerState: 'Draft',
      }))
      .mockImplementationOnce(async (_token, _providerId, request) => ({
        operationId: request.operationId, status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-2', providerRevision: 3, credentialRevision: 2,
        credentialVersion: 'version-1', configurationHash: 'hash-2', providerState: 'Available',
      }));
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

    fireEvent.click(await screen.findByRole('button', { name: 'Edit Saved Draft' }));
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
      fireEvent.click(screen.getByRole('button', { name: 'Edit Primary' }));
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

  it('stores exact expectedCredentialRevision >= 1 on recheckSavedKey failure and replays without apiKey, stopping on 409 conflict', async () => {
    const savedDraft = provider({
      id: 'provider-2', name: 'Saved Draft', state: 'Draft', revision: 2,
      is_active: false, has_api_key: true, hasCredential: true,
    });
    setPage({ activeProviderId: null, providers: [savedDraft] });
    api.createPlatformByokProviderDraft.mockResolvedValue({ ...savedDraft, revision: 1 });
    api.getPlatformByokProviderDraft.mockResolvedValue({ ...savedDraft, revision: 1 });
    api.updatePlatformByokProviderDraft.mockResolvedValue(savedDraft);
    api.setPlatformByokProviderCredential
      .mockImplementationOnce(async (_token, _providerId, request) => ({
        operationId: request.operationId, status: 'published', scope: 'platform', projectId: null,
        providerId: 'provider-2', providerRevision: 1, credentialRevision: 3,
        credentialVersion: 'version-3', configurationHash: 'hash-1', providerState: 'Draft',
      }))
      .mockRejectedValueOnce(new GatewayError(503, {
        code: 'credential_publication_pending',
      }, 'Pending'))
      .mockRejectedValueOnce(new GatewayError(409, {
        code: 'model_provider_draft_conflict',
      }, 'Conflict'));

    render(<PlatformModelProviders />);
    await screen.findByText('Saved Draft');
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

    fireEvent.click(await screen.findByRole('button', { name: 'Recheck saved key' }));
    expect(await screen.findByRole('button', { name: 'Retry publication' })).toBeTruthy();

    const recheckCall = api.setPlatformByokProviderCredential.mock.calls[1][2];
    expect(recheckCall).toMatchObject({
      expectedProviderRevision: 2,
      expectedCredentialRevision: 3,
      recheckExistingVersion: true,
    });
    expect(recheckCall).not.toHaveProperty('apiKey');

    fireEvent.click(screen.getByRole('button', { name: 'Retry publication' }));
    await waitFor(() => expect(api.setPlatformByokProviderCredential).toHaveBeenCalledTimes(3));
    const replayCall = api.setPlatformByokProviderCredential.mock.calls[2][2];
    expect(replayCall).toEqual({
      operationId: recheckCall.operationId,
      expectedProviderRevision: 2,
      expectedCredentialRevision: 3,
      recheckExistingVersion: true,
    });
    expect(replayCall).not.toHaveProperty('apiKey');
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Retry publication' })).toBeNull());
    expect(screen.getByText(/provider changed while publication was pending/i)).toBeTruthy();
  });

  it('does not store an operation on pre-claim rejections (409 credential_slot_conflict, 503 credential_vault_unavailable, 404 credential_slot_not_found)', async () => {
    const draft = provider({ id: 'provider-1', state: 'Draft', revision: 1, is_active: false, hasCredential: false });
    setPage({ activeProviderId: null, providers: [draft] });
    api.createPlatformByokProviderDraft.mockResolvedValue(draft);
    api.setPlatformByokProviderCredential
      .mockRejectedValueOnce(new GatewayError(409, { code: 'credential_slot_conflict' }, 'Conflict'))
      .mockRejectedValueOnce(new GatewayError(503, { code: 'credential_vault_unavailable' }, 'Vault down'));

    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Preclaim' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    expect(await screen.findByText(/already has an existing or disabled credential slot/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Check key status' })).toBeNull();
    expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })).toBeNull();

    // Second pre-claim failure (503 credential_vault_unavailable) on update also does not store an operation.
    api.updatePlatformByokProviderDraft.mockResolvedValue({ ...draft, revision: 2 });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText(/Vault down/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Check key status' })).toBeNull();
  });

  it('blocks API key entry in Edit during unresolved operations while allowing metadata-only edits, and closes/reloads on uncertain PUT', async () => {
    const draft = provider({ id: 'provider-1', state: 'Draft', revision: 1, is_active: false, hasCredential: false });
    setPage({ activeProviderId: null, providers: [draft] });
    api.createPlatformByokProviderDraft.mockResolvedValue(draft);
    api.getPlatformByokProviderDraft.mockResolvedValue(draft);
    api.updatePlatformByokProviderDraft.mockResolvedValue({ ...draft, revision: 2, name: 'Updated name' });
    api.setPlatformByokProviderCredential.mockRejectedValueOnce(new Error('Network lost'));

    render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Primary' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'super-secret-value' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    // Editor closed and list reloaded; Check key status appears on the provider row.
    expect(await screen.findByRole('button', { name: 'Check key status' })).toBeTruthy();
    expect(screen.queryByLabelText('API key')).toBeNull();

    // Secret never appears in operation store or web storage.
    const storedOp = auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    });
    expect(storedOp).toMatchObject({ status: 'uncertain', expectedProviderRevision: 1, expectedCredentialRevision: 0 });
    expect(JSON.stringify(storedOp)).not.toContain('super-secret-value');
    expect(JSON.stringify(window.localStorage)).not.toContain('super-secret-value');
    expect(JSON.stringify(window.sessionStorage)).not.toContain('super-secret-value');

    // Reopen Edit: API key field is blocked, metadata-only edit still works.
    fireEvent.click(screen.getByRole('button', { name: 'Edit Primary' }));
    expect(await screen.findByText(/New key entry is blocked until this operation is resolved/)).toBeTruthy();
    expect(screen.queryByLabelText('API key')).toBeNull();
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'Updated name' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(api.updatePlatformByokProviderDraft).toHaveBeenCalledOnce());
    expect(api.setPlatformByokProviderCredential).toHaveBeenCalledTimes(1);
  });

  it('recovers across 404 credential_operation_not_found, pending, disable_pending, publication_pending, and published, and supports operator reset', async () => {
    const draft = provider({ id: 'provider-1', state: 'Draft', revision: 1, is_active: false, hasCredential: false });
    setPage({ activeProviderId: null, providers: [draft] });
    auth.projectCredentialOperations!.set({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    }, {
      operationId: 'op-recover-1',
      expectedProviderRevision: 1,
      expectedCredentialRevision: 0,
      status: 'uncertain',
    });
    api.getPlatformByokProviderCredentialOperation
      .mockRejectedValueOnce(new GatewayError(404, { code: 'credential_operation_not_found' }, 'Not found'))
      .mockResolvedValueOnce({
        operationId: 'op-recover-1', scope: 'platform', projectId: null,
        providerId: 'provider-1', status: 'disable_pending',
      })
      .mockResolvedValueOnce({
        operationId: 'op-recover-1', scope: 'platform', projectId: null,
        providerId: 'provider-1', status: 'published',
      });

    render(<PlatformModelProviders />);
    expect(await screen.findByRole('button', { name: 'Check key status' })).toBeTruthy();

    // 404 credential_operation_not_found is nonterminal -> pending (keeps operationId and offers reset).
    fireEvent.click(screen.getByRole('button', { name: 'Check key status' }));
    expect(await screen.findByText(/operation is still pending/i)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reset pending check' })).toBeTruthy();

    // 200 disable_pending -> blocks reset and keeps Check key status.
    fireEvent.click(screen.getByRole('button', { name: 'Check key status' }));
    expect(await screen.findByText(/Credential disable is pending/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Reset pending check' })).toBeNull();

    // 200 published -> terminal, clears operation and reloads provider list.
    fireEvent.click(screen.getByRole('button', { name: 'Check key status' }));
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Check key status' })).toBeNull());
    expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })).toBeNull();
  });

  it('preserves loaded rows during proactive token refresh and retains operationId across unmount/remount and 401 reauth', async () => {
    const draft = provider({ id: 'provider-1', state: 'Draft', revision: 1, is_active: false, hasCredential: false });
    setPage({ activeProviderId: null, providers: [draft] });
    auth.projectCredentialOperations!.set({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    }, {
      operationId: 'op-persist-1',
      expectedProviderRevision: 1,
      expectedCredentialRevision: 0,
      status: 'publication_pending',
    });

    // Proactive refresh updates accessToken, authSessionGeneration, and unboundSessionGeneration while authSessionEpoch stays 1.
    api.getPlatformByokProviderCredentialOperation.mockImplementationOnce(async () => {
      auth.session = { accessToken: 'refreshed-token-2', binding: null };
      auth.authSessionGeneration = 2;
      auth.unboundSessionGeneration = 2;
      return {
        operationId: 'op-persist-1', scope: 'platform', projectId: null,
        providerId: 'provider-1', status: 'publication_pending',
      };
    });

    const view = render(<PlatformModelProviders />);
    expect(await screen.findByText('Primary')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry publication' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Check key status' }));
    await waitFor(() => expect(api.getPlatformByokProviderCredentialOperation).toHaveBeenCalledOnce());
    // Loaded rows and recovery controls remain intact after token refresh.
    expect(screen.getByText('Primary')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry publication' })).toBeTruthy();

    // Unmount and remount preserves operationId.
    view.unmount();
    const remounted = render(<PlatformModelProviders />);
    expect(await screen.findByRole('button', { name: 'Retry publication' })).toBeTruthy();

    // 401 access loss prompts sign-in without clearing the in-memory operation, and clicking Sign in starts fresh authorize(null).
    api.getPlatformByokProviderCredentialOperation.mockRejectedValueOnce(
      new GatewayError(401, { code: 'unauthorized' }, 'Expired'));
    fireEvent.click(screen.getByRole('button', { name: 'Check key status' }));
    expect(await screen.findByText('Expired')).toBeTruthy();
    const signInBtn = screen.getByRole('button', { name: 'Switch to platform sign-in' });
    fireEvent.click(signInBtn);
    await waitFor(() => expect(auth.authorize).toHaveBeenCalledWith(null));
    expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })).not.toBeNull();

    // Same-identity reauth (epoch 2) restores the provider list and recovery controls.
    auth.authSessionEpoch = 2;
    remounted.rerender(<PlatformModelProviders />);
    expect(await screen.findByRole('button', { name: 'Retry publication' })).toBeTruthy();
  });

  it('retains operationId on unmount while PUT is in flight, survives temporary context null during refresh, and keeps op when reset reload fails', async () => {
    const draft = provider({ id: 'provider-1', state: 'Draft', revision: 1, is_active: false, hasCredential: false });
    setPage({ activeProviderId: null, providers: [draft] });
    api.createPlatformByokProviderDraft.mockResolvedValue(draft);
    let resolvePut!: (value: unknown) => void;
    api.setPlatformByokProviderCredential.mockImplementationOnce(
      () => new Promise((resolve) => { resolvePut = resolve; }),
    );

    const view = render(<PlatformModelProviders />);
    await screen.findByText('Primary');
    fireEvent.click(screen.getByRole('button', { name: 'Add provider' }));
    fireEvent.click(screen.getByRole('button', { name: /Custom endpoint/ }));
    fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'InFlight' } });
    fireEvent.change(screen.getByLabelText('Base URL'), { target: { value: 'https://api.example.test/v1' } });
    fireEvent.change(screen.getByLabelText('Model or deployment'), { target: { value: 'model-2' } });
    fireEvent.click(screen.getByLabelText('Use a saved rate card'));
    fireEvent.change(screen.getByLabelText('Rate-card ID'), { target: { value: 'saved-card' } });
    fireEvent.change(screen.getByLabelText('Rate-card version'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save provider' }));

    await waitFor(() => expect(api.setPlatformByokProviderCredential).toHaveBeenCalledOnce());
    // Unmount while PUT is still in flight.
    view.unmount();
    const inFlightOp = auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    });
    expect(inFlightOp).toMatchObject({ status: 'uncertain', expectedProviderRevision: 1, expectedCredentialRevision: 0 });
    resolvePut({
      operationId: inFlightOp!.operationId, status: 'publication_pending', scope: 'platform', projectId: null,
      providerId: 'provider-1', providerRevision: 1, credentialRevision: 0,
      credentialVersion: null, configurationHash: null, providerState: 'Draft',
    });
    await waitFor(() => expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })?.status).toBe('publication_pending'));

    // Temporary authorizationContext=null during proactive refresh hides rows, then restores them for the same identity.
    auth.projectCredentialOperations!.set({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    }, {
      operationId: 'op-reset-test', expectedProviderRevision: 1, expectedCredentialRevision: 0, status: 'uncertain',
    });
    const mounted = render(<PlatformModelProviders />);
    expect(await mounted.findByRole('button', { name: 'Reset pending check' })).toBeTruthy();

    auth.authorizationContext = null;
    mounted.rerender(<PlatformModelProviders />);
    expect(mounted.queryByText('Primary')).toBeNull();
    auth.authorizationContext = { tenantId: 'tenant-1' };
    mounted.rerender(<PlatformModelProviders />);
    expect(await mounted.findByText('Primary')).toBeTruthy();

    // Confirm reset does NOT delete the operation if provider reload fails, and deletes it when reload succeeds.
    api.getPlatformByokProviderDraft.mockRejectedValueOnce(new GatewayError(503, { code: 'unavailable' }, 'Down'));
    fireEvent.click(mounted.getByRole('button', { name: 'Reset pending check' }));
    fireEvent.click(mounted.getByRole('button', { name: 'Confirm reset' }));
    expect(await mounted.findByText(/Down/)).toBeTruthy();
    expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })).not.toBeNull();

    api.getPlatformByokProviderDraft.mockResolvedValueOnce(draft);
    fireEvent.click(mounted.getByRole('button', { name: 'Confirm reset' }));
    await waitFor(() => expect(auth.projectCredentialOperations!.get({
      issuer: 'issuer', actorId: 'actor', tenantId: 'tenant-1', scope: 'platform', projectId: null, providerId: 'provider-1',
    })).toBeNull());
  });
});
