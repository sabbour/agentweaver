import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { AgentweaverGatewayClient, gatewayClient, GatewayError } from './api';
import type { AuthorizationCallbackMessage, AuthorizationTransaction } from './authProtocol';

const mocks = vi.hoisted(() => ({
  buildAuthorizeUrl: vi.fn((_config: unknown, transaction: { state: string }) =>
    `https://broker.test/authorize?state=${encodeURIComponent(transaction.state)}`),
  exchangeAuthorizationCode: vi.fn(),
  isConsentPrompt: vi.fn(() => false),
  parseAuthorizationCallbackMessage: (value: unknown) => value as AuthorizationCallbackMessage,
  parseAuthorizationCallbackParameters: vi.fn(),
  readAuthorizationTransaction: vi.fn<() => AuthorizationTransaction | null>(() => null),
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
  parseAuthorizationCallbackMessage: mocks.parseAuthorizationCallbackMessage,
  parseAuthorizationCallbackParameters: mocks.parseAuthorizationCallbackParameters,
  parseAuthorizationResult: (responseUrl: string, expectedState: string) => {
    const url = new URL(responseUrl);
    if (url.searchParams.get('state') !== expectedState) return null;
    const code = url.searchParams.get('code');
    return code ? { code } : null;
  },
  pkceChallenge: async () => 'challenge',
  randomState: () => 'state-1',
  randomVerifier: () => 'verifier-1',
  readAuthorizationTransaction: mocks.readAuthorizationTransaction,
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

let authPopup: Window;

async function completePopupSignIn(
  code = 'code-1',
  state = 'state-1',
): Promise<void> {
  await waitFor(() => expect(fetch).toHaveBeenCalled());
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: authPopup,
      data: {
        type: 'agentweaver.identity.callback',
        code,
        state,
      },
    }));
  });
}

function Harness() {
  const auth = useAuth();
  const [observedTenant, setObservedTenant] = useState<string | null>(null);
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
      <button
        type="button"
        onClick={() => {
          void auth.apiCall((_accessToken, tenantSelector) => {
            setObservedTenant(tenantSelector);
            return Promise.resolve(tenantSelector);
          });
        }}
      >
        Use confirmed tenant
      </button>
      <button type="button" onClick={() => { void auth.resolveAuthorizationContext('tenant-2'); }}>
        Resolve tenant two
      </button>
      <button type="button" onClick={auth.signOut}>Sign out</button>
      <output>{auth.session?.accessToken ?? 'signed-out'}</output>
      <output>{auth.error ?? 'no-auth-error'}</output>
      <output>context:{auth.authorizationContext?.tenantId ?? 'none'}</output>
      <output>context-fields:{Object.keys(auth.authorizationContext ?? {}).sort().join(',')}</output>
      <output>api-tenant:{observedTenant ?? 'none'}</output>
    </div>
  );
}

describe('Broker refresh coordination', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/projects');
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    vi.clearAllMocks();
    mocks.readAuthorizationTransaction.mockReset().mockReturnValue(null);
    mocks.parseAuthorizationCallbackParameters.mockReset();
    vi.spyOn(gatewayClient, 'getAuthorizationContext').mockRejectedValue(
      new GatewayError(403, { code: 'tenant_context_unavailable' }, 'No active tenant membership.'),
    );
    mocks.exchangeAuthorizationCode.mockResolvedValue(token('access-1', 'refresh-1'));
    authPopup = {
      closed: false,
      location: { replace: vi.fn() },
      close: vi.fn(),
    } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(authPopup);
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => ({
      type: 'opaqueredirect',
      status: 0,
      ok: false,
      url: '',
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
    await completePopupSignIn();
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
    await completePopupSignIn();
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

  it('bootstraps the confirmed tenant and supplies it only to selector-aware API calls', async () => {
    const ownerFetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    const ownerClient = new AgentweaverGatewayClient('/api/v1', ownerFetcher);
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      (accessToken, tenantSelector) => ownerClient.getAuthorizationContext(accessToken, tenantSelector),
    );
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();

    await screen.findByText('context:tenant-1');
    await screen.findByText('context-fields:membershipRevision,tenantId');
    expect(gatewayClient.getAuthorizationContext).toHaveBeenCalledWith('access-1', undefined);
    expect(ownerFetcher).toHaveBeenCalledOnce();
    expect(String(ownerFetcher.mock.calls[0][0])).toBe('/api/v1/authorization/context');
    fireEvent.click(screen.getByRole('button', { name: 'Use confirmed tenant' }));
    await screen.findByText('api-tenant:tenant-1');

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByText('context:none');
  });

  it('revalidates an explicitly selected tenant with the Projects owner', async () => {
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (_accessToken, tenantSelector) => ({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: tenantSelector ?? 'tenant-1',
        membershipRevision: 1,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      }),
    );
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('context:tenant-1');
    await screen.findByText('context-fields:membershipRevision,tenantId');

    fireEvent.click(screen.getByRole('button', { name: 'Resolve tenant two' }));
    await screen.findByText('context:tenant-2');
    expect(gatewayClient.getAuthorizationContext).toHaveBeenLastCalledWith('access-1', 'tenant-2');
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

  it('accepts callback messages only from the exact popup and origin and consumes one code', async () => {
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(fetch).toHaveBeenCalled());

    await act(async () => {
      window.dispatchEvent(new MessageEvent('message', {
        origin: 'https://attacker.test',
        source: authPopup,
        data: {
          type: 'agentweaver.identity.callback',
          code: 'attacker-code',
          state: 'state-1',
        },
      }));
      window.dispatchEvent(new MessageEvent('message', {
        origin: 'https://app.test',
        source: window,
        data: {
          type: 'agentweaver.identity.callback',
          code: 'other-window-code',
          state: 'state-1',
        },
      }));
    });
    await completePopupSignIn();
    await screen.findByText('access-1');

    expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledOnce();
    expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ state: 'state-1', verifier: 'verifier-1' }),
      'code-1',
    );
  });

  it('finishes a valid callback after the popup closes during code exchange', async () => {
    const authorizationResponse = deferred<Response>();
    vi.mocked(fetch).mockReturnValue(authorizationResponse.promise);
    const codeExchange = deferred<ReturnType<typeof token>>();
    mocks.exchangeAuthorizationCode.mockReturnValue(codeExchange.promise);
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledOnce());

    Object.defineProperty(authPopup, 'closed', { configurable: true, value: true });
    await act(async () => {
      window.dispatchEvent(new MessageEvent('message', {
        origin: 'https://app.test',
        source: authPopup,
        data: {
          type: 'agentweaver.identity.callback',
          code: 'code-1',
          state: 'state-1',
        },
      }));
      authorizationResponse.resolve({
        type: 'opaqueredirect',
        status: 0,
        ok: false,
        url: '',
      } as Response);
      await authorizationResponse.promise;
    });

    await waitFor(() => expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledOnce());
    expect(screen.getByText('no-auth-error')).toBeTruthy();
    await act(async () => {
      codeExchange.resolve(token('access-after-popup-close'));
      await codeExchange.promise;
    });
    await screen.findByText('access-after-popup-close');
    expect(screen.getByText('no-auth-error')).toBeTruthy();
    expect(authPopup.close).toHaveBeenCalledOnce();
  });

  it('rejects popup closure when no Broker callback was received', async () => {
    const authorizationResponse = deferred<Response>();
    vi.mocked(fetch).mockReturnValue(authorizationResponse.promise);
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledOnce());

    Object.defineProperty(authPopup, 'closed', { configurable: true, value: true });
    await act(async () => {
      authorizationResponse.resolve({
        type: 'opaqueredirect',
        status: 0,
        ok: false,
        url: '',
      } as Response);
      await authorizationResponse.promise;
    });

    await screen.findByText('Identity Broker sign-in was closed before it completed.');
    expect(mocks.exchangeAuthorizationCode).not.toHaveBeenCalled();
  });

  it('consumes a captured full-page callback from the scrubbed document', async () => {
    const transaction = {
      state: 'state-1',
      verifier: 'verifier-1',
      redirectUri: 'https://app.test/auth/callback',
      binding: null,
    };
    mocks.readAuthorizationTransaction.mockReturnValue(transaction);
    mocks.parseAuthorizationCallbackParameters.mockReturnValue({
      type: 'agentweaver.identity.callback',
      state: 'state-1',
      code: 'code-1',
    });
    window.history.replaceState({}, '', '/auth/callback');
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = '?code=code-1&state=state-1';

    render(<AuthProvider><Harness /></AuthProvider>);

    await screen.findByText('access-1');
    expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledOnce();
    expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledWith(
      expect.anything(),
      transaction,
      'code-1',
    );
    expect(window.location.pathname).toBe('/auth/callback');
    expect(window.location.search).toBe('');
    expect(window.__AGENTWEAVER_IDENTITY_CALLBACK__).toBeUndefined();
    expect(window.open).not.toHaveBeenCalled();
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
        type: 'opaqueredirect',
        status: 0,
        ok: false,
        url: '',
        json: async () => null,
      } as Response);

    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Approve consent' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
    await completePopupSignIn('code-2');

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
