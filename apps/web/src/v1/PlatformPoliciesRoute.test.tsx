import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthorizationCallbackMessage, AuthorizationTransaction } from './authProtocol';
import { AgentweaverGatewayClient, gatewayClient } from './api';
import {
  firstRunTourStorageKey,
  markFirstRunTourComplete,
} from '../components/onboarding/firstRunTourStorage';

const authMocks = vi.hoisted(() => ({
  buildAuthorizeUrl: vi.fn((
    _config: unknown,
    transaction: { state: string },
    _challenge: string,
    prompt?: 'none',
  ) => `https://broker.test/authorize?state=${encodeURIComponent(transaction.state)}${prompt ? '&prompt=none' : ''}`),
  exchangeAuthorizationCode: vi.fn(),
  parseAuthorizationCallbackMessage: (value: unknown) => value as AuthorizationCallbackMessage,
  refreshBrokerToken: vi.fn(),
}));

vi.mock('./authProtocol', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./authProtocol')>();
  return {
    ...actual,
    buildAuthorizeUrl: authMocks.buildAuthorizeUrl,
    clearAuthorizationTransaction: vi.fn(),
    defaultAuthorizationConfig: () => ({
      brokerUrl: 'https://broker.test',
      clientId: 'client-1',
      redirectUri: 'https://app.test/auth/callback',
      scopes: ['agentweaver.api'],
    }),
    exchangeAuthorizationCode: authMocks.exchangeAuthorizationCode,
    isConsentPrompt: vi.fn(() => false),
    parseAuthorizationCallbackMessage: authMocks.parseAuthorizationCallbackMessage,
    parseAuthorizationCallbackParameters: vi.fn(() => null),
    parseAuthorizationResult: (responseUrl: string, expectedState: string) => {
      const url = new URL(responseUrl);
      if (url.searchParams.get('state') !== expectedState) return null;
      const code = url.searchParams.get('code');
      return code ? { code } : null;
    },
    pkceChallenge: async () => 'challenge',
    randomState: () => 'state-1',
    randomVerifier: () => 'verifier-1',
    readAuthorizationTransaction: vi.fn<() => AuthorizationTransaction | null>(() => null),
    refreshBrokerToken: authMocks.refreshBrokerToken,
    storeAuthorizationTransaction: vi.fn(),
    submitBrokerConsent: vi.fn(),
  };
});

vi.mock('./config', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./config')>()),
  gatewayBaseUrl: '/api/v1',
  brokerBaseUrl: 'https://broker.test',
  oauthClientId: 'client-1',
  oauthRedirectUri: 'https://app.test/auth/callback',
  oauthScopes: ['agentweaver.api'],
  missingAuthConfiguration: () => [],
}));

import App from './App';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider, useAuth } from './AuthContext';
import {
  PlatformPoliciesControllerProvider,
  usePlatformPoliciesController,
} from './PlatformPoliciesController';

const hash = 'a'.repeat(64);
const policyText = '<img src=x onerror=alert(1)>';

function bytes(value: string): number[] {
  return [...new TextEncoder().encode(value)];
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function policyHistory() {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    snapshotRevision: 2,
    items: omitSnapshotHead
      ? [{ revision: 1, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' }]
      : [
          { revision: 2, contentSha256: hash, createdAt: '2026-01-03T03:04:05Z' },
          { revision: 1, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' },
        ],
    nextCursor: null,
  };
}

function policyRevision() {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    revision: 2,
    contentSha256: hash,
    createdAt: '2026-01-03T03:04:05Z',
    artifact: {
      manifestBytes: bytes('name: saved-policy\n'),
      files: [{ relativePath: 'rules/example.yaml', bytes: bytes(policyText) }],
      contentSha256: hash,
    },
  };
}

let popup: Window;
let policyStatus: number;
let validationStatus: number;
let omitSnapshotHead: boolean;
let delayHistory: boolean;
let unauthorizedTenantOnce: string | null;
let unauthorizedValidationTenantOnce: string | null;
let resolveHistory: ((response: Response) => void) | null;
let requests: { url: string; init?: RequestInit }[];

async function completeSignIn(code = 'code-1'): Promise<void> {
  await waitFor(() => expect(fetch).toHaveBeenCalled());
  await act(async () => {
    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://app.test',
      source: popup,
      data: {
        type: 'agentweaver.identity.callback',
        code,
        state: 'state-1',
      },
    }));
  });
}

function policyRequests() {
  return requests.filter(({ url }) => url.includes('/policies/platform/revisions'));
}

function policyValidationRequests() {
  return requests.filter(({ url }) => url.includes('/policies/platform/validate'));
}

async function completePolicySignIn(): Promise<void> {
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
        code: 'policy-code',
        state: 'state-1',
      },
    }));
  });
}

function PolicyRecoveryHarness() {
  const auth = useAuth();
  const policy = usePlatformPoliciesController();
  return (
    <>
      <button type="button" onClick={() => { void auth.authorize({ projectId: 'project-1', runId: 'run-1' }); }}>
        Sign in for active run
      </button>
      <button type="button" onClick={policy.retry}>Retry policy</button>
      <label>
        Tenant ID
        <input
          value={policy.tenantId}
          onChange={(event) => policy.setTenantId(event.target.value)}
        />
      </label>
      <output>session:{auth.session?.accessToken ?? 'signed-out'}</output>
      <output>policy-status:{policy.status}</output>
      <output>policy-authorized:{String(policy.authorized)}</output>
    </>
  );
}

function renderPolicyRecoveryHarness() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={['/policies']}>
        <PlatformPoliciesControllerProvider>
          <PolicyRecoveryHarness />
        </PlatformPoliciesControllerProvider>
      </MemoryRouter>
    </AuthProvider>,
  );
}

async function startRunBoundPolicySession(): Promise<void> {
  renderPolicyRecoveryHarness();
  fireEvent.click(screen.getByRole('button', { name: 'Sign in for active run' }));
  await completeSignIn('run-code');
  await screen.findByText('session:run-token');
  await completePolicySignIn();
  await waitFor(() => expect(
    document.querySelector('iframe[title="Platform Policy sign-in"]'),
  ).toBeNull());
  await waitFor(() => expect(policyRequests().length).toBeGreaterThan(0));
}

describe('Platform Policies served route', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/policies');
    window.__AGENTWEAVER_IDENTITY_CALLBACK__ = undefined;
    window.localStorage.clear();
    window.sessionStorage.clear();
    vi.clearAllMocks();
    policyStatus = 200;
    validationStatus = 200;
    omitSnapshotHead = false;
    delayHistory = false;
    unauthorizedTenantOnce = null;
    unauthorizedValidationTenantOnce = null;
    resolveHistory = null;
    requests = [];
    authMocks.exchangeAuthorizationCode.mockResolvedValue({
      access_token: 'access-1',
      refresh_token: 'refresh-1',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    authMocks.refreshBrokerToken.mockResolvedValue({
      access_token: 'access-refreshed',
      refresh_token: 'refresh-refreshed',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    popup = {
      closed: false,
      location: { replace: vi.fn() },
      close: vi.fn(),
    } as unknown as Window;
    vi.spyOn(window, 'open').mockReturnValue(popup);
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      type: 'opaqueredirect',
      status: 0,
      ok: false,
      url: '',
      json: async () => null,
    }));

    const fetcher = vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'https://app.test');
      requests.push({ url: url.href, init });
      if (url.pathname === '/api/v1/authorization/context')
      {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        const token = new Headers(init?.headers).get('Authorization')?.replace(/^Bearer /, '');
        const runBinding = token === 'run-token';
        return jsonResponse({
          contractVersion: 1,
          issuer: 'projects-config',
          actorId: 'actor-1',
          tenantId: tenantSelector ?? 'tenant-1',
          membershipRevision: 1,
          boundProjectId: runBinding ? 'project-1' : null,
          boundRunId: runBinding ? 'run-1' : null,
          effectiveAuthority: [],
        });
      }
      if (url.pathname === '/api/v1/policies/platform/revisions') {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        if (unauthorizedTenantOnce === tenantSelector) {
          unauthorizedTenantOnce = null;
          return jsonResponse({ detail: 'private server detail' }, 401);
        }
        if (delayHistory)
          return new Promise<Response>((resolve) => { resolveHistory = resolve; });
        return policyStatus === 200
          ? jsonResponse(policyHistory())
          : jsonResponse({ detail: 'private server detail' }, policyStatus);
      }
      if (url.pathname === '/api/v1/policies/platform/revisions/2')
        return jsonResponse(policyRevision());
      if (url.pathname === '/api/v1/policies/platform/validate') {
        const tenantSelector = new Headers(init?.headers).get('X-Agentweaver-Tenant');
        if (unauthorizedValidationTenantOnce === tenantSelector) {
          unauthorizedValidationTenantOnce = null;
          return jsonResponse({ detail: 'private server detail' }, 401);
        }
        return validationStatus === 422
          ? jsonResponse({
              title: 'native_acs_policy_invalid',
              status: 422,
              code: 'native_acs_policy_invalid',
              diagnostics: [{
                code: 'yaml.invalid',
                message: 'The manifest is invalid.',
                relativePath: null,
                line: 2,
                column: 1,
              }],
            }, 422)
          : validationStatus === 200
            ? jsonResponse({ isValid: true, diagnostics: [] })
            : jsonResponse({ detail: 'private server detail' }, validationStatus);
      }
      return jsonResponse({ items: [], count: 0, nextCursor: null });
    });
    const controlledClient = new AgentweaverGatewayClient('/api/v1', fetcher);
    vi.spyOn(gatewayClient, 'getAuthorizationContext').mockImplementation(
      (token, tenant, signal) =>
        controlledClient.getAuthorizationContext(token, tenant, signal),
    );
    vi.spyOn(gatewayClient, 'getPlatformPolicyRevisionHistory').mockImplementation(
      (token, tenant, options) =>
        controlledClient.getPlatformPolicyRevisionHistory(token, tenant, options),
    );
    vi.spyOn(gatewayClient, 'readPlatformPolicyRevision').mockImplementation(
      (token, revision, tenant, signal) =>
        controlledClient.readPlatformPolicyRevision(token, revision, tenant, signal),
    );
    vi.spyOn(gatewayClient, 'validatePlatformPolicyArtifact').mockImplementation(
      (token, tenant, artifact, signal) =>
        controlledClient.validatePlatformPolicyArtifact(token, tenant, artifact, signal),
    );
    vi.spyOn(gatewayClient, 'listProjects').mockResolvedValue([]);
    vi.spyOn(gatewayClient, 'listNotifications').mockResolvedValue({
      items: [],
      count: 0,
      nextCursor: null,
    });
    markFirstRunTourComplete(firstRunTourStorageKey(JSON.stringify(['projects-config', 'actor-1'])));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('reads the saved snapshot through App and renders source as text', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    await screen.findByRole('heading', { name: 'Platform Policies' });
    await screen.findByText('Saved Platform Policy revisions');
    expect(await screen.findByRole('link', { name: 'Platform Policies' })).toBeTruthy();
    expect(screen.getAllByText('2', { exact: true }).length).toBeGreaterThan(0);

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    expect(await screen.findByText(policyText)).toBeTruthy();
    expect(document.querySelector('img[src="x"]')).toBeNull();
    expect(screen.getByText('name: saved-policy')).toBeTruthy();

    const policyCalls = policyRequests();
    expect(policyCalls.length).toBeGreaterThanOrEqual(2);
    for (const { init } of policyCalls)
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('loads a saved revision into a local draft and validates it without writing', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    await screen.findByText(policyText);
    fireEvent.click(screen.getByRole('button', { name: 'Load viewed revision into draft' }));
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('name: saved-policy\n');
    const fileContent = screen.getByRole('textbox', { name: 'Content for rules/example.yaml' });
    expect((fileContent as HTMLTextAreaElement).value).toBe(policyText);
    fireEvent.change(fileContent, { target: { value: `${policyText}\nallow: false` } });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));

    expect(await screen.findByText('The Platform Policy draft is valid.')).toBeTruthy();
    const validationCall = policyValidationRequests().at(-1);
    expect(validationCall?.url).toBe('https://app.test/api/v1/policies/platform/validate');
    expect(validationCall?.init?.method).toBe('POST');
    expect(new Headers(validationCall?.init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    const body = JSON.parse(String(validationCall?.init?.body));
    expect(body.artifact.manifestBytes).toEqual(bytes('name: saved-policy\n'));
    expect(body.artifact.files).toEqual([{
      relativePath: 'rules/example.yaml',
      bytes: bytes(`${policyText}\nallow: false`),
    }]);
    expect(body.artifact.contentSha256).toMatch(/^[0-9a-f]{64}$/);
    expect(requests.some(({ url, init }) =>
      url.includes('/policies/platform/') && init?.method === 'PUT')).toBe(false);
  });

  it('shows bounded server diagnostics from an invalid validation response', async () => {
    validationStatus = 422;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    await waitFor(() => expect(policyValidationRequests()).toHaveLength(1));
    const diagnostics = await screen.findByRole('alert', { name: 'Validation diagnostics' });
    expect(diagnostics.textContent)
      .toContain('line 2, column 1: The manifest is invalid.');
    expect(document.body.textContent).not.toContain('private server detail');
  });

  it('refreshes policy access after a validation 401 and requires validation again', async () => {
    unauthorizedValidationTenantOnce = 'tenant-1';
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'unsaved policy draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect(await screen.findByText(
      'Platform Policy access expired. Check access, then validate again.',
    )).toBeTruthy();
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('unsaved policy draft');
    expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    const canceledMessage =
      'Platform Policy access changed. Validate the draft again when access is ready.';
    await waitFor(() => expect(
      screen.queryByText('The Platform Policy draft is valid.') ||
        screen.queryByText(canceledMessage),
    ).toBeTruthy());
    if (screen.queryByText(canceledMessage)) {
      await screen.findByText('Saved Platform Policy revisions');
      fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    }
    expect(await screen.findByText('The Platform Policy draft is valid.')).toBeTruthy();

    const calls = policyValidationRequests();
    expect(calls.length).toBeGreaterThanOrEqual(2);
    expect(new Headers(calls[0]?.init?.headers).get('Authorization')).toBe('Bearer access-1');
    expect(new Headers(calls.at(-1)?.init?.headers).get('Authorization')).toBe('Bearer access-refreshed');
  });

  it('preserves a local draft through token refresh for a selected tenant', async () => {
    authMocks.exchangeAuthorizationCode.mockResolvedValue({
      access_token: 'access-1',
      refresh_token: 'refresh-1',
      token_type: 'Bearer',
      expires_in: 31,
    });
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    await waitFor(() => expect(policyRequests().some(({ init }) =>
      new Headers(init?.headers).get('X-Agentweaver-Tenant') === 'tenant-2')).toBe(true));
    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'unsaved before token refresh' },
    });
    const historyCount = policyRequests().length;
    delayHistory = true;

    await waitFor(() => expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce(), {
      timeout: 5_000,
    });
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(historyCount), {
      timeout: 5_000,
    });
    await waitFor(() => expect(resolveHistory).not.toBeNull(), { timeout: 5_000 });
    await waitFor(() => expect(requests.some(({ url, init }) =>
      url.endsWith('/authorization/context') &&
      new Headers(init?.headers).get('Authorization') === 'Bearer access-refreshed')).toBe(true), {
      timeout: 5_000,
    });
    expect(policyRequests().slice(historyCount).some(({ init }) =>
      new Headers(init?.headers).get('X-Agentweaver-Tenant') === 'tenant-2')).toBe(true);
    await act(async () => {
      resolveHistory?.(jsonResponse(policyHistory()));
    });
    expect((await screen.findByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('unsaved before token refresh');
  });

  it('clears saved data and navigation when validation is denied', async () => {
    validationStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'tenant-1 private draft' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Content for rules/policy-1.yaml' }), {
      target: { value: 'tenant-1 private file' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate draft' }));
    expect((await screen.findByText('You do not have permission to access Platform Policies.')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(policyValidationRequests().length).toBe(1);
    expect(requests.some(({ url, init }) =>
      url.includes('/policies/platform/') && init?.method === 'PUT')).toBe(false);

    validationStatus = 200;
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('');
    expect(screen.queryByRole('textbox', { name: 'Content for rules/policy-1.yaml' })).toBeNull();
  });

  it('hides the route data and navigation when the policy history probe denies access', async () => {
    policyStatus = 403;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    expect((await screen.findByRole('alert')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(document.body.textContent).not.toContain('private server detail');
    expect(policyRequests().length).toBeGreaterThan(0);
    for (const { init } of policyRequests())
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('rejects a history page that does not contain its declared snapshot head', async () => {
    omitSnapshotHead = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();

    expect((await screen.findByRole('alert')).textContent)
      .toBe('Platform Policies could not load. Select Retry.');
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
  });

  it('clears the snapshot and navigation as soon as the tenant changes', async () => {
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');
    expect(await screen.findByRole('link', { name: 'Platform Policies' })).toBeTruthy();

    fireEvent.change(screen.getByRole('textbox', { name: 'Platform Policy manifest' }), {
      target: { value: 'tenant-1 private draft' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });

    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect((screen.getByRole('textbox', { name: 'Tenant ID' }) as HTMLInputElement).value)
      .toBe('tenant-2');

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Saved Platform Policy revisions');
    expect((screen.getByRole('textbox', { name: 'Platform Policy manifest' }) as HTMLTextAreaElement).value)
      .toBe('');
  });

  it('keeps the selected tenant for timer and visible-tab checks', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByText('Saved Platform Policy revisions');

    fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
      target: { value: 'tenant-2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(
      new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'),
    ).toBe('tenant-2'));
    await screen.findByText('Saved Platform Policy revisions');

    const timerRequestCount = policyRequests().length;
    act(() => intervalHandlers.at(-1)?.());
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(timerRequestCount));
    expect(new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-2');

    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    visibility.mockReturnValue('hidden');
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    visibility.mockReturnValue('visible');
    const visibleRequestCount = policyRequests().length;
    act(() => document.dispatchEvent(new Event('visibilitychange')));
    await waitFor(() => expect(policyRequests().length).toBeGreaterThan(visibleRequestCount));
    expect(new Headers(policyRequests().at(-1)?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-2');
  });

  it('restores focus when timer revalidation revokes access', async () => {
    const intervalHandlers: (() => void)[] = [];
    const setInterval = window.setInterval.bind(window);
    vi.spyOn(window, 'setInterval').mockImplementation(((handler: TimerHandler, delay?: number) => {
      if (delay === 60_000 && typeof handler === 'function')
        intervalHandlers.push(handler as () => void);
      return setInterval(handler, delay);
    }) as typeof window.setInterval);
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await screen.findByRole('link', { name: 'Platform Policies' });
    fireEvent.click(screen.getAllByRole('button', { name: 'View' })[0]);
    expect(await screen.findByText(policyText)).toBeTruthy();
    const policyLink = screen.getByRole('link', { name: 'Platform Policies' });
    policyLink.focus();
    expect(document.activeElement).toBe(policyLink);

    policyStatus = 403;
    act(() => intervalHandlers.at(-1)?.());
    expect((await screen.findByRole('alert')).textContent)
      .toBe('You do not have permission to access Platform Policies.');
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
    expect(screen.queryByText(policyText)).toBeNull();
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    const retry = screen.getByRole('button', { name: 'Retry' });
    expect((retry as HTMLButtonElement).disabled).toBe(false);
    expect([retry, screen.getByRole('main', { name: 'Main content' })])
      .toContain(document.activeElement);
  });

  it.each([
    ['tenant change first', 'success'],
    ['tenant change first', 'failure'],
    ['refresh first', 'success'],
    ['refresh first', 'failure'],
  ] as const)(
    'keeps the active run session when %s meets a delayed refresh %s',
    async (changeOrder, outcome) => {
      const refresh = deferred<{
        access_token: string;
        refresh_token: string;
        token_type: string;
        expires_in: number;
      }>();
      authMocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) => ({
        access_token: code === 'run-code' ? 'run-token' : 'policy-token',
        refresh_token: code === 'run-code' ? 'refresh-run' : 'refresh-policy',
        token_type: 'Bearer',
        expires_in: 3600,
      }));
      authMocks.refreshBrokerToken.mockReturnValue(refresh.promise);
      unauthorizedTenantOnce = 'tenant-1';
      await startRunBoundPolicySession();
      await waitFor(() => expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce());

      const finishRefresh = async () => {
        await act(async () => {
          if (outcome === 'success') {
            refresh.resolve({
              access_token: 'policy-token-refreshed',
              refresh_token: 'refresh-policy-refreshed',
              token_type: 'Bearer',
              expires_in: 3600,
            });
          } else {
            refresh.reject(new Error('refresh denied'));
          }
          await refresh.promise.catch(() => undefined);
        });
      };
      const selectTenantTwo = () => {
        fireEvent.change(screen.getByRole('textbox', { name: 'Tenant ID' }), {
          target: { value: 'tenant-2' },
        });
      };

      let changeBoundary: number;
      if (changeOrder === 'tenant change first') {
        changeBoundary = requests.length;
        selectTenantTwo();
        await finishRefresh();
      } else {
        await finishRefresh();
        if (outcome === 'success')
          await screen.findByText('policy-authorized:true');
        else
          await screen.findByText('policy-status:unavailable');
        changeBoundary = requests.length;
        selectTenantTwo();
        fireEvent.click(screen.getByRole('button', { name: 'Retry policy' }));
      }
      if (outcome === 'failure') {
        await waitFor(() => expect(
          document.querySelector('iframe[title="Platform Policy sign-in"]'),
        ).not.toBeNull());
        await completePolicySignIn();
      }
      await screen.findByText('policy-authorized:true');
      expect(screen.getByText('session:run-token')).toBeTruthy();

      const requestsAfterTenantChange = requests.slice(changeBoundary);
      const policyHistory = requestsAfterTenantChange.filter(({ url }) =>
        url.includes('/policies/platform/revisions'));
      expect(policyHistory.length).toBeGreaterThan(0);
      for (const { init } of policyHistory)
        expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-2');
      for (const { url, init } of requestsAfterTenantChange) {
        if (url.endsWith('/authorization/context'))
          expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-2');
      }
      expect(unauthorizedTenantOnce).toBeNull();
    },
  );

  it('re-resolves and reprobes the same tenant after a Policy 401 refresh', async () => {
    authMocks.exchangeAuthorizationCode.mockImplementation(async (_config, _transaction, code) => ({
      access_token: code === 'run-code' ? 'run-token' : 'policy-token',
      refresh_token: code === 'run-code' ? 'refresh-run' : 'refresh-policy',
      token_type: 'Bearer',
      expires_in: 3600,
    }));
    authMocks.refreshBrokerToken.mockResolvedValue({
      access_token: 'policy-token-refreshed',
      refresh_token: 'refresh-policy-refreshed',
      token_type: 'Bearer',
      expires_in: 3600,
    });
    unauthorizedTenantOnce = 'tenant-1';
    await startRunBoundPolicySession();
    await screen.findByText('policy-authorized:true');

    expect(authMocks.refreshBrokerToken).toHaveBeenCalledOnce();
    expect(screen.getByText('session:run-token')).toBeTruthy();
    const refreshedContext = requests.find(({ url, init }) =>
      url.endsWith('/authorization/context') &&
      new Headers(init?.headers).get('Authorization') === 'Bearer policy-token-refreshed');
    expect(refreshedContext).toBeTruthy();
    expect(new Headers(refreshedContext?.init?.headers).get('X-Agentweaver-Tenant'))
      .toBe('tenant-1');
    const policyCalls = policyRequests();
    expect(policyCalls.length).toBeGreaterThanOrEqual(2);
    for (const { init } of policyCalls)
      expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('does not render a successful response after sign-out fences the request', async () => {
    delayHistory = true;
    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await completeSignIn();
    await waitFor(() => expect(resolveHistory).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Account' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await screen.findByRole('button', { name: 'Sign in' });

    await act(async () => {
      resolveHistory?.(jsonResponse(policyHistory()));
    });
    expect(screen.queryByText('Saved Platform Policy revisions')).toBeNull();
    expect(screen.queryByRole('link', { name: 'Platform Policies' })).toBeNull();
  });
});
