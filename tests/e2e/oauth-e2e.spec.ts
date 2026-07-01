import { test, expect } from '@playwright/test';

/**
 * S5 — End-to-End Copilot CLI Auto-Refresh Tests
 *
 * Goal: verify user-visible/authenticated OAuth behavior: MCP challenge,
 * dynamic client registration, PKCE token issuance, MCP invocation, refresh,
 * org enforcement, and static API-key compatibility.
 *
 * Metadata-only reachability checks are intentionally excluded from release
 * validation; they do not prove an authenticated Copilot/MCP flow works.
 */

const STAGING_HOST =
  process.env.AKS_BASE_URL ??
  'https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io';
const MCP_URL = `${STAGING_HOST}/mcp`;
const JWKS_URL = `${STAGING_HOST}/oauth/jwks`;

test.describe('S5 — E2E MCP OAuth 2.1 (staging)', () => {
  test.skip('MCP without token returns 401 + WWW-Authenticate discovery header', async ({ request }) => {
    const res = await request.post(MCP_URL, {
      data: {
        jsonrpc: '2.0',
        method: 'initialize',
        params: {
          protocolVersion: '2024-11-05',
          capabilities: {},
          clientInfo: { name: 'smith-test', version: '0' },
        },
        id: 1,
      },
      headers: { 'Content-Type': 'application/json' },
      failOnStatusCode: false,
    });

    expect(res.status()).toBe(401);
    const wwwAuth = res.headers()['www-authenticate'] ?? '';
    expect(wwwAuth).toContain('Bearer');
    expect(wwwAuth).toContain('resource_metadata=');
  });

  test.skip('JWKS endpoint exposes signing keys for issued MCP tokens', async ({ request }) => {
    const res = await request.get(JWKS_URL);
    expect(res.status()).toBe(200);
    const doc = await res.json();
    expect(doc.keys).toBeDefined();
    expect(doc.keys.length).toBeGreaterThan(0);
    expect(doc.keys[0].kid).toBeTruthy();
  });

  test.skip('Full PKCE flow issues JWT that authenticates /mcp', async () => {
    // Manual/CI harness: register public client, complete GitHub browser login,
    // exchange authorization code, then POST /mcp with the issued access token.
  });

  test.skip('Access-token expiry triggers silent refresh without re-prompt', async () => {
    // Requires a test AS configuration with a short access-token TTL.
  });

  test.skip('Non-microsoft-org user is denied 403 at token issuance', async () => {
    // Requires a test GitHub account outside the microsoft org.
  });

  test.skip('CI static API key authenticates /mcp after OAuth deployment', async ({ request }) => {
    const ciKey = process.env.CI_AGENTWEAVER_API_KEY;
    if (!ciKey) test.skip(true, 'CI_AGENTWEAVER_API_KEY not configured');

    const res = await request.post(MCP_URL, {
      data: {
        jsonrpc: '2.0',
        method: 'initialize',
        params: {
          protocolVersion: '2024-11-05',
          capabilities: {},
          clientInfo: { name: 'ci-compat-test', version: '0' },
        },
        id: 1,
      },
      headers: {
        Authorization: `Bearer ${ciKey}`,
        'Content-Type': 'application/json',
      },
    });

    expect(res.status()).toBe(200);
  });
});
