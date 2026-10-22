/** Reads runtime Gateway and Identity Broker settings for the v1 browser client. */
const environment = import.meta.env;

/** Environment and runtime-injected keys that may be supplied by the static host. */
type RuntimeConfigKey =
  | 'GATEWAY_URL'
  | 'IDENTITY_BROKER_URL'
  | 'IDENTITY_BROKER_ISSUER'
  | 'OAUTH_CLIENT_ID'
  | 'OAUTH_REDIRECT_URI'
  | 'OAUTH_SCOPES';

declare global {
  interface Window {
    __AGENTWEAVER_CONFIG_BASE64__?: Partial<Record<RuntimeConfigKey, string>>;
  }
}

/** Decodes one base64-encoded host setting without exposing runtime config parsing to callers. */
function runtimeSetting(key: RuntimeConfigKey): string | undefined {
  const encoded = typeof window === 'undefined'
    ? undefined
    : window.__AGENTWEAVER_CONFIG_BASE64__?.[key];
  if (!encoded) return undefined;
  const binary = atob(encoded);
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  return new TextDecoder().decode(bytes) || undefined;
}

/** Removes trailing separators so callers can append API paths consistently. */
function trimTrailingSlash(value: string): string {
  return value.replace(/\/+$/, '');
}

/** Accepts only a canonical HTTPS Broker URL as a default OIDC issuer. */
function defaultBrokerIssuer(value: string): string {
  if (!value) return '';
  try {
    const url = new URL(value);
    const authority = value.slice(value.indexOf(':') + 3).split(/[/?#]/, 1)[0];
    return url.protocol === 'https:' &&
      !url.username &&
      !url.password &&
      !authority.includes('@') &&
      !value.includes('?') &&
      !value.includes('#')
      ? url.href
      : '';
  } catch {
    return '';
  }
}

const brokerUrlSetting =
  runtimeSetting('IDENTITY_BROKER_URL') ?? environment.VITE_IDENTITY_BROKER_URL ?? '';

/** Resolves the versioned Gateway URL and rejects a missing or local production target. */
function resolveGatewayBaseUrl(): string {
  // Runtime settings take precedence over build-time values, as on the static Web host.
  const configured = runtimeSetting('GATEWAY_URL') ?? environment.VITE_GATEWAY_URL;
  if (!configured) {
    // The development server can proxy the default path; a deployed build must have a configured target.
    if (environment.PROD) throw new Error('Gateway URL is missing from the web configuration. Ask your administrator to check the deployment.');
    return '/api/v1';
  }
  const base = trimTrailingSlash(configured);
  if (environment.PROD) {
    // Production accepts only an absolute URL or a same-origin path; backslashes can be normalized into a new host.
    const absolute = /^[a-z][a-z\d+\-.]*:\/\//i.test(base);
    const rootRelative = base.startsWith('/') && !base.startsWith('//');
    const invalidMessage = 'Gateway URL must point to a valid deployed HTTP(S) /api/v1 endpoint, not localhost.';
    let target: URL;
    try {
      // Parse the URL before checking its path so query text cannot masquerade as the /api/v1 suffix.
      if ((!absolute && !rootRelative) || base.includes('\\')) throw new Error(invalidMessage);
      target = absolute
        ? new URL(base)
        : new URL(base, typeof window === 'undefined' ? 'https://gateway.invalid' : window.location.origin);
    } catch {
      // Keep malformed runtime values inside the same named startup configuration error.
      throw new Error(invalidMessage);
    }
    const hostname = target.hostname.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
    const local = absolute && (
      hostname === 'localhost' || hostname.endsWith('.localhost')
      || hostname.startsWith('127.') || hostname === '0.0.0.0'
      || hostname === '::1' || hostname.startsWith('::ffff:127.')
    );
    if (
      !['http:', 'https:'].includes(target.protocol)
      || !target.pathname.endsWith('/api/v1')
      || base.includes('?')
      || base.includes('#')
      || target.username
      || target.password
      || local
    ) throw new Error(invalidMessage);
  }
  return base;
}

/** Gateway origin/path, preferring host-injected settings over build-time values. */
export const gatewayBaseUrl = resolveGatewayBaseUrl();
/** Identity Broker base URL used by the OAuth client. */
export const brokerBaseUrl = trimTrailingSlash(brokerUrlSetting);
/** OIDC issuer from explicit configuration or a validated HTTPS Broker URL. */
export const brokerIssuer =
  runtimeSetting('IDENTITY_BROKER_ISSUER') ??
  (environment.VITE_IDENTITY_BROKER_ISSUER || defaultBrokerIssuer(brokerUrlSetting));
/** Registered OAuth client identifier configured by the host or build. */
export const oauthClientId =
  runtimeSetting('OAUTH_CLIENT_ID') ?? environment.VITE_OAUTH_CLIENT_ID ?? '';
/** Registered callback URI, defaulting to this browser origin's callback route. */
export const oauthRedirectUri =
  runtimeSetting('OAUTH_REDIRECT_URI') ??
  environment.VITE_OAUTH_REDIRECT_URI ??
  (typeof window === 'undefined' ? '' : `${window.location.origin}/auth/callback`);
/** Whitespace-separated OAuth scopes normalized to individual values. */
export const oauthScopes = (runtimeSetting('OAUTH_SCOPES') ?? environment.VITE_OAUTH_SCOPES ?? '')
  .split(/\s+/)
  .map((scope: string) => scope.trim())
  .filter(Boolean);

/** Lists required Broker and OAuth settings that are absent from this browser configuration. */
export function missingAuthConfiguration(): string[] {
  return [
    ['Identity Broker URL', brokerBaseUrl],
    ['registered OAuth client ID', oauthClientId],
    ['registered OAuth redirect URI', oauthRedirectUri],
    ['registered OAuth scopes', oauthScopes.length > 0 ? 'configured' : ''],
  ]
    .filter(([, value]) => !value)
    .map(([label]) => label as string);
}
