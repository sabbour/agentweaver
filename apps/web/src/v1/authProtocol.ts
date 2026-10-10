import {
  brokerBaseUrl,
  brokerIssuer,
  oauthClientId,
  oauthRedirectUri,
  oauthScopes,
} from './config';
import type { BrokerConsentPrompt, BrokerTokenResponse } from './contracts';

export interface RunBinding {
  projectId: string;
  runId: string;
}

export interface AuthorizationTransaction {
  state: string;
  verifier: string;
  redirectUri: string;
  binding: RunBinding | null;
}

export interface AuthorizationConfig {
  brokerUrl: string;
  clientId: string;
  redirectUri: string;
  scopes: string[];
}

export interface AuthorizationResult {
  code: string;
  state: string;
}

export const IDENTITY_CALLBACK_MESSAGE_TYPE = 'agentweaver.identity.callback';
const IDENTITY_CALLBACK_PARAMETER_NAMES = new Set([
  'state',
  'code',
  'error',
  'error_description',
  'iss',
]);
const IDENTITY_CALLBACK_STATE_LIMIT = 512;
const IDENTITY_CALLBACK_CODE_LIMIT = 4096;
const IDENTITY_CALLBACK_ERROR_LIMIT = 256;
const IDENTITY_CALLBACK_DESCRIPTION_LIMIT = 2048;
const IDENTITY_CALLBACK_ISSUER_LIMIT = 2048;

export type AuthorizationCallbackMessage =
  | { type: typeof IDENTITY_CALLBACK_MESSAGE_TYPE; state: string; code: string }
  | {
      type: typeof IDENTITY_CALLBACK_MESSAGE_TYPE;
      state: string;
      error: string;
      error_description?: string;
    };

declare global {
  interface Window {
    __AGENTWEAVER_IDENTITY_CALLBACK__?: unknown;
  }
}

export const AUTH_TRANSACTION_STORAGE_KEY = 'agentweaver.oauth.transaction';

export function defaultAuthorizationConfig(): AuthorizationConfig {
  return {
    brokerUrl: brokerBaseUrl,
    clientId: oauthClientId,
    redirectUri: oauthRedirectUri,
    scopes: oauthScopes,
  };
}

export function randomVerifier(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return base64Url(bytes);
}

export function randomState(): string {
  return randomVerifier();
}

function base64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const value of bytes) binary += String.fromCharCode(value);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export async function pkceChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64Url(new Uint8Array(digest));
}

export function buildAuthorizeUrl(
  config: AuthorizationConfig,
  transaction: AuthorizationTransaction,
  challenge: string,
): string {
  const url = new URL('/connect/authorize', config.brokerUrl);
  url.searchParams.set('client_id', config.clientId);
  url.searchParams.set('redirect_uri', transaction.redirectUri);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('scope', config.scopes.join(' '));
  url.searchParams.set('code_challenge', challenge);
  url.searchParams.set('code_challenge_method', 'S256');
  url.searchParams.set('state', transaction.state);
  if (transaction.binding) {
    url.searchParams.set('project_id', transaction.binding.projectId);
    url.searchParams.set('run_id', transaction.binding.runId);
  }
  return url.toString();
}

export function parseAuthorizationResult(
  responseUrl: string,
  expectedState: string,
): AuthorizationResult | { error: string; description?: string } | undefined {
  let url: URL;
  try {
    url = new URL(responseUrl);
  } catch {
    return undefined;
  }
  const code = url.searchParams.get('code');
  const error = url.searchParams.get('error');
  const state = url.searchParams.get('state');
  if (!code && !error) return undefined;
  if (state !== expectedState)
    throw new Error('The Identity Broker returned an authorization response with an invalid state.');
  if (error) return { error, description: url.searchParams.get('error_description') ?? undefined };
  return { code: code!, state: state! };
}

export function parseAuthorizationCallbackMessage(
  value: unknown,
): AuthorizationCallbackMessage | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const message = value as Record<string, unknown>;
  if (message.type !== IDENTITY_CALLBACK_MESSAGE_TYPE ||
      !isBoundedCallbackValue(message.state, IDENTITY_CALLBACK_STATE_LIMIT))
    return undefined;

  const keys = Object.keys(message);
  if (isBoundedCallbackValue(message.code, IDENTITY_CALLBACK_CODE_LIMIT) &&
      keys.every((key) => ['type', 'state', 'code'].includes(key)))
    return {
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: message.state,
      code: message.code,
    };

  if (isBoundedCallbackValue(message.error, IDENTITY_CALLBACK_ERROR_LIMIT) &&
      (message.error_description === undefined ||
        isBoundedCallbackValue(message.error_description, IDENTITY_CALLBACK_DESCRIPTION_LIMIT)) &&
      keys.every((key) => ['type', 'state', 'error', 'error_description'].includes(key)))
    return {
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: message.state,
      error: message.error,
      ...(typeof message.error_description === 'string'
        ? { error_description: message.error_description }
        : {}),
    };

  return undefined;
}

export function parseAuthorizationCallbackParameters(
  search: string,
  expectedIssuer: string = brokerIssuer,
): AuthorizationCallbackMessage | undefined {
  const parameters = new URLSearchParams(search);
  if ([...parameters.keys()].some((key) => !IDENTITY_CALLBACK_PARAMETER_NAMES.has(key)))
    return undefined;

  const states = parameters.getAll('state');
  const codes = parameters.getAll('code');
  const errors = parameters.getAll('error');
  const descriptions = parameters.getAll('error_description');
  const issuers = parameters.getAll('iss');
  if (states.length !== 1 || issuers.length > 1) return undefined;
  if (issuers.length === 1 &&
      (!isHttpsIssuer(issuers[0]) ||
        !isHttpsIssuer(expectedIssuer) ||
        issuers[0] !== expectedIssuer))
    return undefined;
  if (codes.length === 1 && errors.length === 0 && descriptions.length === 0)
    return parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      code: codes[0],
    });
  if (codes.length === 0 && errors.length === 1 && descriptions.length <= 1)
    return parseAuthorizationCallbackMessage({
      type: IDENTITY_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      error: errors[0],
      ...(descriptions.length ? { error_description: descriptions[0] } : {}),
    });
  return undefined;
}

function isBoundedCallbackValue(value: unknown, limit: number): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= limit &&
    !value.split('').some((character) => {
      const code = character.charCodeAt(0);
      return code <= 0x1f || code === 0x7f;
    });
}

function isHttpsIssuer(value: unknown): value is string {
  if (!isBoundedCallbackValue(value, IDENTITY_CALLBACK_ISSUER_LIMIT) ||
      value.includes('?') ||
      value.includes('#'))
    return false;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return false;
  }
  const authority = value.slice(value.indexOf(':') + 3).split(/[/?#]/, 1)[0];
  return url.protocol === 'https:' &&
    url.hostname.length > 0 &&
    !url.username &&
    !url.password &&
    !authority.includes('@') &&
    !url.search &&
    !url.hash;
}

export function isConsentPrompt(value: unknown): value is BrokerConsentPrompt {
  if (!value || typeof value !== 'object') return false;
  const prompt = value as Partial<BrokerConsentPrompt>;
  return prompt.consent_required === true &&
    typeof prompt.consent_handle === 'string' &&
    typeof prompt.client_id === 'string' &&
    Array.isArray(prompt.requested_scopes) &&
    prompt.requested_scopes.every((scope) => typeof scope === 'string') &&
    typeof prompt.csrf_token === 'string';
}

export async function exchangeAuthorizationCode(
  config: AuthorizationConfig,
  transaction: AuthorizationTransaction,
  code: string,
  fetcher: typeof fetch = fetch,
): Promise<BrokerTokenResponse> {
  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: config.clientId,
    code,
    redirect_uri: transaction.redirectUri,
    code_verifier: transaction.verifier,
  });
  const response = await fetcher(new URL('/connect/token', config.brokerUrl), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    credentials: 'include',
    cache: 'no-store',
    body,
  });
  return await readTokenResponse(response);
}

export async function refreshBrokerToken(
  config: AuthorizationConfig,
  refreshToken: string,
  fetcher: typeof fetch = fetch,
): Promise<BrokerTokenResponse> {
  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    client_id: config.clientId,
    refresh_token: refreshToken,
  });
  const response = await fetcher(new URL('/connect/token', config.brokerUrl), {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
    credentials: 'include',
    cache: 'no-store',
    body,
  });
  return await readTokenResponse(response);
}

async function readTokenResponse(response: Response): Promise<BrokerTokenResponse> {
  const result = await response.json().catch(() => ({})) as Record<string, unknown>;
  if (!response.ok || typeof result.access_token !== 'string' ||
      typeof result.expires_in !== 'number' || result.token_type !== 'Bearer') {
    const description = typeof result.error_description === 'string'
      ? result.error_description
      : typeof result.error === 'string'
        ? result.error
        : `Identity Broker token exchange failed with HTTP ${response.status}.`;
    throw new Error(description);
  }
  return result as unknown as BrokerTokenResponse;
}

export async function submitBrokerConsent(
  config: AuthorizationConfig,
  prompt: BrokerConsentPrompt,
  approve: boolean,
  fetcher: typeof fetch = fetch,
): Promise<Response> {
  const response = await fetcher(new URL('/connect/consent', config.brokerUrl), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      'X-CSRF-TOKEN': prompt.csrf_token,
    },
    credentials: 'include',
    cache: 'no-store',
    redirect: 'manual',
    body: JSON.stringify({
      consent_handle: prompt.consent_handle,
      approve,
      scopes: approve ? prompt.requested_scopes : [],
    }),
  });
  if (response.type === 'opaqueredirect' ||
      (response.status >= 300 && response.status < 400))
    return response;
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { error_description?: string; error?: string };
    throw new Error(problem.error_description ?? problem.error ?? `Broker consent failed with HTTP ${response.status}.`);
  }
  return response;
}

export function storeAuthorizationTransaction(
  transaction: AuthorizationTransaction,
  storage: Storage = sessionStorage,
): void {
  storage.setItem(AUTH_TRANSACTION_STORAGE_KEY, JSON.stringify(transaction));
}

export function readAuthorizationTransaction(
  storage: Storage = sessionStorage,
): AuthorizationTransaction | null {
  const raw = storage.getItem(AUTH_TRANSACTION_STORAGE_KEY);
  if (!raw) return null;
  try {
    const transaction = JSON.parse(raw) as AuthorizationTransaction;
    if (typeof transaction.state !== 'string' || typeof transaction.verifier !== 'string' ||
        typeof transaction.redirectUri !== 'string' ||
        (transaction.binding !== null && transaction.binding !== undefined &&
          (typeof transaction.binding.projectId !== 'string' || typeof transaction.binding.runId !== 'string')))
      return null;
    return { ...transaction, binding: transaction.binding ?? null };
  } catch {
    return null;
  }
}

export function clearAuthorizationTransaction(storage: Storage = sessionStorage): void {
  storage.removeItem(AUTH_TRANSACTION_STORAGE_KEY);
}
