const environment = import.meta.env;

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

function runtimeSetting(key: RuntimeConfigKey): string | undefined {
  const encoded = typeof window === 'undefined'
    ? undefined
    : window.__AGENTWEAVER_CONFIG_BASE64__?.[key];
  if (!encoded) return undefined;
  const binary = atob(encoded);
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  return new TextDecoder().decode(bytes) || undefined;
}

function trimTrailingSlash(value: string): string {
  return value.replace(/\/+$/, '');
}

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

export const gatewayBaseUrl = trimTrailingSlash(
  runtimeSetting('GATEWAY_URL') ?? environment.VITE_GATEWAY_URL ?? '/api/v1',
);
export const brokerBaseUrl = trimTrailingSlash(brokerUrlSetting);
export const brokerIssuer =
  runtimeSetting('IDENTITY_BROKER_ISSUER') ??
  (environment.VITE_IDENTITY_BROKER_ISSUER || defaultBrokerIssuer(brokerUrlSetting));
export const oauthClientId =
  runtimeSetting('OAUTH_CLIENT_ID') ?? environment.VITE_OAUTH_CLIENT_ID ?? '';
export const oauthRedirectUri =
  runtimeSetting('OAUTH_REDIRECT_URI') ??
  environment.VITE_OAUTH_REDIRECT_URI ??
  (typeof window === 'undefined' ? '' : `${window.location.origin}/auth/callback`);
export const oauthScopes = (runtimeSetting('OAUTH_SCOPES') ?? environment.VITE_OAUTH_SCOPES ?? '')
  .split(/\s+/)
  .map((scope: string) => scope.trim())
  .filter(Boolean);

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
