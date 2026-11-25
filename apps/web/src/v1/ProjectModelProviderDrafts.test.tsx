import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';

const mocks = vi.hoisted(() => ({
  apiCall: vi.fn(),
  authorize: vi.fn(),
  ensureSession: vi.fn(),
  resolveAuthorizationContext: vi.fn(),
  list: vi.fn(),
  read: vi.fn(),
  create: vi.fn(),
  update: vi.fn(),
  auth: {
    apiCall: vi.fn(),
    authorize: vi.fn(),
    ensureSession: vi.fn(),
    resolveAuthorizationContext: vi.fn(),
    session: { binding: { projectId: 'project-1' } },
    authSessionGeneration: 0,
    authorizationContext: { tenantId: 'tenant-1' },
    authorizationContextLoading: false,
    busy: false,
    configurationError: null,
  },
}));

vi.mock('./AuthContext', () => ({ useAuth: () => mocks.auth }));
vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api')>(),
  gatewayClient: {
    listProjectByokProviderDrafts: mocks.list,
    getProjectByokProviderDraft: mocks.read,
    createProjectByokProviderDraft: mocks.create,
    updateProjectByokProviderDraft: mocks.update,
  },
}));

import { ProjectModelProviderDrafts } from './ProjectModelProviderDrafts';

function provider(overrides: Record<string, unknown> = {}) {
  return {
    id: 'provider-stable-id',
    name: 'Customer model',
    type: 'openai',
    base_url: 'https://provider.example/v1',
    model: 'model-a',
    wire_api: null,
    azure_api_version: null,
    reasoning_efforts: ['low'],
    prompt_capacity_tokens: null,
    scope: 'project',
    projectId: 'project-1',
    state: 'Draft',
    revision: 1,
    configurationHash: 'a'.repeat(64),
    has_api_key: false,
    is_active: false,
    hasCredential: false,
    ...overrides,
  };
}

describe('Project BYOK provider drafts', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocks.apiCall.mockImplementation(
      (operation: (token: string, tenant: string | null) => Promise<unknown>) =>
        operation('project-access-token', mocks.auth.authorizationContext?.tenantId ?? null),
    );
    mocks.authorize.mockResolvedValue(undefined);
    mocks.ensureSession.mockResolvedValue(true);
    mocks.resolveAuthorizationContext.mockResolvedValue({ tenantId: 'tenant-1' });
    mocks.list.mockResolvedValue({ providers: [], nextAfterProviderId: null });
    mocks.read.mockResolvedValue(provider());
    mocks.create.mockResolvedValue(provider({ id: 'created-provider-id', revision: 1 }));
    mocks.update.mockResolvedValue(provider({ name: 'Renamed model', revision: 2 }));
    Object.assign(mocks.auth, {
      apiCall: mocks.apiCall,
      authorize: mocks.authorize,
      ensureSession: mocks.ensureSession,
      resolveAuthorizationContext: mocks.resolveAuthorizationContext,
      session: { binding: { projectId: 'project-1' } },
      authSessionGeneration: 0,
      authorizationContext: { tenantId: 'tenant-1' },
      authorizationContextLoading: false,
    });
  });

  it('lists, shows, and updates by stable provider ID with metadata only', async () => {
    mocks.list.mockResolvedValue({
      providers: [provider()],
      nextAfterProviderId: null,
    });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockResolvedValueOnce(provider({ name: 'Renamed model', revision: 2 }));
    mocks.update.mockResolvedValue(provider({ name: 'Renamed model', revision: 2 }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));

    expect(await screen.findByText('provider-stable-id')).toBeTruthy();
    expect(screen.queryByText(/hasCredential|has_api_key|credentialVersion/i)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await screen.findByText('Provider draft saved.');
    await waitFor(() => expect(mocks.update).toHaveBeenCalledOnce());
    const [token, projectId, providerId, request, tenant] = mocks.update.mock.calls[0];
    expect([token, projectId, providerId, tenant]).toEqual([
      'project-access-token', 'project-1', 'provider-stable-id', 'tenant-1',
    ]);
    expect(Object.keys(request).sort()).toEqual([
      'azure_api_version',
      'base_url',
      'expectedRevision',
      'model',
      'name',
      'operationId',
      'prompt_capacity_tokens',
      'reasoning_efforts',
      'type',
      'wire_api',
    ]);
    expect(request).toMatchObject({
      expectedRevision: 1,
      name: 'Renamed model',
      type: 'openai',
      base_url: 'https://provider.example/v1',
      model: 'model-a',
      reasoning_efforts: ['low'],
    });
    expect(JSON.stringify(request)).not.toMatch(/api[_-]?key|secret|credential/i);
    expect(mocks.ensureSession).toHaveBeenCalledWith({ projectId: 'project-1' }, true);
    expect(mocks.resolveAuthorizationContext).toHaveBeenCalledWith('tenant-1');
  });

  it('creates metadata without a client-selected provider ID or secret fields', async () => {
    mocks.create
      .mockRejectedValueOnce(new Error('Temporary save failure.'))
      .mockResolvedValueOnce(provider({ id: 'created-provider-id', revision: 1 }));
    mocks.read.mockResolvedValueOnce(provider({
      id: 'created-provider-id',
      name: 'New model',
      revision: 1,
    }));
    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Add provider draft' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'New model' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Provider type' }), {
      target: { value: 'openai' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Base URL' }), {
      target: { value: 'https://provider.example/v1' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Model or deployment' }), {
      target: { value: 'model-b' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create draft' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Create draft' }));

    await screen.findByText('Provider draft saved.');
    await waitFor(() => expect(mocks.create).toHaveBeenCalledTimes(2));
    const [token, projectId, request, tenant] = mocks.create.mock.calls[0];
    const retriedRequest = mocks.create.mock.calls[1][2];
    expect([token, projectId, tenant]).toEqual(['project-access-token', 'project-1', 'tenant-1']);
    expect(retriedRequest.operationId).toBe(request.operationId);
    expect(Object.keys(request).sort()).toEqual([
      'azure_api_version',
      'base_url',
      'expectedRevision',
      'model',
      'name',
      'operationId',
      'prompt_capacity_tokens',
      'reasoning_efforts',
      'type',
      'wire_api',
    ]);
    expect(request).toMatchObject({
      expectedRevision: 0,
      name: 'New model',
      type: 'openai',
      base_url: 'https://provider.example/v1',
      model: 'model-b',
    });
    expect(JSON.stringify(request)).not.toMatch(/api[_-]?key|secret|credential|providerId/i);
    expect(await screen.findByText('created-provider-id')).toBeTruthy();
  });

  it('reloads the latest revision after an update conflict', async () => {
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: null });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockResolvedValueOnce(provider({ name: 'Updated elsewhere', revision: 2 }))
      .mockResolvedValueOnce(provider({ name: 'Merged name', revision: 3 }));
    mocks.update
      .mockRejectedValueOnce(new GatewayError(409, { code: 'model_provider_draft_conflict' }, 'Stale revision.'))
      .mockResolvedValueOnce(provider({ name: 'Merged name', revision: 3 }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'My edit' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Discard edits and reload latest' }));
    expect(await screen.findByText('Updated elsewhere')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    expect((screen.getByRole('textbox', { name: 'Name' }) as HTMLInputElement).value)
      .toBe('Updated elsewhere');
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Merged name' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await screen.findByText('Provider draft saved.');
    expect(mocks.update).toHaveBeenCalledTimes(2);
    const firstRequest = mocks.update.mock.calls[0][3];
    const refreshedRequest = mocks.update.mock.calls[1][3];
    expect(firstRequest.expectedRevision).toBe(1);
    expect(refreshedRequest.expectedRevision).toBe(2);
    expect(refreshedRequest.operationId).not.toBe(firstRequest.operationId);
  });

  it('reloads the provider after an idempotent retry returns an older revision', async () => {
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: null });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockResolvedValueOnce(provider({ name: 'Updated elsewhere', revision: 3 }));
    mocks.update
      .mockRejectedValueOnce(new Error('The connection closed before the response arrived.'))
      .mockResolvedValueOnce(provider({ name: 'My edit', revision: 2 }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'My edit' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));
    expect(await screen.findByRole('alert')).toBeTruthy();

    const firstRequest = mocks.update.mock.calls[0][3];
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await screen.findByText('Provider draft saved.');
    expect(await screen.findByText('Updated elsewhere')).toBeTruthy();
    expect(mocks.update.mock.calls[1][3].operationId).toBe(firstRequest.operationId);
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    expect((screen.getByRole('textbox', { name: 'Name' }) as HTMLInputElement).value)
      .toBe('Updated elsewhere');
    expect(mocks.update.mock.calls[1][3].expectedRevision).toBe(1);
  });

  it('hides drafts and asks for project sign-in when the active project session is lost', async () => {
    mocks.list.mockResolvedValue({
      providers: [provider()],
      nextAfterProviderId: null,
    });
    mocks.ensureSession
      .mockResolvedValueOnce(true)
      .mockResolvedValueOnce(false);
    const rendered = render(
      <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />,
    );
    expect(await screen.findByRole('button', { name: 'Customer model' })).toBeTruthy();

    Object.assign(mocks.auth, {
      session: null,
      authSessionGeneration: 1,
    });
    rendered.rerender(
      <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />,
    );

    expect(await screen.findByText('Sign in for access to this project’s provider drafts.'))
      .toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Customer model' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign in for project access' })).toBeTruthy();
  });

  it('keeps an unchanged save operation ID across project reauthorization', async () => {
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: null });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockResolvedValueOnce(provider())
      .mockResolvedValueOnce(provider({ name: 'Renamed model', revision: 2 }));
    mocks.update
      .mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'Session expired.'))
      .mockResolvedValueOnce(provider({ name: 'Renamed model', revision: 2 }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    expect(await screen.findByRole('button', { name: 'Sign in for project access' }))
      .toBeTruthy();
    expect(screen.queryByRole('textbox', { name: 'Name' })).toBeNull();
    const firstRequest = mocks.update.mock.calls[0][3];
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for project access' }));

    // Re-entry starts at the list without restoring cached editor fields.
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    expect((screen.getByRole('textbox', { name: 'Name' }) as HTMLInputElement).value)
      .toBe('Customer model');
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await screen.findByText('Provider draft saved.');
    expect(mocks.update.mock.calls[1][3].operationId).toBe(firstRequest.operationId);
    expect(mocks.update.mock.calls[1][3].expectedRevision).toBe(firstRequest.expectedRevision);
  });

  it('shows loading, error, retry, and empty states', async () => {
    let finishList!: (page: { providers: ReturnType<typeof provider>[]; nextAfterProviderId: null }) => void;
    mocks.list
      .mockImplementationOnce(() => new Promise((resolve) => { finishList = resolve; }))
      .mockRejectedValueOnce(new Error('Provider drafts are unavailable.'))
      .mockResolvedValueOnce({ providers: [], nextAfterProviderId: null });

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    expect(await screen.findByText('Loading provider drafts…')).toBeTruthy();
    finishList({ providers: [], nextAfterProviderId: null });
    expect(await screen.findByText('No provider drafts are set up for this project.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('No provider drafts are set up for this project.')).toBeTruthy();
    expect(mocks.list).toHaveBeenCalledTimes(3);
  });

  it('uses the owner cursor to load the next provider page', async () => {
    mocks.list
      .mockResolvedValueOnce({
        providers: [provider()],
        nextAfterProviderId: 'provider-stable-id',
      })
      .mockResolvedValueOnce({
        providers: [provider({ id: 'provider-2', name: 'Second provider' })],
        nextAfterProviderId: null,
      });

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    expect(await screen.findByRole('button', { name: 'Customer model' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));

    expect(await screen.findByRole('button', { name: 'Second provider' })).toBeTruthy();
    expect(mocks.list.mock.calls[1]).toEqual([
      'project-access-token',
      'project-1',
      50,
      'provider-stable-id',
      'tenant-1',
    ]);
  });

  it('hides stale list and detail data after failed reads', async () => {
    mocks.list
      .mockResolvedValueOnce({ providers: [provider()], nextAfterProviderId: null })
      .mockRejectedValueOnce(new Error('Provider drafts are unavailable.'));
    mocks.read
      .mockRejectedValueOnce(new Error('Provider details are unavailable.'))
      .mockResolvedValueOnce(provider());

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('provider-stable-id')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Back to provider drafts' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Customer model' })).toBeNull();
  });

  it('shows returned host, port, and host-request status labels and refreshes without saving', async () => {
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: null });
    mocks.read
      .mockResolvedValueOnce(provider({
        base_url: 'https://provider.example/v1',
        egress: {
          host: 'egress.override.example',
          port: 8443,
          destinationState: null,
          canApprove: false,
          deliveryStatus: 'pending',
        },
      }))
      .mockResolvedValueOnce(provider({
        base_url: 'https://provider.example/v1',
        egress: {
          host: 'egress.override.example',
          port: 8443,
          destinationState: 'requested',
          canApprove: false,
          deliveryStatus: 'delivered',
        },
      }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));

    expect(await screen.findByText('Waiting to send host request')).toBeTruthy();
    expect(screen.getByText('egress.override.example')).toBeTruthy();
    expect(screen.getByText('8443')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Waiting for platform admin approval')).toBeTruthy();
    expect(mocks.read).toHaveBeenCalledTimes(2);
    expect(mocks.read.mock.calls[1]).toEqual([
      'project-access-token',
      'project-1',
      'provider-stable-id',
      'tenant-1',
    ]);
    expect(mocks.create).not.toHaveBeenCalled();
    expect(mocks.update).not.toHaveBeenCalled();
  });

  it('maps failed, missing, unavailable, and unexpected egress combinations and distinguishes failed GETs', async () => {
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: null });
    let failRefresh!: (reason: Error) => void;
    mocks.read
      .mockResolvedValueOnce(provider({
        egress: {
          host: 'api.openai.com',
          port: 443,
          destinationState: null,
          canApprove: false,
          deliveryStatus: 'failed',
        },
      }))
      .mockResolvedValueOnce(provider({ egress: null }))
      .mockResolvedValueOnce(provider({
        egress: {
          host: 'api.openai.com',
          port: 443,
          destinationState: null,
          canApprove: false,
          deliveryStatus: 'unavailable',
        },
      }))
      .mockResolvedValueOnce(provider({
        egress: {
          host: 'api.openai.com',
          port: 443,
          destinationState: 'requested',
          canApprove: false,
          deliveryStatus: 'pending',
        },
      }))
      .mockResolvedValueOnce(provider({
        egress: {
          host: 'api.openai.com',
          port: 443,
          destinationState: null,
          canApprove: false,
          deliveryStatus: 'delivered',
        },
      }))
      .mockImplementationOnce(() => new Promise((_, reject) => { failRefresh = reject; }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('Host request delivery failed')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Host request status unavailable')).toBeTruthy();
    expect(screen.queryByText('provider.example')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Host request status unavailable')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Host request status unavailable')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Host request status unavailable')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(screen.queryByText('Host request status unavailable')).toBeNull();
    failRefresh(new Error('Gateway read failed.'));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByText('Gateway read failed.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.queryByText('Host request status unavailable')).toBeNull();
    expect(mocks.update).not.toHaveBeenCalled();
  });

  it('clears state on 403 and does not reuse operation IDs across identities or scopes', async () => {
    mocks.auth.authorizationContext = { tenantId: 'tenant-1', actorId: 'actor-1', issuer: 'broker' } as unknown as { tenantId: string };
    mocks.list.mockResolvedValue({ providers: [provider()], nextAfterProviderId: 'cursor-1' });
    mocks.read.mockResolvedValue(provider());
    mocks.update
      .mockRejectedValueOnce(new GatewayError(403, { code: 'forbidden' }, 'Access denied.'))
      .mockResolvedValueOnce(provider({ name: 'Renamed model', revision: 2 }));

    const rendered = render(
      <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    expect(await screen.findByText('Access denied.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Customer model' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
    expect(screen.queryByRole('textbox', { name: 'Name' })).toBeNull();

    const firstOperationId = mocks.update.mock.calls[0][3].operationId;

    // Different actor signs in after 403 -> must not reuse the previous actor's operationId.
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    mocks.resolveAuthorizationContext.mockResolvedValue({
      tenantId: 'tenant-1',
      actorId: 'actor-2',
      issuer: 'broker',
    });
    mocks.auth.authorizationContext = { tenantId: 'tenant-1', actorId: 'actor-2', issuer: 'broker' } as unknown as { tenantId: string };
    rendered.rerender(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await screen.findByText('Provider draft saved.');
    expect(mocks.update.mock.calls[1][3].operationId).not.toBe(firstOperationId);
  });

  it('reloads on tenant switch and preserves the confirmed tenant during token renewal', async () => {
    mocks.list
      .mockResolvedValueOnce({ providers: [provider()], nextAfterProviderId: null })
      .mockResolvedValueOnce({
        providers: [provider({ id: 'provider-t2', name: 'Tenant 2 provider' })],
        nextAfterProviderId: null,
      })
      .mockResolvedValueOnce({
        providers: [provider({ id: 'provider-t2', name: 'Tenant 2 provider' })],
        nextAfterProviderId: null,
      });

    const rendered = render(
      <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />,
    );
    expect(await screen.findByRole('button', { name: 'Customer model' })).toBeTruthy();

    // Switch tenant from tenant-1 to tenant-2.
    mocks.resolveAuthorizationContext.mockResolvedValue({ tenantId: 'tenant-2' });
    mocks.auth.authorizationContext = { tenantId: 'tenant-2' };
    rendered.rerender(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-2" />);

    expect(screen.queryByRole('button', { name: 'Customer model' })).toBeNull();
    expect(await screen.findByRole('button', { name: 'Tenant 2 provider' })).toBeTruthy();
    expect(mocks.resolveAuthorizationContext).toHaveBeenLastCalledWith('tenant-2');

    // Temporary loss of authorizationContext during token renewal must keep tenant-2.
    mocks.auth.authorizationContext = null as unknown as { tenantId: string };
    mocks.auth.authSessionGeneration = 1;
    rendered.rerender(<ProjectModelProviderDrafts projectId="project-1" tenantSelector={undefined} />);

    await waitFor(() => expect(mocks.resolveAuthorizationContext).toHaveBeenLastCalledWith('tenant-2'));
    expect(mocks.ensureSession).toHaveBeenLastCalledWith({ projectId: 'project-1' }, true);
  });

  it('ignores late refresh, mismatched provider ID, and late save success or failure after navigation', async () => {
    let finishRefresh!: (value: ReturnType<typeof provider>) => void;
    let finishLateSave!: (value: ReturnType<typeof provider>) => void;
    let failLateSave!: (reason: Error) => void;
    mocks.list.mockResolvedValue({
      providers: [
        provider(),
        provider({ id: 'provider-2', name: 'Second provider' }),
      ],
      nextAfterProviderId: null,
    });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockImplementationOnce(() => new Promise((resolve) => { finishRefresh = resolve; }))
      .mockResolvedValueOnce(provider({ id: 'provider-2', name: 'Second provider' }))
      .mockResolvedValueOnce(provider({ id: 'wrong-provider-id', name: 'Wrong' }));
    mocks.update
      .mockImplementationOnce(() => new Promise((resolve) => { finishLateSave = resolve; }))
      .mockImplementationOnce(() => new Promise((_, reject) => { failLateSave = reject; }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();

    // Start refresh then go back before it completes.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    fireEvent.click(screen.getByRole('button', { name: 'Back to provider drafts' }));
    finishRefresh(provider({ name: 'Stale detail' }));
    expect(await screen.findByRole('button', { name: 'Second provider' })).toBeTruthy();
    expect(screen.queryByText('Stale detail')).toBeNull();

    // Open Second provider, then go back and open Customer model which gets mismatched ID.
    fireEvent.click(screen.getByRole('button', { name: 'Second provider' }));
    expect(await screen.findByText('provider-2')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Back to provider drafts' }));
    fireEvent.click(screen.getByRole('button', { name: 'Customer model' }));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('wrong-provider-id')).toBeNull();

    // Return to Second provider, edit, save, then Cancel before save finishes.
    fireEvent.click(screen.getByRole('button', { name: 'Back to provider drafts' }));
    fireEvent.click(screen.getByRole('button', { name: 'Second provider' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Edit metadata' })).toBeTruthy());
    mocks.read.mockResolvedValueOnce(provider({ id: 'provider-2', name: 'Second provider' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('provider-2')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    finishLateSave(provider({ id: 'provider-2', name: 'Late saved', revision: 2 }));
    expect(screen.queryByText('Provider draft saved.')).toBeNull();
    expect(screen.queryByText(/canceled/i)).toBeNull();

    // Edit again, save, Cancel before late failure.
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    failLateSave(new Error('Late save failed.'));
    expect(screen.queryByText('Late save failed.')).toBeNull();
  });

  it('disables editing until post-save detail loads, preserves save confirmation on read failure, and reloads list on return', async () => {
    let finishPostSaveRead!: (value: ReturnType<typeof provider>) => void;
    mocks.list
      .mockResolvedValueOnce({ providers: [provider()], nextAfterProviderId: null })
      .mockResolvedValueOnce({
        providers: [provider({ name: 'Renamed model', revision: 2 })],
        nextAfterProviderId: null,
      });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockRejectedValueOnce(new Error('Temporary read failure.'))
      .mockImplementationOnce(() => new Promise((resolve) => { finishPostSaveRead = resolve; }));
    mocks.update.mockResolvedValueOnce(provider({ name: 'Renamed model', revision: 2 }));

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    // Save confirmation is preserved when post-save detail GET fails, and Edit remains disabled.
    expect(await screen.findByText('Provider draft saved.')).toBeTruthy();
    expect(await screen.findByText('Temporary read failure.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Edit metadata' }) as HTMLButtonElement).disabled).toBe(true);
    // No second automatic list read after save.
    expect(mocks.list).toHaveBeenCalledTimes(1);

    // Retry post-save detail GET: Edit stays disabled while loading, then enables.
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect((screen.getByRole('button', { name: 'Edit metadata' }) as HTMLButtonElement).disabled).toBe(true);
    finishPostSaveRead(provider({ name: 'Renamed model', revision: 2 }));
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Edit metadata' }) as HTMLButtonElement).disabled).toBe(false),
    );
    expect(screen.getByText('Provider draft saved.')).toBeTruthy();

    // Returning to list reloads it.
    fireEvent.click(screen.getByRole('button', { name: 'Back to provider drafts' }));
    await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2));
  });

  it('drops stale detail and save responses when authSessionEpoch switches', async () => {
    let finishDetailRefresh!: (value: ReturnType<typeof provider>) => void;
    let finishSave!: (value: ReturnType<typeof provider>) => void;
    mocks.list
      .mockResolvedValueOnce({ providers: [provider()], nextAfterProviderId: null })
      .mockResolvedValueOnce({
        providers: [provider({ id: 'new-session-provider', name: 'New session provider' })],
        nextAfterProviderId: null,
      })
      .mockResolvedValueOnce({
        providers: [provider({ id: 'after-save-switch', name: 'After save switch' })],
        nextAfterProviderId: null,
      });
    mocks.read
      .mockResolvedValueOnce(provider())
      .mockImplementationOnce(() => new Promise((resolve) => { finishDetailRefresh = resolve; }))
      .mockResolvedValueOnce(provider({ id: 'new-session-provider', name: 'New session provider' }));
    mocks.update.mockImplementationOnce(() => new Promise((resolve) => { finishSave = resolve; }));
    (mocks.auth as { authSessionEpoch?: number }).authSessionEpoch = 0;

    const rendered = render(
      <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />,
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('provider-stable-id')).toBeTruthy();

    // Start detail refresh, then switch authSessionEpoch before it settles.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    (mocks.auth as { authSessionEpoch?: number }).authSessionEpoch = 1;
    mocks.auth.authSessionGeneration = 1;
    rendered.rerender(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);

    finishDetailRefresh(provider({ name: 'Stale detail from old epoch' }));
    expect(await screen.findByRole('button', { name: 'New session provider' })).toBeTruthy();
    expect(screen.queryByText('Stale detail from old epoch')).toBeNull();

    // Open provider, start save, then switch authSessionEpoch before save settles.
    fireEvent.click(screen.getByRole('button', { name: 'New session provider' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Edit metadata' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    (mocks.auth as { authSessionEpoch?: number }).authSessionEpoch = 2;
    mocks.auth.authSessionGeneration = 2;
    rendered.rerender(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);

    finishSave(provider({ name: 'Stale save from old epoch', revision: 2 }));
    expect(await screen.findByRole('button', { name: 'After save switch' })).toBeTruthy();
    expect(screen.queryByText('Provider draft saved.')).toBeNull();
  });

  it('loads the list after signInForProject replaces a pre-existing session', async () => {
    mocks.ensureSession.mockResolvedValueOnce(false);
    mocks.auth.session = { binding: null } as unknown as { binding: { projectId: string } };
    (mocks.auth as { authSessionEpoch?: number }).authSessionEpoch = 0;
    mocks.list.mockResolvedValueOnce({
      providers: [provider({ id: 'signed-in-provider', name: 'Signed in provider' })],
      nextAfterProviderId: null,
    });
    mocks.authorize.mockImplementationOnce(async () => {
      mocks.auth.session = { binding: { projectId: 'project-1' } };
      (mocks.auth as { authSessionEpoch?: number }).authSessionEpoch = 1;
      mocks.auth.authSessionGeneration = 1;
    });

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    const signInButton = await screen.findByRole('button', { name: 'Sign in for project access' });
    fireEvent.click(signInButton);

    expect(await screen.findByRole('button', { name: 'Signed in provider' })).toBeTruthy();
    expect(mocks.list).toHaveBeenCalledTimes(1);
  });

  it('offers project sign-in when resolveAuthorizationContext returns null with a 401 error', async () => {
    const unauthorized = new GatewayError(401, { code: 'unauthorized' }, 'Token expired.');
    mocks.resolveAuthorizationContext.mockResolvedValueOnce(null);
    (mocks.auth as { getAuthorizationContextError?: () => unknown }).getAuthorizationContextError =
      () => unauthorized;

    render(<ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />);
    expect(await screen.findByRole('button', { name: 'Sign in for project access' })).toBeTruthy();
    expect(screen.getByText('Token expired.')).toBeTruthy();
  });
});
