import { afterEach, describe, expect, it, vi } from 'vitest';

afterEach(() => {
  delete window.__AGENTWEAVER_CONFIG_BASE64__;
  vi.unstubAllEnvs();
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

  it('fails clearly when a deployed build has no Gateway setting', async () => {
    vi.stubEnv('PROD', true);
    await expect(import('./config')).rejects.toThrow('Gateway URL is missing from the web configuration');
  });

  it('rejects a deployed runtime setting that targets the visitor’s localhost', async () => {
    vi.stubEnv('PROD', true);
    window.__AGENTWEAVER_CONFIG_BASE64__ = { GATEWAY_URL: btoa('http://localhost:5000/api/v1') };
    await expect(import('./config')).rejects.toThrow('not localhost');
  });

  it('rejects protocol-relative loopback URLs in a deployed build', async () => {
    vi.stubEnv('PROD', true);
    window.__AGENTWEAVER_CONFIG_BASE64__ = { GATEWAY_URL: btoa('//localhost:5000/api/v1') };
    await expect(import('./config')).rejects.toThrow('not localhost');
  });

  it.each([
    ['/\\localhost:5000/api/v1', 'backslashes that URL parsing could normalize into another host'],
    ['ftp://gateway.example.test/api/v1', 'non-HTTP protocols'],
    ['https://gateway.example.test?x=/api/v1', 'a version-like query string without a versioned path'],
    ['https://[::1', 'malformed absolute URLs'],
  ])('rejects deployed Gateway URL %s (%s)', async (value) => {
    vi.stubEnv('PROD', true);
    window.__AGENTWEAVER_CONFIG_BASE64__ = { GATEWAY_URL: btoa(value) };
    await expect(import('./config')).rejects.toThrow('Gateway URL');
  });

  it('accepts a same-origin v1 path in a deployed build', async () => {
    vi.stubEnv('PROD', true);
    window.__AGENTWEAVER_CONFIG_BASE64__ = { GATEWAY_URL: btoa('/api/v1') };
    const config = await import('./config');
    expect(config.gatewayBaseUrl).toBe('/api/v1');
  });

  it('accepts an absolute deployed Gateway URL with the versioned path', async () => {
    vi.stubEnv('PROD', true);
    window.__AGENTWEAVER_CONFIG_BASE64__ = { GATEWAY_URL: btoa('https://gateway.example.test/api/v1/') };
    const config = await import('./config');
    expect(config.gatewayBaseUrl).toBe('https://gateway.example.test/api/v1');
  });
});
