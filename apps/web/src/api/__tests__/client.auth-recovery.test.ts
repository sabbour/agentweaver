import { afterEach, describe, expect, it, vi } from 'vitest';

const authMocks = vi.hoisted(() => {
  const state: { token: string | null } = { token: 'stale-token' };
  return {
    state,
    requestSessionAuthFromPeer: vi.fn(async () => {
      state.token = 'peer-token';
      return true;
    }),
    clearSessionAuth: vi.fn(() => {
      state.token = null;
    }),
    notifySessionAuthInvalid: vi.fn(),
  };
});

vi.mock('../../config', () => ({
  getSessionToken: () => authMocks.state.token,
  requestSessionAuthFromPeer: authMocks.requestSessionAuthFromPeer,
  clearSessionAuth: authMocks.clearSessionAuth,
  notifySessionAuthInvalid: authMocks.notifySessionAuthInvalid,
}));

import { AgentweaverApiClient } from '../client';

describe('AgentweaverApiClient auth recovery', () => {
  afterEach(() => {
    authMocks.state.token = 'stale-token';
    authMocks.requestSessionAuthFromPeer.mockClear();
    authMocks.requestSessionAuthFromPeer.mockImplementation(async () => {
      authMocks.state.token = 'peer-token';
      return true;
    });
    authMocks.clearSessionAuth.mockClear();
    authMocks.notifySessionAuthInvalid.mockClear();
    vi.unstubAllGlobals();
  });

  it('requests a different peer token and retries one unauthorized request once', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response('{"error":"unauthorized"}', { status: 401 }))
      .mockResolvedValueOnce(new Response('{"items":[]}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test');

    await expect(client.listProjects()).resolves.toEqual({ items: [] });

    expect(authMocks.requestSessionAuthFromPeer).toHaveBeenCalledWith('stale-token');
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect((fetchMock.mock.calls[0]![1] as RequestInit).headers).toMatchObject({
      Authorization: 'Bearer stale-token',
    });
    expect((fetchMock.mock.calls[1]![1] as RequestInit).headers).toMatchObject({
      Authorization: 'Bearer peer-token',
    });
    expect(authMocks.notifySessionAuthInvalid).not.toHaveBeenCalled();
  });

  it('coalesces concurrent unauthorized responses into one peer recovery', async () => {
    const fetchMock = vi.fn(async (_url: string, init?: RequestInit) => {
      const authorization = (init?.headers as Record<string, string> | undefined)?.Authorization;
      return authorization === 'Bearer peer-token'
        ? new Response('{"items":[]}', { status: 200 })
        : new Response('{"error":"unauthorized"}', { status: 401 });
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://api.example.test');

    const results = await Promise.all([
      client.listProjects(),
      client.listProjects(),
      client.listProjects(),
      client.listProjects(),
    ]);

    expect(results).toEqual([
      { items: [] },
      { items: [] },
      { items: [] },
      { items: [] },
    ]);
    expect(authMocks.requestSessionAuthFromPeer).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(8);
    expect(authMocks.notifySessionAuthInvalid).not.toHaveBeenCalled();
  });

  it('notifies the auth gate when no peer can recover an unauthorized session', async () => {
    authMocks.requestSessionAuthFromPeer.mockResolvedValueOnce(false);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response('{"error":"unauthorized"}', { status: 401 }),
    ));
    const client = new AgentweaverApiClient('https://api.example.test');

    await expect(client.listProjects()).rejects.toMatchObject({ status: 401 });

    expect(authMocks.clearSessionAuth).toHaveBeenCalled();
    expect(authMocks.notifySessionAuthInvalid).toHaveBeenCalledTimes(1);
  });

  it('does not treat a model-provider authorization requirement as a browser-session failure', async () => {
    const requirement = {
      code: 'model_provider_connection_required',
      message: 'Connect a model provider.',
      action: { type: 'configure_project_model_provider', project_id: 'project-1' },
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(
      new Response(JSON.stringify(requirement), { status: 401 }),
    ));
    const client = new AgentweaverApiClient('https://api.example.test');

    await expect(client.getProject('project-1')).rejects.toMatchObject({ status: 401 });

    expect(authMocks.requestSessionAuthFromPeer).not.toHaveBeenCalled();
    expect(authMocks.clearSessionAuth).not.toHaveBeenCalled();
    expect(authMocks.notifySessionAuthInvalid).not.toHaveBeenCalled();
  });
});
