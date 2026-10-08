import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  AUTH_TRANSACTION_STORAGE_KEY,
  buildAuthorizeUrl,
  clearAuthorizationTransaction,
  exchangeAuthorizationCode,
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
    expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('csrf-value');
    expect(JSON.parse(String(init?.body))).toEqual({
      consent_handle: 'consent-handle',
      approve: true,
      scopes: ['projects.read', 'runs.read'],
    });
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
