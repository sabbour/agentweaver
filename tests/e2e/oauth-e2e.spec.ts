import { test, expect } from '@playwright/test';

const STAGING_HOST =
  process.env.AKS_BASE_URL ??
  'https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io';
const MCP_URL = `${STAGING_HOST}/mcp`;

test.describe('MCP OAuth 2.1 broker cutover (staging)', () => {
  test.skip('public metadata and missing-token challenge agree', async ({ request }) => {
    const metadata = await request.get(
      `${STAGING_HOST}/.well-known/oauth-protected-resource/mcp`,
    );
    expect(metadata.status()).toBe(200);
    const document = await metadata.json();
    expect(document.resource).toBe(MCP_URL);
    expect(document.authorization_servers).toEqual([`${STAGING_HOST}/`]);
    expect(document.scopes_supported).toEqual(['mcp:invoke']);

    const response = await request.post(MCP_URL, {
      data: {
        jsonrpc: '2.0',
        method: 'initialize',
        params: {
          protocolVersion: '2025-03-26',
          capabilities: {},
          clientInfo: { name: 'broker-cutover-test', version: '1' },
        },
        id: 1,
      },
      failOnStatusCode: false,
    });
    expect(response.status()).toBe(401);
    expect(response.headers()['www-authenticate']).toBe(
      `Bearer resource_metadata="${STAGING_HOST}/.well-known/oauth-protected-resource/mcp", scope="mcp:invoke"`,
    );
  });

  test.skip('AS metadata and JWKS are publicly reachable', async ({ request }) => {
    for (const path of [
      '/.well-known/oauth-authorization-server',
      '/.well-known/openid-configuration',
    ]) {
      const response = await request.get(`${STAGING_HOST}${path}`);
      expect(response.status()).toBe(200);
      const document = await response.json();
      expect(document.issuer).toBe(`${STAGING_HOST}/`);
      expect(document.jwks_uri).toBe(`${STAGING_HOST}/oauth/jwks`);
    }

    const jwks = await request.get(`${STAGING_HOST}/oauth/jwks`);
    expect(jwks.status()).toBe(200);
    expect((await jwks.json()).keys[0].kid).toBeTruthy();
  });

  test.skip('full PKCE flow issues a broker JWT accepted by MCP', async () => {
    // The live harness completes browser consent, initializes MCP, lists tools,
    // invokes a read-only tool, refreshes, and checks invalid-token rejection.
  });
});
