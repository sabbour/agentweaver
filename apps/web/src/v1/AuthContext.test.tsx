import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';

const mocks = vi.hoisted(() => ({
  buildAuthorizeUrl: vi.fn((_config: unknown, transaction: { state: string }) =>
    `https://broker.test/authorize?state=${encodeURIComponent(transaction.state)}`),
  exchangeAuthorizationCode: vi.fn(),
  isConsentPrompt: vi.fn(() => false),
  refreshBrokerToken: vi.fn(),
  submitBrokerConsent: vi.fn(),
}));

vi.mock('./authProtocol', () => ({
  buildAuthorizeUrl: mocks.buildAuthorizeUrl,
  clearAuthorizationTransaction: vi.fn(),
  defaultAuthorizationConfig: () => ({
    brokerUrl: 'https://broker.test',
    clientId: 'client-1',
    redirectUri: 'https://app.test/auth/callback',
    scopes: ['agentweaver.api'],
  }),
  exchangeAuthorizationCode: mocks.exchangeAuthorizationCode,
  isConsentPrompt: mocks.isConsentPrompt,
  parseAuthorizationResult: (responseUrl: string, expectedState: string) => {
    const url = new URL(responseUrl);
    if (url.searchParams.get('state') !== expectedState) return null;
    const code = url.searchParams.get('code');
    return code ? { code } : null;
  },
  pkceChallenge: async () => 'challenge',
  randomState: () => 'state-1',
  randomVerifier: () => 'verifier-1',
  readAuthorizationTransaction: () => null,
  refreshBrokerToken: mocks.refreshBrokerToken,
  storeAuthorizationTransaction: vi.fn(),
  submitBrokerConsent: mocks.submitBrokerConsent,
}));

vi.mock('./config', () => ({
  gatewayBaseUrl: '/api/v1',
  brokerBaseUrl: 'https://broker.test',
  oauthClientId: 'client-1',
  oauthRedirectUri: 'https://app.test/auth/callback',
  oauthScopes: ['agentweaver.api'],
  missingAuthConfiguration: () => [],
}));

import { AuthProvider, useAuth } from './AuthContext';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => { resolve = complete; });
  return { promise, resolve };
}

function token(accessToken: string, refreshToken?: string) {
  return {
    access_token: accessToken,
    token_type: 'Bearer',
    expires_in: 3600,
    refresh_token: refreshToken,
  };
}

function Harness() {
  const auth = useAuth();
  const runConcurrentCalls = async () => {
    const operation = async (accessToken: string) => {
      if (accessToken === 'access-1')
        throw new GatewayError(401, { code: 'unauthorized' }, 'expired');
      return accessToken;
    };
    return await Promise.all([auth.apiCall(operation), auth.apiCall(operation)]);
  };

  return (
    <div>
      <button type="button" onClick={() => { void auth.authorize(null); }}>Sign in</button>
      <button type="button" onClick={() => { void auth.authorize({ projectId: 'project-1', runId: 'run-1' }); }}>
        Sign in for run
      </button>
      {auth.consent && (
        <button type="button" onClick={() => { void auth.decideConsent(true); }}>Approve consent</button>
      )}
      <button type="button" onClick={() => { void runConcurrentCalls().catch(() => undefined); }}>Call twice</button>
      <button type="button" onClick={auth.signOut}>Sign out</button>
      <output>{auth.session?.accessToken ?? 'signed-out'}</output>
      <output>{auth.error ?? 'no-auth-error'}</output>
    </div>
  );
}

describe('Broker refresh coordination', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/projects');
    vi.clearAllMocks();
    mocks.exchangeAuthorizationCode.mockResolvedValue(token('access-1', 'refresh-1'));
    vi.spyOn(window, 'open').mockReturnValue({
      closed: false,
      location: { replace: vi.fn() },
      close: vi.fn(),
    } as unknown as Window);
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => ({
      ok: true,
      url: 'https://app.test/auth/callback?code=code-1&state=state-1',
      json: async () => null,
    })));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('redeems a rotating refresh token once for concurrent unauthorized calls', async () => {
    const refresh = deferred<ReturnType<typeof token>>();
    mocks.refreshBrokerToken.mockReturnValue(refresh.promise);
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('access-1');

    fireEvent.click(screen.getByRole('button', { name: 'Call twice' }));
    await waitFor(() => expect(mocks.refreshBrokerToken).toHaveBeenCalledOnce());
    await act(async () => {
      refresh.resolve(token('access-2', 'refresh-2'));
      await refresh.promise;
    });
    await screen.findByText('access-2');
    expect(mocks.refreshBrokerToken).toHaveBeenCalledOnce();
  });

  it('does not restore a session after sign-out while refresh is in flight', async () => {
    const refresh = deferred<ReturnType<typeof token>>();
    mocks.refreshBrokerToken.mockReturnValue(refresh.promise);
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('access-1');

    fireEvent.click(screen.getByRole('button', { name: 'Call twice' }));
    await waitFor(() => expect(mocks.refreshBrokerToken).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await act(async () => {
      refresh.resolve(token('access-2', 'refresh-2'));
      await refresh.promise;
    });
    await screen.findByText('signed-out');
    expect(screen.queryByText('access-2')).toBeNull();
  });

  it('surfaces Broker authorization errors instead of polling indefinitely', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 400,
      url: 'https://broker.test/authorize?state=state-1',
      json: async () => ({
        error: 'access_denied',
        error_description: 'No active grant authorizes the requested project and run.',
      }),
    } as Response));
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run' }));

    await screen.findByText('No active grant authorizes the requested project and run.');
    expect(fetch).toHaveBeenCalledOnce();
  });

  it('restarts Broker authorization after consent without losing the run binding', async () => {
    window.history.replaceState({}, '', '/projects/project-1/runs/run-1?view=approvals');
    mocks.exchangeAuthorizationCode.mockResolvedValue(token('access-after-consent'));
    mocks.isConsentPrompt.mockReturnValueOnce(true);
    mocks.submitBrokerConsent.mockResolvedValue({
      type: 'opaqueredirect',
      status: 0,
      ok: false,
      url: 'https://broker.test/connect/consent',
    } as Response);
    vi.mocked(fetch)
      .mockResolvedValueOnce({
        ok: true,
        url: 'https://broker.test/authorize?state=state-1',
        json: async () => ({
          consent_required: true,
          consent_handle: 'consent-1',
          client_id: 'client-1',
          requested_scopes: ['agentweaver.api'],
          csrf_token: 'csrf-1',
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        url: 'https://app.test/auth/callback?code=code-2&state=state-1',
        json: async () => null,
      } as Response);

    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Approve consent' }));

    await screen.findByText('access-after-consent');
    expect(mocks.submitBrokerConsent).toHaveBeenCalledOnce();
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledTimes(2);
    expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({
        binding: { projectId: 'project-1', runId: 'run-1' },
      }),
      'challenge',
    );
    expect(window.location.pathname).toBe('/projects/project-1/runs/run-1');
  });
});
