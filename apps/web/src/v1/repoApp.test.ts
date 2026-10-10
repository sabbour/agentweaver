import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  IdentityBrokerRepoAppClient,
  RepoAppBrokerError,
  isCurrentRepoAppPopupCallback,
  isRepoAppConnectedCallbackMessage,
} from './repoApp';

const status = {
  state: 'connected',
  localReadiness: 'access_token_available',
  connectionId: 'connection-1',
  connectionRevision: 2,
  githubLogin: 'octo',
  accessTokenExpiresAt: '2026-10-08T18:00:00Z',
  updatedAt: '2026-10-08T17:00:00Z',
};

const repositories = {
  connectionId: 'connection-1',
  connectionRevision: 2,
  githubLogin: 'octo',
  repositories: [{
    installationId: 101,
    repositoryId: 202,
    fullName: 'octo/agentweaver',
    ownerLogin: 'octo',
    isPrivate: true,
    defaultBranch: 'main',
  }],
};

const selection = {
  code: 'opaque-selection-code',
  connectionId: 'connection-1',
  connectionRevision: 2,
  installationId: 101,
  repositoryId: 202,
  repositoryFullName: 'octo/agentweaver',
};

afterEach(() => {
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

describe('IdentityBrokerRepoAppClient', () => {
  it('reads exact status and repository contracts with cookie credentials and no bearer token', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(status))
      .mockResolvedValueOnce(Response.json(repositories));
    const client = new IdentityBrokerRepoAppClient('https://identity.example.test/', fetcher);

    await expect(client.getStatus()).resolves.toEqual(status);
    await expect(client.getRepositories()).resolves.toEqual(repositories);

    expect(fetcher.mock.calls.map(([url]) => String(url))).toEqual([
      'https://identity.example.test/auth/github/repo-app/status',
      'https://identity.example.test/auth/github/repo-app/repositories',
    ]);
    for (const [, init] of fetcher.mock.calls) {
      expect(init?.credentials).toBe('include');
      expect(init?.cache).toBe('no-store');
      expect(new Headers(init?.headers).get('Authorization')).toBeNull();
      expect(new Headers(init?.headers).get('Accept')).toBe('application/json');
    }
  });

  it.each(['connect', 'install'] as const)(
    'starts %s through a native antiforgery form targeted at the tracked popup',
    async (action) => {
      const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
        Response.json({ csrf_token: 'csrf-value' }),
      );
      const submittedForms: HTMLFormElement[] = [];
      const submit = vi.spyOn(HTMLFormElement.prototype, 'submit')
        .mockImplementation(function (this: HTMLFormElement) {
          submittedForms.push(this);
        });
      const client = new IdentityBrokerRepoAppClient('https://identity.example.test', fetcher);

      await (action === 'connect'
        ? client.beginConnect({ name: 'agentweaver-repo-app-1', closed: false })
        : client.beginInstallationSetup({ name: 'agentweaver-repo-app-1', closed: false }));

      expect(fetcher).toHaveBeenCalledOnce();
      expect(String(fetcher.mock.calls[0][0])).toBe(
        'https://identity.example.test/auth/github/repo-app/csrf',
      );
      expect(fetcher.mock.calls[0][1]?.credentials).toBe('include');
      expect(submit).toHaveBeenCalledOnce();
      const form = document.body.querySelector('form');
      expect(form).toBeNull();
      expect(submit).toHaveBeenCalledOnce();
      expect(submittedForms[0]?.method).toBe('post');
      expect(submittedForms[0]?.action).toBe(
        `https://identity.example.test/auth/github/repo-app/${action}`,
      );
      expect(submittedForms[0]?.target).toBe('agentweaver-repo-app-1');
      expect(submittedForms[0]?.querySelector('input')?.name)
        .toBe('__RequestVerificationToken');
      expect(submittedForms[0]?.querySelector('input')?.value).toBe('csrf-value');
    },
  );

  it('uses CSRF cookies and X-CSRF-TOKEN for selection and disconnect', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ csrf_token: 'selection-csrf' }))
      .mockResolvedValueOnce(Response.json(selection))
      .mockResolvedValueOnce(Response.json({ csrf_token: 'disconnect-csrf' }))
      .mockResolvedValueOnce(Response.json({ ...status, state: 'not_connected', localReadiness: 'not_connected', connectionId: null, connectionRevision: null, githubLogin: null, accessTokenExpiresAt: null, updatedAt: null }));
    const client = new IdentityBrokerRepoAppClient('https://identity.example.test', fetcher);

    await expect(client.createRepositorySelection(101, 202)).resolves.toEqual(selection);
    await expect(client.disconnect('connection-1', 2)).resolves.toMatchObject({
      state: 'not_connected',
      connectionId: null,
    });

    expect(String(fetcher.mock.calls[0][0])).toBe(
      'https://identity.example.test/auth/github/repo-app/csrf',
    );
    const [selectionUrl, selectionInit] = fetcher.mock.calls[1];
    expect(String(selectionUrl)).toBe(
      'https://identity.example.test/auth/github/repo-app/selection',
    );
    expect(selectionInit?.method).toBe('POST');
    expect(selectionInit?.credentials).toBe('include');
    expect(new Headers(selectionInit?.headers).get('X-CSRF-TOKEN')).toBe('selection-csrf');
    expect(JSON.parse(String(selectionInit?.body))).toEqual({
      installationId: 101,
      repositoryId: 202,
    });

    const [disconnectUrl, disconnectInit] = fetcher.mock.calls[3];
    expect(String(disconnectUrl)).toBe(
      'https://identity.example.test/auth/github/repo-app/disconnect',
    );
    expect(disconnectInit?.method).toBe('POST');
    expect(new Headers(disconnectInit?.headers).get('X-CSRF-TOKEN')).toBe('disconnect-csrf');
    expect(JSON.parse(String(disconnectInit?.body))).toEqual({
      connectionId: 'connection-1',
      expectedConnectionRevision: 2,
    });
  });

  it('rejects malformed Broker DTOs and preserves explicit endpoint errors', async () => {
    const malformed = new IdentityBrokerRepoAppClient(
      'https://identity.example.test',
      vi.fn<typeof fetch>().mockResolvedValue(Response.json({ ...status, connected: true })),
    );
    await expect(malformed.getStatus()).rejects.toMatchObject({
      name: 'RepoAppBrokerError',
      status: 502,
      code: 'broker_contract_invalid',
    });

    const failed = new IdentityBrokerRepoAppClient(
      'https://identity.example.test',
      vi.fn<typeof fetch>().mockResolvedValue(Response.json(
        { error: 'not_connected' },
        { status: 409 },
      )),
    );
    await expect(failed.getRepositories()).rejects.toMatchObject({
      name: 'RepoAppBrokerError',
      status: 409,
      code: 'not_connected',
      message: 'Identity Broker Repo App request failed with HTTP 409 (not_connected).',
    } satisfies Partial<RepoAppBrokerError>);
  });

  it('accepts only the exact popup callback notification shape', () => {
    const callback = {
      type: 'agentweaver.repo-app.connected',
    };
    expect(isRepoAppConnectedCallbackMessage(callback)).toBe(true);
    const extraFields: unknown = {
      ...callback,
      connected: true,
    };
    expect(isRepoAppConnectedCallbackMessage(extraFields)).toBe(false);
    expect(isRepoAppConnectedCallbackMessage({ type: 'wrong-callback' })).toBe(false);
  });

  it('requires the exact origin, tracked popup source, and active request generation', () => {
    const popup = {} as Window;
    const event = {
      origin: 'https://web.example.test',
      source: popup,
      data: { type: 'agentweaver.repo-app.connected' },
    } as MessageEvent<unknown>;
    const matches = (candidate: MessageEvent<unknown>, generation = 7) =>
      isCurrentRepoAppPopupCallback(
        candidate,
        'https://web.example.test',
        popup,
        generation,
        7,
      );

    expect(matches(event)).toBe(true);
    expect(matches({ ...event, origin: 'https://attacker.example.test' })).toBe(false);
    expect(matches({ ...event, source: {} as Window })).toBe(false);
    expect(matches(event, 6)).toBe(false);
    const extraMessage: MessageEvent<unknown> = {
      ...event,
      data: { type: 'agentweaver.repo-app.connected', connected: true },
    };
    expect(matches(extraMessage)).toBe(false);
  });
});
