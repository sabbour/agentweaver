import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useEffect, useRef, useState } from 'react';
import { AgentweaverGatewayClient, gatewayClient, GatewayError } from './api';
import type { AuthorizationCallbackMessage, AuthorizationTransaction } from './authProtocol';
import type { PlatformEgressDestinationPage } from './contracts';

const mocks = vi.hoisted(() => ({
  buildAuthorizeUrl: vi.fn((
    _config: unknown,
    transaction: { state: string },
    _challenge: string,
    prompt?: 'none',
  ) => `https://broker.test/authorize?state=${encodeURIComponent(transaction.state)}${prompt ? `&prompt=${prompt}` : ''}`),
  exchangeAuthorizationCode: vi.fn(),
  isConsentPrompt: vi.fn(() => false),
  parseAuthorizationCallbackMessage: (value: unknown) => value as AuthorizationCallbackMessage,
  parseAuthorizationCallbackParameters: vi.fn(),
  readAuthorizationTransaction: vi.fn<() => AuthorizationTransaction | null>(() => null),
  refreshBrokerToken: vi.fn(),
  submitBrokerConsent: vi.fn(),
  storeAuthorizationTransaction: vi.fn(),
}));

vi.mock('./authProtocol', async (importOriginal) => ({
  // The real decoder, so a session gets the name from the ID token the Broker returns.
  displayNameFromIdToken: (await importOriginal<typeof import('./authProtocol')>()).displayNameFromIdToken,
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
    const error = url.searchParams.get('error');
    if (error) return { error, description: url.searchParams.get('error_description') ?? undefined };
    const code = url.searchParams.get('code');
    return code ? { code } : null;
  },
  pkceChallenge: async () => 'challenge',
  randomState: () => 'state-1',
  randomVerifier: () => 'verifier-1',
  readAuthorizationTransaction: mocks.readAuthorizationTransaction,
  refreshBrokerToken: mocks.refreshBrokerToken,
  storeAuthorizationTransaction: mocks.storeAuthorizationTransaction,
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
import { ProjectModelProviderDrafts } from './ProjectModelProviderDrafts';
import { DeploymentByokBindings } from './DeploymentByokBindings';
import { PlatformProviderHostRequests } from './PlatformProviderHostRequests';
import { RunEgressPanel } from './RunEgressPanel';

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
const SIGNED_IN_BEFORE_STORAGE_KEY = 'agentweaver.auth.signed-in-before';

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

async function completeSilentSignIn(
  callback: { code?: string; error?: string },
): Promise<void> {
  await waitFor(() => expect(document.querySelector('iframe[title="Silent sign-in"]')).not.toBeNull());
  const iframe = document.querySelector<HTMLIFrameElement>('iframe[title="Silent sign-in"]')!;
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: iframe.contentWindow,
      data: {
        type: 'agentweaver.identity.callback',
        state: 'state-1',
        ...callback,
      },
    }));
  });
}

async function completePlatformPolicySignIn(
  callback: { code?: string; error?: string } = { code: 'policy-code' },
): Promise<void> {
  await waitFor(() => expect(
    document.querySelector('iframe[title="Platform Policy sign-in"]'),
  ).not.toBeNull());
  const iframe = document.querySelector<HTMLIFrameElement>('iframe[title="Platform Policy sign-in"]')!;
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: iframe.contentWindow,
      data: {
        type: 'agentweaver.identity.callback',
        state: 'state-1',
        ...callback,
      },
    }));
  });
}

function Harness() {
  const auth = useAuth();
  const [observedTenant, setObservedTenant] = useState<string | null>(null);
  // Records the result of ensureSession and which token an unbound call used,
  // so tests can check that a run-bound session does not replace the unbound one.
  const [ensured, setEnsured] = useState('not-ensured');
  const [unboundToken, setUnboundToken] = useState('none');
  const [projectToken, setProjectToken] = useState('none');
  const [projectEgress, setProjectEgress] = useState('not-read');
  const [policyRead, setPolicyRead] = useState('not-read');
  const [concurrentPolicyResult, setConcurrentPolicyResult] = useState('not-started');
  const firstPolicyAbortRef = useRef<AbortController | null>(null);
  const cancelablePolicyAbortRef = useRef<AbortController | null>(null);
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
      <button type="button" onClick={() => {
        void auth.authorize({ projectId: 'project-1', runId: 'run-1' });
      }}>
        Sign in for active run
      </button>
      <button type="button" onClick={() => {
        void auth.authorize({ projectId: 'project-2', runId: 'run-2' });
      }}>
        Start interactive run
      </button>
      <button type="button" onClick={() => { void auth.authorize({ projectId: 'project-1', runId: 'run-1' }); }}>
        Sign in for run
      </button>
      <button
        type="button"
        onClick={() => { void auth.authorize({ projectId: 'project-1' }, undefined, false); }}
      >
        Sign in for project
      </button>
      <button
        type="button"
        onClick={() => { void auth.authorize({ projectId: 'project-1', runId: 'run-1' }, undefined, false); }}
      >
        Sign in for run without activating
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
      <button type="button" onClick={() => {
        void (async () => {
          const identity = await auth.establishPlatformPolicyAccess('tenant-1');
          await auth.getPlatformPolicyRevisionHistory(identity.tenantId);
          setPolicyRead(`${identity.issuer}:${identity.actorId}:${identity.tenantId}`);
        })().catch(() => setPolicyRead('failed'));
      }}>
        Check Platform Policy
      </button>
      <button type="button" onClick={() => {
        const controller = new AbortController();
        cancelablePolicyAbortRef.current = controller;
        void auth.establishPlatformPolicyAccess('tenant-1', controller.signal)
          .then(() => setPolicyRead('cancelable-policy-allowed'), () => setPolicyRead('cancelable-policy-cancelled'));
      }}>
        Check cancelable Platform Policy
      </button>
      <button type="button" onClick={() => cancelablePolicyAbortRef.current?.abort()}>
        Cancel Platform Policy check
      </button>
      <button type="button" onClick={() => {
        void (async () => {
          const identity = await auth.establishPlatformPolicyAccess('tenant-2');
          await auth.getPlatformPolicyRevisionHistory(identity.tenantId);
          setPolicyRead(`${identity.issuer}:${identity.actorId}:${identity.tenantId}`);
        })().catch(() => setPolicyRead('failed'));
      }}>
        Check tenant two Policy
      </button>
      <button type="button" onClick={() => {
        void auth.getPlatformPolicyRevisionHistory('tenant-2')
          .then(() => setPolicyRead('tenant-two-history-ok'), () => setPolicyRead('tenant-two-history-failed'));
      }}>
        Read tenant two Policy history
      </button>
      <button type="button" onClick={() => {
        const firstAbort = new AbortController();
        firstPolicyAbortRef.current = firstAbort;
        const first = auth.establishPlatformPolicyAccess('tenant-1', firstAbort.signal)
          .then(() => 'allowed', () => 'cancelled');
        const second = auth.establishPlatformPolicyAccess('tenant-1')
          .then(() => 'allowed', () => 'denied');
        void Promise.all([first, second]).then(([firstResult, secondResult]) =>
          setConcurrentPolicyResult(`${firstResult}:${secondResult}`));
      }}>
        Start concurrent Policy checks
      </button>
      <button type="button" onClick={() => firstPolicyAbortRef.current?.abort()}>
        Abort first Policy check
      </button>
      <button type="button" onClick={auth.signOut}>Sign out</button>
      <button type="button" onClick={() => { void auth.ensureSession(null); }}>Ensure platform session</button>
      <button
        type="button"
        onClick={() => {
          void auth.ensureSession({ projectId: 'project-1', runId: 'run-1' })
            .then((ok) => setEnsured(ok ? 'ensured' : 'needs-user'));
        }}
      >
        Ensure run session
      </button>
      <button
        type="button"
        onClick={() => { void auth.apiCall(async (accessToken) => { setUnboundToken(accessToken); }, null); }}
      >
        Use unbound session
      </button>
      <button type="button" onClick={() => {
        void auth.apiCall(async (accessToken) => {
          if (accessToken === 'project-access')
            throw new GatewayError(401, { code: 'unauthorized' }, 'expired');
          setProjectToken(accessToken);
        }, { projectId: 'project-1' }).catch(() => setProjectToken('failed'));
      }}>
        Use project session
      </button>
      <button type="button" onClick={() => {
        void auth.apiCall(async (accessToken) => {
          const context = await gatewayClient.getAuthorizationContext(accessToken);
          await gatewayClient.readRunEgress(
            accessToken,
            'run-1',
            { limit: 50, cursor: null },
            context.tenantId,
          );
          setProjectEgress(`${accessToken}:${context.tenantId}`);
        }, { projectId: 'project-1' }).catch(() => setProjectEgress('failed'));
      }}>
        Read project egress
      </button>
      <output>ensure:{ensured}</output>
      <output>unbound:{unboundToken}</output>
      <output>project:{projectToken}</output>
      <output>egress:{projectEgress}</output>
      <output>{auth.session?.accessToken ?? 'signed-out'}</output>
      <output>name:{auth.session?.displayName ?? 'none'}</output>
      <output>{auth.restoringSession ? 'restoring' : 'not-restoring'}</output>
      <output>{auth.error ?? 'no-auth-error'}</output>
      <output>context:{auth.authorizationContext?.tenantId ?? 'none'}</output>
      <output>actor:{auth.authorizationContext?.actorId ?? 'none'}</output>
      <output>issuer:{auth.authorizationContext?.issuer ?? 'none'}</output>
      <output>context-fields:{Object.keys(auth.authorizationContext ?? {}).sort().join(',')}</output>
      <output>api-tenant:{observedTenant ?? 'none'}</output>
      <output>policy:{policyRead}</output>
      <output>policy-concurrent:{concurrentPolicyResult}</output>
    </div>
  );
}

function RunEgressAuthHarness() {
  const auth = useAuth();
  const [showEgress, setShowEgress] = useState(false);
  const [activeToken, setActiveToken] = useState('signed-out');
  return (
    <div>
      <button type="button" onClick={() => { void auth.authorize(null); }}>Sign in for unbound access</button>
      <button type="button" onClick={() => setShowEgress(true)}>Show run egress</button>
      <button
        type="button"
        onClick={() => {
          void auth.apiCall(async (accessToken) => {
            if (accessToken === 'access-1')
              throw new GatewayError(401, { code: 'unauthorized' }, 'Refresh required.');
            setActiveToken(accessToken);
          });
        }}
      >
        Refresh active session
      </button>
      <output>active-token:{activeToken}</output>
      <output>active-session:{auth.session?.accessToken ?? 'signed-out'}</output>
      {showEgress && <RunEgressPanel projectId="project-1" runId="run-1" />}
    </div>
  );
}

describe('Broker refresh coordination', () => {
  it('restores once from a non-secret tab marker in a hidden frame without opening a popup', async () => {
    window.sessionStorage.setItem(SIGNED_IN_BEFORE_STORAGE_KEY, '1');
    render(<AuthProvider><Harness /></AuthProvider>);

    expect(await screen.findByText('restoring')).toBeTruthy();
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenCalledOnce());
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: null }),
      'challenge',
      'none',
    );
    expect(window.open).not.toHaveBeenCalled();
    await completeSilentSignIn({ code: 'silent-code' });

    await screen.findByText('access-1');
    expect(window.sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY)).toBe('1');
    const sessionValues = Array.from(
      { length: window.sessionStorage.length },
      (_, index) => window.sessionStorage.getItem(window.sessionStorage.key(index) ?? '') ?? '',
    );
    expect(sessionValues).toEqual(['1']);
    expect(window.localStorage?.length ?? 0).toBe(0);
    expect(document.cookie).not.toContain('access-1');
    expect(document.cookie).not.toContain('refresh-1');
    expect(window.location.search).not.toMatch(/[?&](access_token|refresh_token)=/);
    expect(fetch).not.toHaveBeenCalled();
    expect(document.querySelector('iframe[title="Silent sign-in"]')).toBeNull();
  });

  it('restores the unbound session on a run page so the shell keeps working', async () => {
    window.history.replaceState({}, '', '/projects/project-1/runs/run-1');
    window.sessionStorage.setItem(SIGNED_IN_BEFORE_STORAGE_KEY, '1');
    render(<AuthProvider><Harness /></AuthProvider>);

    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenCalledOnce());
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: null }),
      'challenge',
      'none',
    );
    await completeSilentSignIn({ code: 'silent-code' });
    await screen.findByText('access-1');
    // Shell reads use the unbound session, so it must be held after a run-page reload.
    fireEvent.click(screen.getByRole('button', { name: 'Use unbound session' }));
    await screen.findByText('unbound:access-1');
  });

  it('returns to the sign-in screen after one silent error and clears the marker', async () => {
    window.sessionStorage.setItem(SIGNED_IN_BEFORE_STORAGE_KEY, '1');
    render(<AuthProvider><Harness /></AuthProvider>);
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenCalledOnce());

    await completeSilentSignIn({ error: 'login_required' });

    await waitFor(() => {
      expect(window.sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY)).toBeNull();
      expect(document.querySelector('iframe[title="Silent sign-in"]')).toBeNull();
    });
    expect(screen.getByText('signed-out')).toBeTruthy();
    expect(screen.getByText('no-auth-error')).toBeTruthy();
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledOnce();
    expect(window.open).not.toHaveBeenCalled();
  });

  it('clears the marker on sign-out so a later mount does not restore silently', async () => {
    const firstMount = render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    expect(window.sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY)).toBe('1');

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByText('signed-out');
    expect(window.sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY)).toBeNull();
    firstMount.unmount();

    render(<AuthProvider><Harness /></AuthProvider>);
    await screen.findByText('signed-out');
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledOnce();
    expect(window.open).toHaveBeenCalledOnce();
    expect(document.querySelector('iframe[title="Silent sign-in"]')).toBeNull();
  });

  beforeEach(() => {
    window.history.replaceState({}, '', '/projects');
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    window.localStorage?.clear();
    window.sessionStorage.clear();
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

  // A run-bound session must be added next to the unbound one, without a popup,
  // and unbound calls must keep using the original token.
  it('gets a run session silently and keeps the unbound session usable', async () => {
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('access-run', 'refresh-run'));

    fireEvent.click(screen.getByRole('button', { name: 'Ensure run session' }));
    // The run session must be requested silently (prompt=none) for this exact project and run.
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: { projectId: 'project-1', runId: 'run-1' } }),
      'challenge',
      'none',
    ));
    await completeSilentSignIn({ code: 'run-code' });

    await screen.findByText('ensure:ensured');
    await screen.findByText('access-run');
    fireEvent.click(screen.getByRole('button', { name: 'Use unbound session' }));
    // Unbound calls still use the first token, and only the first sign-in opened a popup.
    await screen.findByText('unbound:access-1');
    expect(window.open).toHaveBeenCalledOnce();
  });

  it('keeps a project-only session separate and refreshes that exact binding', async () => {
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('project-access', 'project-refresh'));
    const exchangesBeforeProjectSignIn = mocks.exchangeAuthorizationCode.mock.calls.length;

    fireEvent.click(screen.getByRole('button', { name: 'Sign in for project' }));
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: { projectId: 'project-1' } }),
      'challenge',
    ));
    await completePopupSignIn('project-code');
    await waitFor(() => expect(mocks.exchangeAuthorizationCode)
      .toHaveBeenCalledTimes(exchangesBeforeProjectSignIn + 1));
    await act(async () => { await Promise.resolve(); });
    await screen.findByText('access-1');

    mocks.refreshBrokerToken.mockResolvedValueOnce(token('project-access-refreshed'));
    fireEvent.click(screen.getByRole('button', { name: 'Use project session' }));
    await screen.findByText('project:project-access-refreshed');
    expect(screen.getByText('access-1')).toBeTruthy();

    expect(mocks.refreshBrokerToken).toHaveBeenCalledWith(
      expect.anything(),
      'project-refresh',
    );
    expect(mocks.refreshBrokerToken).not.toHaveBeenCalledWith(
      expect.anything(),
      'refresh-1',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Use unbound session' }));
    await screen.findByText('unbound:access-1');
  });

  it('finishes project egress sign-in against the current unbound session scope', async () => {
    render(<AuthProvider><RunEgressAuthHarness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for unbound access' }));
    await completePopupSignIn();
    expect(await screen.findByText('active-session:access-1')).toBeTruthy();

    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(
      token('project-access', 'project-refresh'),
    );
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (accessToken, tenantSelector) => ({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: tenantSelector ?? 'tenant-1',
        membershipRevision: 1,
        boundProjectId: accessToken === 'project-access' ? 'project-1' : null,
        boundRunId: null,
        effectiveAuthority: [],
      }),
    );
    const readEgress = vi.spyOn(gatewayClient, 'readRunEgress').mockResolvedValue({
      runId: 'run-1',
      coverage: 'Unavailable',
      delayedUntil: null,
      items: [],
      nextCursor: null,
    });

    fireEvent.click(screen.getByRole('button', { name: 'Show run egress' }));
    await completeSilentSignIn({ error: 'login_required' });
    const projectSignIn = await screen.findByRole('button', { name: 'Sign in for this project' });
    await waitFor(() => expect(projectSignIn.hasAttribute('disabled')).toBe(false));
    fireEvent.click(projectSignIn);
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: { projectId: 'project-1' } }),
      'challenge',
    ));

    mocks.refreshBrokerToken.mockResolvedValueOnce(token('access-2', 'refresh-2'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh active session' }));
    expect(await screen.findByText('active-token:access-2')).toBeTruthy();
    await completePopupSignIn('project-code');

    expect(await screen.findByText(
      'Coverage unavailable. This view shows stored records only.',
    )).toBeTruthy();
    expect(readEgress).toHaveBeenCalledWith(
      'project-access',
      'run-1',
      { limit: 50, cursor: null, signal: expect.any(AbortSignal) },
      'tenant-1',
    );
  });

  it('resolves and reads egress with the refreshed exact project session while unbound stays active', async () => {
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('project-access', 'project-refresh'));
    const projectSignInExchangeCount = mocks.exchangeAuthorizationCode.mock.calls.length;

    fireEvent.click(screen.getByRole('button', { name: 'Sign in for project' }));
    await waitFor(() => expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: { projectId: 'project-1' } }),
      'challenge',
    ));
    await completePopupSignIn('project-code');
    await waitFor(() => expect(mocks.exchangeAuthorizationCode)
      .toHaveBeenCalledTimes(projectSignInExchangeCount + 1));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('access-1')).toBeTruthy();

    mocks.refreshBrokerToken.mockResolvedValueOnce(token('project-access-refreshed'));
    vi.mocked(gatewayClient.getAuthorizationContext)
      .mockRejectedValueOnce(new GatewayError(401, { code: 'expired' }, 'Expired.'))
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      });
    const readEgress = vi.spyOn(gatewayClient, 'readRunEgress').mockResolvedValue({
      runId: 'run-1',
      coverage: 'Unavailable',
      delayedUntil: null,
      items: [],
      nextCursor: null,
    });

    fireEvent.click(screen.getByRole('button', { name: 'Read project egress' }));
    expect(await screen.findByText('egress:project-access-refreshed:tenant-1')).toBeTruthy();
    expect(screen.getByText('access-1')).toBeTruthy();
    expect(mocks.refreshBrokerToken).toHaveBeenCalledWith(
      expect.anything(),
      'project-refresh',
    );
    expect(readEgress).toHaveBeenCalledWith(
      'project-access-refreshed',
      'run-1',
      { limit: 50, cursor: null },
      'tenant-1',
    );
  });

  it('loads project drafts after first authorization while context bootstrap runs', async () => {
    const contextTenants: (string | null)[] = [];
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (_accessToken, tenantSelector) => {
        contextTenants.push(tenantSelector ?? null);
        await new Promise((resolve) => window.setTimeout(resolve, 80));
        return {
          contractVersion: 1,
          issuer: 'projects-config',
          actorId: 'actor-1',
          tenantId: tenantSelector ?? 'tenant-1',
          membershipRevision: 1,
          boundProjectId: 'project-1',
          boundRunId: null,
          effectiveAuthority: [],
        };
      },
    );
    const listDrafts = vi.spyOn(gatewayClient, 'listProjectByokProviderDrafts')
      .mockResolvedValue({ providers: [], nextAfterProviderId: null });
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('project-access', 'project-refresh'));

    render(
      <AuthProvider>
        <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />
      </AuthProvider>,
    );
    await completeSilentSignIn({ code: 'project-code' });

    expect(await screen.findByText('No provider drafts are set up for this project.'))
      .toBeTruthy();
    expect(listDrafts).toHaveBeenCalledOnce();
    expect(contextTenants).toContain('tenant-1');
  });

  it('gets an independent unbound session for policy reads without replacing the active run session', async () => {
    mocks.exchangeAuthorizationCode
      .mockResolvedValueOnce(token('run-token', 'refresh-run'))
      .mockResolvedValueOnce(token('policy-token', 'refresh-policy'));
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (accessToken, tenantSelector) => ({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: tenantSelector ?? 'tenant-1',
        membershipRevision: 1,
        boundProjectId: accessToken === 'run-token' ? 'project-1' : null,
        boundRunId: accessToken === 'run-token' ? 'run-1' : null,
        effectiveAuthority: [],
      }),
    );
    const historyRead = vi.spyOn(gatewayClient, 'getPlatformPolicyRevisionHistory')
      .mockResolvedValue({
        scopeType: 'platform',
        scopeId: 'default',
        policyId: null,
        snapshotRevision: null,
        items: [],
        nextCursor: null,
      });

    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run' }));
    await completePopupSignIn();
    await screen.findByText('run-token');

    fireEvent.click(screen.getByRole('button', { name: 'Check Platform Policy' }));
    await completePlatformPolicySignIn();
    await screen.findByText('policy:projects-config:actor-1:tenant-1');

    expect(historyRead).toHaveBeenCalledWith('policy-token', 'tenant-1', {});
    expect(screen.getByText('run-token')).toBeTruthy();
    expect(window.open).toHaveBeenCalledOnce();
    expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ binding: null }),
      'challenge',
      'none',
    );
  });

  it('keeps a shared silent policy sign-in alive when one caller aborts', async () => {
    mocks.exchangeAuthorizationCode
      .mockResolvedValueOnce(token('run-token', 'refresh-run'))
      .mockResolvedValueOnce(token('policy-token', 'refresh-policy'));
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (accessToken, tenantSelector) => ({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: tenantSelector ?? 'tenant-1',
        membershipRevision: 1,
        boundProjectId: accessToken === 'run-token' ? 'project-1' : null,
        boundRunId: accessToken === 'run-token' ? 'run-1' : null,
        effectiveAuthority: [],
      }),
    );
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run' }));
    await completePopupSignIn();
    await screen.findByText('run-token');

    fireEvent.click(screen.getByRole('button', { name: 'Start concurrent Policy checks' }));
    await waitFor(() => expect(
      document.querySelector('iframe[title="Platform Policy sign-in"]'),
    ).not.toBeNull());
    fireEvent.click(screen.getByRole('button', { name: 'Abort first Policy check' }));
    await completePlatformPolicySignIn();

    await screen.findByText('policy-concurrent:cancelled:allowed');
    expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledTimes(2);
    expect(window.open).toHaveBeenCalledOnce();
    expect(document.querySelector('iframe[title="Platform Policy sign-in"]')).toBeNull();
  });

  it.each([
    ['late success', 'success', true],
    ['late denial', 'denial', true],
    ['early success', 'success', false],
    ['early denial', 'denial', false],
  ] as const)('keeps the newer tenant access after an older %s response', async (_label, resultKind, late) => {
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
    let resolveOld!: (page: {
      scopeType: 'platform';
      scopeId: 'default';
      policyId: null;
      snapshotRevision: null;
      items: [];
      nextCursor: null;
    }) => void;
    let rejectOld!: (reason: unknown) => void;
    const oldResponse = new Promise<{
      scopeType: 'platform';
      scopeId: 'default';
      policyId: null;
      snapshotRevision: null;
      items: [];
      nextCursor: null;
    }>((resolve, reject) => {
      resolveOld = resolve;
      rejectOld = reject;
    });
    const emptyPage = {
      scopeType: 'platform' as const,
      scopeId: 'default' as const,
      policyId: null,
      snapshotRevision: null,
      items: [] as [],
      nextCursor: null,
    };
    const historyRead = vi.spyOn(gatewayClient, 'getPlatformPolicyRevisionHistory')
      .mockImplementation((_accessToken, tenantSelector) =>
        tenantSelector === 'tenant-1' ? oldResponse : Promise.resolve(emptyPage));

    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    fireEvent.click(screen.getByRole('button', { name: 'Check Platform Policy' }));
    await waitFor(() => expect(historyRead).toHaveBeenCalledWith('access-1', 'tenant-1', {}));

    const settleOldResponse = () => {
      if (resultKind === 'success') resolveOld(emptyPage);
      else rejectOld(new GatewayError(403, { code: 'forbidden' }, 'Denied.'));
    };
    if (!late) {
      await act(async () => { settleOldResponse(); });
    }

    fireEvent.click(screen.getByRole('button', { name: 'Check tenant two Policy' }));
    await screen.findByText('policy:projects-config:actor-1:tenant-2');
    if (late) {
      await act(async () => { settleOldResponse(); });
    }

    fireEvent.click(screen.getByRole('button', { name: 'Read tenant two Policy history' }));
    await screen.findByText('policy:tenant-two-history-ok');
    expect(historyRead).toHaveBeenLastCalledWith('access-1', 'tenant-2', {});
  });

  it.each(['success', 'failure', 'timeout', 'cancel'] as const)(
    'keeps interactive PKCE and consent polling usable during isolated Policy %s',
    async (policyOutcome) => {
      mocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) =>
        token(code === 'policy-code'
          ? 'policy-token'
          : code === 'run-2-code'
            ? 'run2-token'
            : 'run-token'));
      vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
        async (accessToken, tenantSelector) => ({
          contractVersion: 1,
          issuer: 'projects-config',
          actorId: 'actor-1',
          tenantId: tenantSelector ?? 'tenant-1',
          membershipRevision: 1,
          boundProjectId: accessToken === 'policy-token'
            ? null
            : accessToken === 'run2-token' ? 'project-2' : 'project-1',
          boundRunId: accessToken === 'policy-token'
            ? null
            : accessToken === 'run2-token' ? 'run-2' : 'run-1',
          effectiveAuthority: [],
        }),
      );
      vi.spyOn(gatewayClient, 'getPlatformPolicyRevisionHistory').mockResolvedValue({
        scopeType: 'platform',
        scopeId: 'default',
        policyId: null,
        snapshotRevision: null,
        items: [],
        nextCursor: null,
      });

      render(<AuthProvider><Harness /></AuthProvider>);
      fireEvent.click(screen.getByRole('button', { name: 'Sign in for active run' }));
      await completePopupSignIn('run-1-code');
      await screen.findByText('run-token');

      const pollsBeforeInteractive = vi.mocked(fetch).mock.calls.length;
      fireEvent.click(screen.getByRole('button', { name: 'Start interactive run' }));
      await waitFor(() => expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThan(pollsBeforeInteractive));
      await waitFor(() => expect(mocks.storeAuthorizationTransaction).toHaveBeenCalledTimes(2));

      if (policyOutcome === 'cancel') {
        fireEvent.click(screen.getByRole('button', { name: 'Check cancelable Platform Policy' }));
      } else {
        fireEvent.click(screen.getByRole('button', { name: 'Check Platform Policy' }));
      }
      await waitFor(() => expect(
        document.querySelector('iframe[title="Platform Policy sign-in"]'),
      ).not.toBeNull());

      if (policyOutcome === 'success') {
        await completePlatformPolicySignIn();
        await screen.findByText('policy:projects-config:actor-1:tenant-1');
      } else if (policyOutcome === 'failure') {
        await completePlatformPolicySignIn({ error: 'login_required' });
        await screen.findByText('policy:failed');
      } else if (policyOutcome === 'timeout') {
        await act(async () => new Promise((resolve) => window.setTimeout(resolve, 15_100)));
        await screen.findByText('policy:failed');
      } else {
        fireEvent.click(screen.getByRole('button', { name: 'Cancel Platform Policy check' }));
        await screen.findByText('policy:cancelable-policy-cancelled');
      }

      expect(document.querySelector('iframe[title="Platform Policy sign-in"]')).toBeNull();
      expect(screen.getByText('run-token')).toBeTruthy();
      expect(mocks.storeAuthorizationTransaction).toHaveBeenCalledTimes(2);
      expect(mocks.storeAuthorizationTransaction.mock.calls[1][0]).toMatchObject({
        binding: { projectId: 'project-2', runId: 'run-2' },
      });

      await completePopupSignIn('run-2-code');
      await screen.findByText('run2-token');
      expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThan(pollsBeforeInteractive);
    },
    25_000,
  );

  it('stores an interactive run session without activating it', async () => {
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');

    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('access-run', 'refresh-run'));
    const fetchCallsBeforeRunSignIn = vi.mocked(fetch).mock.calls.length;
    const exchangesBeforeRunSignIn = mocks.exchangeAuthorizationCode.mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run without activating' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(fetchCallsBeforeRunSignIn + 1));
    await completePopupSignIn();
    await waitFor(() => expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledTimes(exchangesBeforeRunSignIn + 1));
    await act(async () => { await Promise.resolve(); });

    await screen.findByText('access-1');
    fireEvent.click(screen.getByRole('button', { name: 'Ensure run session' }));
    await screen.findByText('ensure:ensured');
    await screen.findByText('access-run');
  });

  it('takes the display name from the ID token and keeps it for run sessions and refreshes', async () => {
    const claims = btoa(JSON.stringify({ sub: 'user-1', name: 'Ada Lovelace' })).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce({ ...token('access-1', 'refresh-1'), id_token: `e30.${claims}.sig` });
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');
    expect(screen.getByText('name:Ada Lovelace')).toBeTruthy();

    // A run-bound token without an ID token belongs to the same person, so the name stays.
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('access-run', 'refresh-run'));
    fireEvent.click(screen.getByRole('button', { name: 'Ensure run session' }));
    await completeSilentSignIn({ code: 'run-code' });
    await screen.findByText('access-run');
    expect(screen.getByText('name:Ada Lovelace')).toBeTruthy();
  });

  it('keeps the current session when the silent run sign-in needs the user', async () => {    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');

    fireEvent.click(screen.getByRole('button', { name: 'Ensure run session' }));
    await completeSilentSignIn({ error: 'consent_required' });

    await screen.findByText('ensure:needs-user');
    expect(screen.getByText('access-1')).toBeTruthy();
    expect(screen.getByText('no-auth-error')).toBeTruthy();
    expect(window.sessionStorage.getItem(SIGNED_IN_BEFORE_STORAGE_KEY)).toBe('1');
    expect(window.open).toHaveBeenCalledOnce();
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

  it('keeps the same session and confirmed tenant when the platform queue persistently returns 401', async () => {
    vi.mocked(gatewayClient.getAuthorizationContext).mockResolvedValue({
      contractVersion: 1,
      issuer: 'projects-config',
      actorId: 'actor-1',
      tenantId: 'tenant-1',
      membershipRevision: 1,
      boundProjectId: null,
      boundRunId: null,
      effectiveAuthority: [],
    });
    mocks.refreshBrokerToken.mockResolvedValueOnce(token('access-2', 'refresh-2'));
    const queue = vi.spyOn(gatewayClient, 'listPlatformEgressDestinations')
      .mockRejectedValue(new GatewayError(401, { code: 'unauthorized' }, 'Unauthorized'));
    render(<AuthProvider><Harness /><PlatformProviderHostRequests /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();

    await screen.findByText('Sign in to view provider host requests.');
    expect(queue).toHaveBeenCalledOnce();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Refresh provider host requests' }));
    });
    expect(screen.getByText('Sign in to view provider host requests.')).toBeTruthy();
    expect(queue.mock.calls).toEqual([
      ['access-1', 'tenant-1', null, expect.any(AbortSignal)],
      ['access-1', 'tenant-1', null, expect.any(AbortSignal)],
    ]);
    expect(mocks.refreshBrokerToken).not.toHaveBeenCalled();
    expect(gatewayClient.getAuthorizationContext).toHaveBeenCalledOnce();
    expect(screen.getByText('access-1')).toBeTruthy();
    expect(screen.getByText('context:tenant-1')).toBeTruthy();
    mocks.refreshBrokerToken.mockReset();
  });

  it.each(['queue control', 'outside control'])('renews the session during an append with focus on the %s', async (focused) => {
    vi.mocked(gatewayClient.getAuthorizationContext).mockResolvedValue({
      contractVersion: 1,
      issuer: 'projects-config',
      actorId: 'actor-1',
      tenantId: 'tenant-1',
      membershipRevision: 1,
      boundProjectId: null,
      boundRunId: null,
      effectiveAuthority: [],
    });
    const append = deferred<PlatformEgressDestinationPage>();
    const queue = vi.spyOn(gatewayClient, 'listPlatformEgressDestinations')
      .mockResolvedValueOnce({ destinations: [], nextCursor: 'old-page' })
      .mockReturnValueOnce(append.promise)
      .mockResolvedValueOnce({ destinations: [], nextCursor: null });
    mocks.refreshBrokerToken.mockResolvedValueOnce(token('access-2', 'refresh-2'));
    render(<AuthProvider><Harness /><PlatformProviderHostRequests /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    const heading = screen.getByText('Platform-wide provider host requests');
    const loadMore = await screen.findByRole('button', { name: 'Load more' });
    loadMore.focus();
    fireEvent.click(loadMore);
    const outside = screen.getByRole('button', { name: 'Call twice' });
    if (focused === 'outside control') outside.focus();
    fireEvent.click(outside);

    await screen.findByText('access-2');
    await screen.findByText('No provider host requests were returned.');
    expect(mocks.refreshBrokerToken).toHaveBeenCalledOnce();
    expect(screen.getByText('Platform-wide provider host requests')).toBe(heading);
    expect(document.activeElement).toBe(focused === 'queue control' ? heading : outside);
    expect(queue.mock.calls.map(([access, tenant, cursor]) => [access, tenant, cursor])).toEqual([
      ['access-1', 'tenant-1', null],
      ['access-1', 'tenant-1', 'old-page'],
      ['access-2', 'tenant-1', null],
    ]);
    expect(queue.mock.calls[1][3]?.aborted).toBe(true);
    await act(async () => append.resolve({ destinations: [], nextCursor: 'stale-page' }));
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('restarts the bindings read when its 401 refreshes the active session', async () => {
    mocks.refreshBrokerToken.mockResolvedValueOnce(token('access-2', 'refresh-2'));
    const getBindings = vi.spyOn(gatewayClient, 'getByokDeploymentBindings')
      .mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'expired'))
      .mockResolvedValueOnce({ revision: 'refreshed', bindings: [] });
    render(<AuthProvider><Harness /><DeploymentByokBindings /></AuthProvider>);
    fireEvent.click(screen.getAllByRole('button', { name: 'Sign in' })[0]);
    await completePopupSignIn();
    await screen.findByText('access-1');
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    await screen.findByText('Projects startup revision: refreshed');
    expect(getBindings.mock.calls).toEqual([['access-1', 'tenant-a'], ['access-2', 'tenant-a']]);
    expect(mocks.refreshBrokerToken).toHaveBeenCalledOnce();
  });

  it('does not restart a bindings read after sign-out and a new sign-in', async () => {
    const old = deferred<{ revision: string; bindings: [] }>();
    const getBindings = vi.spyOn(gatewayClient, 'getByokDeploymentBindings').mockReturnValueOnce(old.promise);
    render(<AuthProvider><Harness /><DeploymentByokBindings /></AuthProvider>);
    fireEvent.click(screen.getAllByRole('button', { name: 'Sign in' })[0]);
    await completePopupSignIn();
    await screen.findByText('access-1');
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('other', 'refresh-other'));
    fireEvent.click(screen.getAllByRole('button', { name: 'Sign in' })[0]);
    await completePopupSignIn();
    await screen.findByText('other');
    await act(async () => old.resolve({ revision: 'old', bindings: [] }));
    expect(screen.queryByText('Projects startup revision: old')).toBeNull();
    expect(getBindings).toHaveBeenCalledTimes(1);
  });

  it('does not restart a bindings read when another unbound sign-in replaces the active identity', async () => {
    const identity = (accessToken: string, subject: string) => ({
      ...token(accessToken, `refresh-${subject}`),
      id_token: `e30.${btoa(JSON.stringify({ sub: subject, name: subject }))}.sig`,
    });
    const replacement = deferred<ReturnType<typeof identity>>();
    mocks.exchangeAuthorizationCode
      .mockReturnValueOnce(replacement.promise)
      .mockResolvedValueOnce(identity('access-1', 'first-user'));
    const old = deferred<{ revision: string; bindings: [] }>();
    const getBindings = vi.spyOn(gatewayClient, 'getByokDeploymentBindings').mockReturnValue(old.promise);
    render(<AuthProvider><Harness /><DeploymentByokBindings /></AuthProvider>);

    fireEvent.click(screen.getAllByRole('button', { name: 'Sign in' })[0]);
    await completePopupSignIn('replacement-code');
    await waitFor(() => expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledTimes(1));
    // Keep the first exchange pending while a second sign-in becomes active.
    fireEvent.click(screen.getByRole('button', { name: 'Ensure platform session' }));
    await completeSilentSignIn({ code: 'active-code' });
    await screen.findByText('access-1');
    expect(screen.getByText('name:first-user')).toBeTruthy();
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), { target: { value: 'tenant-a' } });
    fireEvent.click(screen.getByRole('button', { name: 'Load bindings' }));
    expect(getBindings.mock.calls).toEqual([['access-1', 'tenant-a']]);

    await act(async () => replacement.resolve(identity('other', 'second-user')));
    await screen.findByText('other');
    expect(screen.getByText('name:second-user')).toBeTruthy();
    expect(getBindings).toHaveBeenCalledTimes(1);
    await act(async () => old.resolve({ revision: 'old', bindings: [] }));
    expect(screen.queryByText('Projects startup revision: old')).toBeNull();
    expect(getBindings.mock.calls).toEqual([['access-1', 'tenant-a']]);
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
    await screen.findByText('actor:actor-1');
    await screen.findByText('issuer:projects-config');
    await screen.findByText('context-fields:actorId,issuer,membershipRevision,tenantId');
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
    await screen.findByText('context-fields:actorId,issuer,membershipRevision,tenantId');

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

    await screen.findByText('The sign-in window closed before sign-in finished.');
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
    render(<AuthProvider><Harness /></AuthProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completePopupSignIn();
    await screen.findByText('access-1');

    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('access-after-consent'));
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

    const fetchCallsBeforeRunSignIn = vi.mocked(fetch).mock.calls.length;
    fireEvent.click(screen.getByRole('button', { name: 'Sign in for run without activating' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(fetchCallsBeforeRunSignIn + 1));
    fireEvent.click(await screen.findByRole('button', { name: 'Approve consent' }));
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(fetchCallsBeforeRunSignIn + 2));
    await completePopupSignIn('code-2');

    await waitFor(() => expect(mocks.exchangeAuthorizationCode).toHaveBeenCalledTimes(2));
    await act(async () => { await Promise.resolve(); });
    await screen.findByText('access-1');
    fireEvent.click(screen.getByRole('button', { name: 'Ensure run session' }));
    await screen.findByText('ensure:ensured');
    await screen.findByText('access-after-consent');
    expect(mocks.submitBrokerConsent).toHaveBeenCalledOnce();
    expect(mocks.buildAuthorizeUrl).toHaveBeenCalledTimes(3);
    expect(mocks.buildAuthorizeUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({
        binding: { projectId: 'project-1', runId: 'run-1' },
      }),
      'challenge',
    );
    expect(window.location.pathname).toBe('/projects/project-1/runs/run-1');
  });

  it('completes in-flight detail refresh and save across real AuthContext token renewal', async () => {
    vi.mocked(gatewayClient.getAuthorizationContext).mockImplementation(
      async (_accessToken, tenantSelector) => ({
        contractVersion: 1,
        issuer: 'projects-config',
        actorId: 'actor-1',
        tenantId: tenantSelector ?? 'tenant-1',
        membershipRevision: 1,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      }),
    );
    const baseProvider = {
      id: 'provider-1',
      name: 'Customer model',
      type: 'openai',
      base_url: 'https://provider.example/v1',
      model: 'model-a',
      wire_api: null,
      azure_api_version: null,
      reasoning_efforts: ['low'],
      prompt_capacity_tokens: null,
      scope: 'project' as const,
      projectId: 'project-1',
      state: 'Draft' as const,
      revision: 1,
      configurationHash: 'a'.repeat(64),
      has_api_key: false,
      is_active: false,
      hasCredential: false,
      egress: {
        host: 'provider.example',
        port: 443,
        destinationState: null,
        canApprove: false,
        deliveryStatus: 'pending' as const,
      },
    };
    vi.spyOn(gatewayClient, 'listProjectByokProviderDrafts').mockResolvedValue({
      providers: [baseProvider],
      nextAfterProviderId: null,
    });
    const readDraft = vi.spyOn(gatewayClient, 'getProjectByokProviderDraft')
      .mockResolvedValueOnce(baseProvider)
      .mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'expired'))
      .mockResolvedValueOnce({
        ...baseProvider,
        egress: {
          host: 'provider.example',
          port: 443,
          destinationState: 'requested',
          canApprove: false,
          deliveryStatus: 'delivered',
        },
      })
      .mockResolvedValueOnce({
        ...baseProvider,
        name: 'Renamed model',
        revision: 2,
      });
    const updateDraft = vi.spyOn(gatewayClient, 'updateProjectByokProviderDraft')
      .mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'expired'))
      .mockResolvedValueOnce({
        ...baseProvider,
        name: 'Renamed model',
        revision: 2,
      });
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('project-access-1', 'project-refresh-1'));
    mocks.refreshBrokerToken
      .mockResolvedValueOnce(token('project-access-2', 'project-refresh-2'))
      .mockResolvedValueOnce(token('project-access-3', 'project-refresh-3'));

    render(
      <AuthProvider>
        <ProjectModelProviderDrafts projectId="project-1" tenantSelector="tenant-1" />
      </AuthProvider>,
    );
    await completeSilentSignIn({ code: 'project-code' });
    fireEvent.click(await screen.findByRole('button', { name: 'Customer model' }));
    expect(await screen.findByText('Waiting to send')).toBeTruthy();

    // Detail Refresh status hits 401 -> real AuthContext token refresh -> retries with new token.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Received by Environment')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Refresh status' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Edit metadata' }) as HTMLButtonElement).disabled).toBe(false);
    expect(readDraft.mock.calls.slice(1, 3).map(([access]) => access)).toEqual([
      'project-access-1',
      'project-access-2',
    ]);

    // Save metadata hits 401 -> real AuthContext token refresh -> retries with same operationId.
    fireEvent.click(screen.getByRole('button', { name: 'Edit metadata' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), {
      target: { value: 'Renamed model' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    expect(await screen.findByText('Provider draft saved.')).toBeTruthy();
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Edit metadata' }) as HTMLButtonElement).disabled).toBe(false),
    );
    expect(screen.getByText('Renamed model')).toBeTruthy();
    expect(updateDraft).toHaveBeenCalledTimes(2);
    expect(updateDraft.mock.calls[0][0]).toBe('project-access-2');
    expect(updateDraft.mock.calls[1][0]).toBe('project-access-3');
    expect(updateDraft.mock.calls[1][3].operationId).toBe(updateDraft.mock.calls[0][3].operationId);
    expect(mocks.refreshBrokerToken).toHaveBeenCalledTimes(2);
  });

  it('apiCallOnce pre-refreshes, reconfirms context, checks identity after await, and never auto-retries on 401', async () => {
    vi.spyOn(gatewayClient, 'getAuthorizationContext')
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      })
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 2,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      });
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce({
      ...token('expiring-access', 'refresh-1'),
      expires_in: 1,
    });
    mocks.refreshBrokerToken.mockResolvedValueOnce(token('refreshed-access', 'refresh-2'));

    let authValue: ReturnType<typeof useAuth> | null = null;
    function Probe() {
      const auth = useAuth();
      useEffect(() => {
        authValue = auth;
      }, [auth]);
      return null;
    }

    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    const authorizePromise = authValue!.authorize({ projectId: 'project-1' }, authPopup);
    await completePopupSignIn('code-1');
    await authorizePromise;
    await waitFor(() => expect(authValue!.authorizationContext?.tenantId).toBe('tenant-1'));

    // Pre-refresh + context reconfirmation + no 401 retry when operation returns 401.
    const operation = vi.fn().mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'Expired'));
    await expect(
      authValue!.apiCallOnce(operation, { projectId: 'project-1' }, {
        tenantId: 'tenant-1',
        actorId: 'actor-1',
        issuer: 'broker',
      }),
    ).rejects.toMatchObject({ status: 401 });

    expect(mocks.refreshBrokerToken).toHaveBeenCalledTimes(1);
    expect(operation).toHaveBeenCalledTimes(1);
    expect(operation).toHaveBeenCalledWith('refreshed-access', 'tenant-1');

    // Expected identity mismatch fails closed before dispatching operation.
    const blockedOp = vi.fn();
    await expect(
      authValue!.apiCallOnce(blockedOp, { projectId: 'project-1' }, {
        tenantId: 'tenant-2',
        actorId: 'actor-1',
        issuer: 'broker',
      }),
    ).rejects.toMatchObject({ status: 403 });
    expect(blockedOp).not.toHaveBeenCalled();

    // Run-bound or unbound binding is rejected before dispatching operation.
    await expect(
      authValue!.apiCallOnce(blockedOp, { projectId: 'project-1', runId: 'run-1' }),
    ).rejects.toMatchObject({ status: 403 });
    expect(blockedOp).not.toHaveBeenCalled();
  });

  it('retains projectCredentialOperations across same-identity reauth and clears on signOut or different identity', async () => {
    vi.spyOn(gatewayClient, 'getAuthorizationContext')
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      })
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-2',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: 'project-1',
        boundRunId: null,
        effectiveAuthority: [],
      });

    let authValue: ReturnType<typeof useAuth> | null = null;
    function Probe() {
      const auth = useAuth();
      useEffect(() => {
        authValue = auth;
      }, [auth]);
      return null;
    }
    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    const opKey = {
      issuer: 'broker',
      actorId: 'actor-1',
      tenantId: 'tenant-1',
      projectId: 'project-1',
      providerId: 'provider-1',
    };
    authValue!.projectCredentialOperations!.set(opKey, {
      operationId: 'op-1',
      expectedProviderRevision: 1,
      status: 'publication_pending',
    });

    // Same-identity sign-in preserves operation.
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce(token('access-1', 'refresh-1'));
    const authorizePromise = authValue!.authorize({ projectId: 'project-1' }, authPopup);
    await completePopupSignIn('code-1');
    await authorizePromise;
    await waitFor(() => expect(authValue!.authorizationContext?.actorId).toBe('actor-1'));
    expect(authValue!.projectCredentialOperations!.get(opKey)).toEqual({
      operationId: 'op-1',
      expectedProviderRevision: 1,
      status: 'publication_pending',
    });

    // Different-identity confirmation clears operation.
    await authValue!.resolveAuthorizationContext('tenant-1');
    expect(authValue!.projectCredentialOperations!.get(opKey)).toBeNull();

    // signOut clears operation.
    authValue!.projectCredentialOperations!.set(opKey, {
      operationId: 'op-2',
      expectedProviderRevision: 1,
      status: 'pending',
    });
    authValue!.signOut();
    expect(authValue!.projectCredentialOperations!.get(opKey)).toBeNull();
  });

  it('apiCallOnce supports null unbound binding with proactive refresh, reconfirmation, identity check, and no 401 retry', async () => {
    vi.spyOn(gatewayClient, 'getAuthorizationContext')
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 1,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      })
      .mockResolvedValueOnce({
        contractVersion: 1,
        issuer: 'broker',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 2,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      });
    mocks.exchangeAuthorizationCode.mockResolvedValueOnce({
      ...token('expiring-unbound', 'refresh-1'),
      expires_in: 1,
    });
    mocks.refreshBrokerToken.mockResolvedValueOnce(token('refreshed-unbound', 'refresh-2'));

    let authValue: ReturnType<typeof useAuth> | null = null;
    function Probe() {
      const auth = useAuth();
      useEffect(() => {
        authValue = auth;
      }, [auth]);
      return null;
    }

    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    const authorizePromise = authValue!.authorize(null, authPopup);
    await completePopupSignIn('code-1');
    await authorizePromise;
    await waitFor(() => expect(authValue!.authorizationContext?.tenantId).toBe('tenant-1'));
    const initialEpoch = authValue!.authSessionEpoch;

    // Pre-refresh increments sessionGeneration while keeping authSessionEpoch; apiCallOnce succeeds and never auto-retries on 401.
    const operation = vi.fn().mockRejectedValueOnce(new GatewayError(401, { code: 'unauthorized' }, 'Expired'));
    await expect(
      authValue!.apiCallOnce(operation, null, {
        tenantId: 'tenant-1',
        actorId: 'actor-1',
        issuer: 'broker',
      }),
    ).rejects.toMatchObject({ status: 401 });

    expect(mocks.refreshBrokerToken).toHaveBeenCalledTimes(1);
    expect(operation).toHaveBeenCalledTimes(1);
    expect(operation).toHaveBeenCalledWith('refreshed-unbound', 'tenant-1');
    expect(authValue!.session?.accessToken).toBe('refreshed-unbound');
    expect(authValue!.authSessionGeneration).toBeGreaterThan(1);
    expect(authValue!.authSessionEpoch).toBe(initialEpoch);

    // Expected identity mismatch fails closed before dispatching operation.
    const blockedOp = vi.fn();
    await expect(
      authValue!.apiCallOnce(blockedOp, null, {
        tenantId: 'tenant-2',
        actorId: 'actor-1',
        issuer: 'broker',
      }),
    ).rejects.toMatchObject({ status: 403 });
    expect(blockedOp).not.toHaveBeenCalled();

    // Run-bound and project-bound requests fail when only an unbound session exists.
    await expect(
      authValue!.apiCallOnce(blockedOp, { projectId: 'project-1', runId: 'run-1' }),
    ).rejects.toMatchObject({ status: 403 });
    await expect(
      authValue!.apiCallOnce(blockedOp, { projectId: 'project-1' }),
    ).rejects.toMatchObject({ status: 401 });
    expect(blockedOp).not.toHaveBeenCalled();
  });
});
