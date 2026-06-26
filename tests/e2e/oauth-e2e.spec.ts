import { test, expect } from '@playwright/test';

/**
 * S5 — End-to-End Copilot CLI Auto-Refresh Tests
 *
 * Goal: Verify that Copilot CLI can auto-discover the Authorization Server,
 * complete a PKCE browser flow, call /mcp successfully, and silently refresh
 * when the access token expires — no re-prompt to the user.
 *
 * Staging URL: https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io/mcp
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * MANUAL RUNBOOK (execute after Tank T1-T6 + Link L1-L3 are deployed to staging)
 * ─────────────────────────────────────────────────────────────────────────────
 *
 * Prerequisites:
 *   - gh CLI installed and authenticated as a microsoft org member
 *   - Copilot CLI (or VS Code with Copilot extension) installed
 *   - Network access to the staging AKS ingress
 *
 * Step 1 — Confirm discovery endpoints are reachable:
 *   curl -sf https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io/.well-known/oauth-protected-resource | jq .
 *   # Expect: {"resource":"https://.../mcp","authorization_servers":["https://..."]}
 *
 *   curl -sf https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io/.well-known/oauth-authorization-server | jq .
 *   # Expect: RFC 8414 document with issuer, authorization_endpoint, token_endpoint, jwks_uri, registration_endpoint
 *
 * Step 2 — Trigger MCP discovery (no token):
 *   curl -si https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io/mcp
 *   # Expect: HTTP 401
 *   #         WWW-Authenticate: Bearer realm="agentweaver-mcp", resource_metadata="https://.../.well-known/oauth-protected-resource"
 *
 * Step 3 — Register a public client (DCR):
 *   curl -sf -X POST \
 *     https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io/oauth/register \
 *     -H 'Content-Type: application/json' \
 *     -d '{"redirect_uris":["http://localhost:12345/callback"],"token_endpoint_auth_method":"none"}' | jq .
 *   # Expect: {"client_id":"<ephemeral-id>","redirect_uris":["http://localhost:12345/callback"]}
 *
 * Step 4 — Complete PKCE flow (manual browser login):
 *   a. Generate code_verifier (43-128 char random string) and code_challenge = base64url(sha256(verifier))
 *   b. Open in browser:
 *      https://agentweaver.../oauth/authorize?client_id=<id>&redirect_uri=http://localhost:12345/callback&scope=mcp:invoke+offline_access&response_type=code&code_challenge=<challenge>&code_challenge_method=S256&state=<random>
 *   c. Log in with a microsoft org GitHub account
 *   d. Capture the `code` parameter from the loopback redirect
 *
 * Step 5 — Exchange code for tokens:
 *   curl -sf -X POST https://agentweaver.../oauth/token \
 *     -H 'Content-Type: application/x-www-form-urlencoded' \
 *     -d 'grant_type=authorization_code&client_id=<id>&code=<code>&redirect_uri=http://localhost:12345/callback&code_verifier=<verifier>' | jq .
 *   # Expect: {"access_token":"<JWT>","token_type":"bearer","expires_in":900,"refresh_token":"<opaque>","scope":"mcp:invoke offline_access"}
 *
 * Step 6 — Call /mcp with the access token:
 *   curl -sf -X POST https://agentweaver.../mcp \
 *     -H "Authorization: Bearer <access_token>" \
 *     -H 'Content-Type: application/json' \
 *     -d '{"jsonrpc":"2.0","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}},"id":1}' | jq .
 *   # Expect: 200 with {"jsonrpc":"2.0","result":{"protocolVersion":"2024-11-05",...},"id":1}
 *
 * Step 7 — Verify silent refresh after token expiry:
 *   a. Wait for access_token to expire (or configure a test AS with 10s TTL)
 *   b. POST /oauth/token grant_type=refresh_token&refresh_token=<rt>&client_id=<id>
 *   c. Expect new access_token + new refresh_token; old refresh_token now invalid
 *   d. Call /mcp again with new access_token → 200 (no re-prompt to user)
 *
 * Step 8 — Verify non-member is denied:
 *   a. Log in as a GitHub user who is NOT in the microsoft org
 *   b. Complete the browser flow
 *   c. Expect: 403 {"error":"access_denied"} — no token issued
 *
 * Step 9 — Verify CI static API key still works:
 *   curl -sf -X POST https://agentweaver.../mcp \
 *     -H "Authorization: Bearer <CI_API_KEY>" \
 *     -H 'Content-Type: application/json' \
 *     -d '{"jsonrpc":"2.0","method":"initialize",...}' | jq .
 *   # Expect: 200 (static key fast path, S4 backward compat)
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * AUTOMATABLE HARNESS (below)
 * All tests are skipped until T1-T6 + L1-L3 are deployed to staging.
 * ─────────────────────────────────────────────────────────────────────────────
 */

const STAGING_HOST = 'https://agentweaver.6a3de4fe60529400010f3fba.westus2.staging.aksapp.io';
const WELL_KNOWN_AS  = `${STAGING_HOST}/.well-known/oauth-authorization-server`;
const WELL_KNOWN_PR  = `${STAGING_HOST}/.well-known/oauth-protected-resource`;
const WELL_KNOWN_PR_SUFFIXED = `${STAGING_HOST}/.well-known/oauth-protected-resource/mcp`;
const MCP_URL        = `${STAGING_HOST}/mcp`;
const JWKS_URL       = `${STAGING_HOST}/oauth/jwks`;

// Skip tag: remove when T1-T6 and L1-L3 are deployed to staging.
test.describe('S5 — E2E MCP OAuth 2.1 (staging)', () => {
  // =========================================================================
  // S5-01 — AS metadata is reachable and well-formed (S1 smoke on staging)
  // =========================================================================
  test.skip('AS metadata document is valid RFC 8414', async ({ request }) => {
    // TODO: remove skip when Tank T1 + Link L1 deployed
    const res = await request.get(WELL_KNOWN_AS);
    expect(res.status()).toBe(200);
    const doc = await res.json();

    expect(doc.issuer).toBe(STAGING_HOST);
    expect(doc.authorization_endpoint).toContain('/oauth/authorize');
    expect(doc.token_endpoint).toContain('/oauth/token');
    expect(doc.registration_endpoint).toContain('/oauth/register');
    expect(doc.jwks_uri).toContain('/oauth/jwks');
    expect(doc.code_challenge_methods_supported).toEqual(['S256']);
    expect(doc.code_challenge_methods_supported).not.toContain('plain');
  });

  // =========================================================================
  // S5-02 — Protected-resource metadata is reachable (root path)
  // =========================================================================
  test.skip('Protected-resource metadata (root) is valid RFC 9728', async ({ request }) => {
    // TODO: remove skip when Tank T6 + Link L1 deployed
    const res = await request.get(WELL_KNOWN_PR);
    expect(res.status()).toBe(200);
    const doc = await res.json();

    expect(doc.resource).toBe(`${STAGING_HOST}/mcp`);
    expect(doc.authorization_servers).toContain(STAGING_HOST);
    expect(doc.bearer_methods_supported).toContain('header');
  });

  // =========================================================================
  // S5-03 — Protected-resource metadata suffixed path also works
  // =========================================================================
  test.skip('Protected-resource metadata (/mcp suffix) is reachable', async ({ request }) => {
    // TODO: remove skip when Tank T6 deployed
    const res = await request.get(WELL_KNOWN_PR_SUFFIXED);
    expect(res.status()).toBe(200);
  });

  // =========================================================================
  // S5-04 — MCP with no token returns 401 + WWW-Authenticate (discovery trigger)
  // =========================================================================
  test.skip('MCP without token returns 401 + WWW-Authenticate discovery header', async ({ request }) => {
    // TODO: remove skip when Tank T6 deployed
    const res = await request.post(MCP_URL, {
      data: { jsonrpc: '2.0', method: 'initialize', params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'smith-test', version: '0' } }, id: 1 },
      headers: { 'Content-Type': 'application/json' },
      failOnStatusCode: false,
    });
    expect(res.status()).toBe(401);
    const wwwAuth = res.headers()['www-authenticate'] ?? '';
    expect(wwwAuth).toContain('Bearer');
    expect(wwwAuth).toContain('resource_metadata=');
    expect(wwwAuth).toContain('/.well-known/oauth-protected-resource');
  });

  // =========================================================================
  // S5-05 — JWKS endpoint is reachable and well-formed
  // =========================================================================
  test.skip('JWKS endpoint has at least one key', async ({ request }) => {
    // TODO: remove skip when Tank T1 + Link L2 deployed
    const res = await request.get(JWKS_URL);
    expect(res.status()).toBe(200);
    const doc = await res.json();
    expect(doc.keys).toBeDefined();
    expect(doc.keys.length).toBeGreaterThan(0);
    expect(doc.keys[0].kid).toBeTruthy();
  });

  // =========================================================================
  // S5-06 — Full PKCE flow + MCP call (requires human-in-the-loop browser step)
  //          Automated when run with OAUTH_TEST_CODE env var set (CI gate).
  // =========================================================================
  test.skip('Full PKCE flow issues JWT that authenticates /mcp', async ({ request }) => {
    // TODO: remove skip when Tank T3 + Link L1 deployed
    // Automated path:
    //   1. POST /oauth/register → client_id
    //   2. Open authorize URL in browser (manual or Playwright page.goto)
    //   3. Capture code from loopback redirect
    //   4. POST /oauth/token with code + verifier
    //   5. POST /mcp with access_token → 200
    // See MANUAL RUNBOOK above for detailed steps.
  });

  // =========================================================================
  // S5-07 — Silent access-token refresh (no re-prompt)
  // =========================================================================
  test.skip('Access-token expiry triggers silent refresh without re-prompt', async ({ request }) => {
    // TODO: remove skip when Tank T4 deployed
    // Requires a test AS configuration with very short token TTL (e.g., 5s).
    // See MANUAL RUNBOOK Step 7.
  });

  // =========================================================================
  // S5-08 — Non-org user denied at token issuance (staging, real GitHub check)
  // =========================================================================
  test.skip('Non-microsoft-org user is denied 403 at token issuance', async ({ request }) => {
    // TODO: remove skip when Tank T3 deployed
    // Requires a test GitHub account not in the microsoft org.
  });

  // =========================================================================
  // S5-09 — CI static API key still works after OAuth changes deployed
  // =========================================================================
  test.skip('CI static API key authenticates /mcp after OAuth deployment', async ({ request }) => {
    // TODO: remove skip when all Tank + Link tasks deployed
    // Reads CI_API_KEY from environment.
    const ciKey = process.env['CI_AGENTWEAVER_API_KEY'];
    if (!ciKey) test.skip(); // no key configured

    const res = await request.post(MCP_URL, {
      data: { jsonrpc: '2.0', method: 'initialize', params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'ci-compat-test', version: '0' } }, id: 1 },
      headers: {
        'Authorization': `Bearer ${ciKey}`,
        'Content-Type': 'application/json',
      },
    });
    expect(res.status()).toBe(200);
  });
});
