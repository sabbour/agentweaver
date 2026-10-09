import { afterEach, describe, expect, it, vi } from 'vitest';

afterEach(() => {
  delete window.__AGENTWEAVER_CONFIG_BASE64__;
  vi.resetModules();
});

describe('web runtime configuration', () => {
  it('uses the runtime Gateway and Broker settings injected by the static image', async () => {
    window.__AGENTWEAVER_CONFIG_BASE64__ = {
      GATEWAY_URL: btoa('https://gateway.example.test/api/v1/'),
      IDENTITY_BROKER_URL: btoa('https://identity.example.test/'),
      IDENTITY_BROKER_ISSUER: btoa('https://identity.example.test/'),
      OAUTH_CLIENT_ID: btoa('agentweaver-web'),
      OAUTH_REDIRECT_URI: btoa('https://app.example.test/auth/callback'),
      OAUTH_SCOPES: btoa('projects.read runs.read'),
    };

    const config = await import('./config');

    expect(config.gatewayBaseUrl).toBe('https://gateway.example.test/api/v1');
    expect(config.brokerBaseUrl).toBe('https://identity.example.test');
    expect(config.brokerIssuer).toBe('https://identity.example.test/');
    expect(config.oauthClientId).toBe('agentweaver-web');
    expect(config.oauthRedirectUri).toBe('https://app.example.test/auth/callback');
    expect(config.oauthScopes).toEqual(['projects.read', 'runs.read']);
    expect(config.missingAuthConfiguration()).toEqual([]);
  });

  it('derives the HTTPS Broker issuer with its canonical trailing slash', async () => {
    window.__AGENTWEAVER_CONFIG_BASE64__ = {
      IDENTITY_BROKER_URL: btoa('https://identity.example.test:8443/broker/'),
    };

    const config = await import('./config');

    expect(config.brokerBaseUrl).toBe('https://identity.example.test:8443/broker');
    expect(config.brokerIssuer).toBe('https://identity.example.test:8443/broker/');
  });

  it('uses the same-origin Gateway and callback defaults when no runtime config is present', async () => {
    vi.resetModules();
    const config = await import('./config');

    expect(config.gatewayBaseUrl).toBe('/api/v1');
    expect(config.brokerIssuer).toBe('');
    expect(config.oauthRedirectUri).toBe(`${window.location.origin}/auth/callback`);
  });
});
