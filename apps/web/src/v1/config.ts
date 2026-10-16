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

/** Gateway origin/path, preferring host-injected settings over build-time values. */
export const gatewayBaseUrl = trimTrailingSlash(
  runtimeSetting('GATEWAY_URL') ?? environment.VITE_GATEWAY_URL ?? '/api/v1',
);
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
