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
        operation('project-access-token', 'tenant-1'),
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
    fireEvent.click(await screen.findByRole('button', { name: 'Save metadata' }));

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
});
