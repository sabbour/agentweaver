import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  AUTH_TRANSACTION_STORAGE_KEY,
  buildAuthorizeUrl,
  clearAuthorizationTransaction,
  exchangeAuthorizationCode,
  IDENTITY_CALLBACK_MESSAGE_TYPE,
  parseAuthorizationCallbackMessage,
  parseAuthorizationCallbackParameters,
  parseAuthorizationResult,
  pkceChallenge,
  readAuthorizationTransaction,
  storeAuthorizationTransaction,
  submitBrokerConsent,
} from './authProtocol';
import type { AuthorizationConfig, AuthorizationTransaction } from './authProtocol';

const config: AuthorizationConfig = {
  brokerUrl: 'https://identity.example.test',
  clientId: 'registered-agentweaver-web',
  redirectUri: 'https://app.example.test/auth/callback',
  scopes: ['projects.read', 'runs.read'],
};

afterEach(() => {
  clearAuthorizationTransaction();
  vi.restoreAllMocks();
});

describe('Identity Broker authorization', () => {
  it('builds a PKCE authorization request bound to the exact project and run', async () => {
    const transaction: AuthorizationTransaction = {
      state: 'state-value',
      verifier: 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk',
      redirectUri: config.redirectUri,
      binding: { projectId: 'project/one', runId: 'run 42' },
    };

    const challenge = await pkceChallenge(transaction.verifier);
    expect(challenge).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');

    const url = new URL(buildAuthorizeUrl(config, transaction, challenge));
    expect(url.origin).toBe(config.brokerUrl);
    expect(url.pathname).toBe('/connect/authorize');
    expect(url.searchParams.get('client_id')).toBe(config.clientId);
    expect(url.searchParams.get('redirect_uri')).toBe(config.redirectUri);
    expect(url.searchParams.get('response_type')).toBe('code');
    expect(url.searchParams.get('code_challenge_method')).toBe('S256');
    expect(url.searchParams.get('code_challenge')).toBe(challenge);
    expect(url.searchParams.get('scope')).toBe('projects.read runs.read');
    expect(url.searchParams.get('project_id')).toBe('project/one');
    expect(url.searchParams.get('run_id')).toBe('run 42');
  });

  it('rejects a callback whose OAuth state does not match the stored transaction', () => {
    expect(() => parseAuthorizationResult(
      'https://app.example.test/auth/callback?code=one&state=wrong',
      'expected',
    )).toThrow(/invalid state/i);
  });

  it('accepts only a single bounded identity callback response shape', () => {
    expect(parseAuthorizationCallbackParameters('?code=code-value&state=state-value')).toEqual({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'code-value',
    });
    expect(parseAuthorizationCallbackParameters(
      '?error=access_denied&error_description=Denied&state=state-value',
    )).toEqual({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      error: 'access_denied',
      error_description: 'Denied',
    });
    expect(parseAuthorizationCallbackParameters(
      '?code=code-value&state=state-value&state=duplicate',
    )).toBeUndefined();
    expect(parseAuthorizationCallbackParameters(
      '?code=code-value&state=state-value&unexpected=parameter',
    )).toBeUndefined();
  });

  it('accepts the exact configured Broker issuer on code and error callbacks', () => {
    const issuer = 'https://identity.example.test/';
    expect(parseAuthorizationCallbackParameters(
      `?code=code-value&state=state-value&iss=${encodeURIComponent(issuer)}`,
      issuer,
    )).toEqual({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'code-value',
    });
    expect(parseAuthorizationCallbackParameters(
      `?error=access_denied&error_description=Denied&state=state-value&iss=${encodeURIComponent(issuer)}`,
      issuer,
    )).toEqual({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      error: 'access_denied',
      error_description: 'Denied',
    });
  });

  it.each([
    ['wrong host', 'https://other.example.test/'],
    ['wrong path', 'https://identity.example.test/other'],
    ['different port', 'https://identity.example.test:8443/'],
    ['missing trailing slash', 'https://identity.example.test'],
    ['non-HTTPS scheme', 'http://identity.example.test/'],
    ['credentials', 'https://user@identity.example.test/'],
    ['query', 'https://identity.example.test/?x=1'],
    ['fragment', 'https://identity.example.test/#x'],
    ['empty value', ''],
    ['malformed URI', 'https://identity.example.test:invalid/'],
    ['oversized value', `https://identity.example.test/${'x'.repeat(2048)}`],
  ])('rejects an issuer that is not an exact HTTPS match (%s)', (_name, issuer) => {
    expect(parseAuthorizationCallbackParameters(
      `?code=code-value&state=state-value&iss=${encodeURIComponent(issuer)}`,
      'https://identity.example.test/',
    )).toBeUndefined();
  });

  it('rejects duplicate or unconfigured issuer parameters', () => {
    const issuer = encodeURIComponent('https://identity.example.test/');
    expect(parseAuthorizationCallbackParameters(
      `?code=code-value&state=state-value&iss=${issuer}&iss=${issuer}`,
      'https://identity.example.test/',
    )).toBeUndefined();
    expect(parseAuthorizationCallbackParameters(
      `?code=code-value&state=state-value&iss=${issuer}`,
      '',
    )).toBeUndefined();
  });

  it('accepts only bounded callback messages with an exact shape', () => {
    expect(parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'single-use-code',
    })).toEqual({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'single-use-code',
    });
    expect(parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'single-use-code',
      access_token: 'must-not-be-forwarded',
    })).toBeUndefined();
    expect(parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: '',
      code: 'single-use-code',
    })).toBeUndefined();
    expect(parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: 'state-value',
      code: 'single-use-code',
      error: 'access_denied',
    })).toBeUndefined();
  });

  it('recovers only the PKCE transaction from session storage, never tokens', () => {
    const transaction: AuthorizationTransaction = {
      state: 'state-value',
      verifier: 'verifier-value',
      redirectUri: config.redirectUri,
      binding: { projectId: 'p1', runId: 'r1' },
    };
    storeAuthorizationTransaction(transaction);

    expect(readAuthorizationTransaction()).toEqual(transaction);
    const serialized = sessionStorage.getItem(AUTH_TRANSACTION_STORAGE_KEY) ?? '';
    expect(serialized).toContain('verifier-value');
    expect(serialized).not.toContain('access_token');
    expect(serialized).not.toContain('refresh_token');
  });

  it('submits Broker consent with its antiforgery token, cookies, and requested scopes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    const prompt = {
      consent_required: true as const,
      consent_handle: 'consent-handle',
      client_id: config.clientId,
      requested_scopes: ['projects.read', 'runs.read'],
      csrf_token: 'csrf-value',
    };

    await submitBrokerConsent(config, prompt, true, fetcher);

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(`${config.brokerUrl}/connect/consent`);
    expect(init?.credentials).toBe('include');
    expect(init?.redirect).toBe('manual');
    expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('csrf-value');
    expect(JSON.parse(String(init?.body))).toEqual({
      consent_handle: 'consent-handle',
      approve: true,
      scopes: ['projects.read', 'runs.read'],
    });
  });

  it('does not follow the Broker consent redirect from fetch', async () => {
    const redirect = { type: 'opaqueredirect', status: 0, ok: false, url: '' } as Response;
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(redirect);
    await expect(submitBrokerConsent(config, {
      consent_required: true,
      consent_handle: 'consent-handle',
      client_id: config.clientId,
      requested_scopes: ['projects.read'],
      csrf_token: 'csrf-value',
    }, true, fetcher)).resolves.toBe(redirect);
  });

  it('submits a Broker consent denial without granting requested scopes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('{}', { status: 200 }));
    await submitBrokerConsent(config, {
      consent_required: true,
      consent_handle: 'consent-handle',
      client_id: config.clientId,
      requested_scopes: ['projects.read'],
      csrf_token: 'csrf-value',
    }, false, fetcher);

    const [, init] = fetcher.mock.calls[0];
    expect(JSON.parse(String(init?.body))).toEqual({
      consent_handle: 'consent-handle',
      approve: false,
      scopes: [],
    });
    expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('csrf-value');
  });

  it('exchanges the authorization code at the Broker with the PKCE verifier', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        access_token: 'access-value',
        refresh_token: 'refresh-value',
        token_type: 'Bearer',
        expires_in: 300,
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    const transaction: AuthorizationTransaction = {
      state: 'state-value',
      verifier: 'verifier-value',
      redirectUri: config.redirectUri,
      binding: null,
    };

    const token = await exchangeAuthorizationCode(config, transaction, 'one-time-code', fetcher);
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(`${config.brokerUrl}/connect/token`);
    expect(init?.credentials).toBe('include');
    expect(new URLSearchParams(String(init?.body))).toEqual(new URLSearchParams({
      grant_type: 'authorization_code',
      client_id: config.clientId,
      code: 'one-time-code',
      redirect_uri: config.redirectUri,
      code_verifier: 'verifier-value',
    }));
    expect(token.access_token).toBe('access-value');
    expect(token.refresh_token).toBe('refresh-value');
  });
});
