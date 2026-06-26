using FluentAssertions;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// S2 — Token lifecycle tests.
///
/// Validates the full PKCE authorization-code flow and token lifecycle:
///   • S256 PKCE happy path: /oauth/authorize → GitHub → /oauth/token → JWT access token + refresh token.
///   • plain PKCE rejected 400 at /oauth/authorize.
///   • Missing code_verifier on /oauth/token → 400.
///   • Mismatched redirect URI → 400.
///   • Expired access token rejected by MCP RS with 401.
///   • Refresh rotation: new access + refresh tokens issued, old refresh invalidated.
///   • Replay of old refresh token → 401 + full chain revoked (reuse detection).
///   • /oauth/revoke kills refresh + denylists access jti.
///   • Revoked access jti rejected by MCP RS before expiry.
///
/// Seraph design ref: §3b (PKCE mandatory S256), §4 (token model), §7 (security threats).
///
/// CURRENT STATUS: All tests are Skip-marked pending Tank T2 (signing/token service),
/// T3 (authorize + token endpoints), T4 (refresh rotation + revoke).
/// Remove [Skip] and implement as each task lands.
/// </summary>
public sealed class OAuthTokenLifecycleTests : IClassFixture<OAuthWebApplicationFactory>
{
    private readonly OAuthWebApplicationFactory _factory;

    public OAuthTokenLifecycleTests(OAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // =========================================================================
    // S2-01 (STUB — requires Tank T2 + T3)
    // Full happy path: S256 PKCE → /oauth/authorize → /oauth/token
    // → JWT access token with correct iss/aud/sub/scope/exp claims.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 (/oauth/authorize + /oauth/token with PKCE) not yet implemented")]
    public async Task HappyPath_S256Pkce_IssuesJwtWithCorrectClaims()
    {
        // Implementation outline (for when T3 lands):
        // 1. POST /oauth/register → ephemeral client_id
        // 2. Build PKCE verifier+challenge (S256)
        // 3. GET /oauth/authorize?client_id=...&code_challenge=...&code_challenge_method=S256
        //    → stub the GitHub callback via test HttpClient override
        // 4. POST /oauth/token code=... code_verifier=...
        // 5. Assert access_token is a valid JWT:
        //    iss == TestIssuer
        //    aud == TestAudience  ("http://localhost/mcp")
        //    sub == test GitHub login
        //    scope == "mcp:invoke"
        //    exp ≈ now + 15min
        //    jti != null
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-02 — plain PKCE at /oauth/authorize → 400 invalid_request.
    // Tank T3: LIVE. Seraph §3b: "code_challenge_methods_supported: ["S256"] ONLY"
    // =========================================================================
    [Fact]
    public async Task PlainPkce_Returns400_InvalidRequest()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&redirect_uri=http://127.0.0.1:9999/cb" +
            "&code_challenge=abc&code_challenge_method=plain");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "plain PKCE must be rejected with 400 at /oauth/authorize");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("S256", "error must mention S256 as the required method");
    }

    // =========================================================================
    // S2-03 — Missing code_challenge at /oauth/authorize → 400.
    // Tank T3: LIVE.
    // =========================================================================
    [Fact]
    public async Task MissingCodeChallenge_Returns400()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&redirect_uri=http://127.0.0.1:9999/cb");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "missing code_challenge must return 400 (PKCE is mandatory)");
    }

    // =========================================================================
    // S2-04 (STUB — requires Tank T3 full flow to obtain a code)
    // Missing code_verifier on POST /oauth/token → 400 invalid_grant.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 full PKCE flow — need a valid code to test /oauth/token rejection")]
    public async Task MissingCodeVerifier_Returns400_InvalidGrant()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-05 (STUB — requires Tank T3 full flow)
    // Wrong code_verifier (doesn't match challenge) → 400 invalid_grant.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 full PKCE flow — need a valid code to test verifier mismatch")]
    public async Task WrongCodeVerifier_Returns400_InvalidGrant()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-06 (STUB — requires Tank T3 full flow)
    // Mismatched redirect_uri on /oauth/token → 400 invalid_grant.
    // Seraph §7: "Redirect URIs exact-match loopback/registered-only".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 full PKCE flow — need a valid code to test redirect_uri mismatch")]
    public async Task MismatchedRedirectUri_Returns400_InvalidGrant()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-07 — Non-loopback HTTP redirect_uri at /oauth/authorize → 400.
    // Tank T3: LIVE. OAuthServerConfig.IsAllowedRedirectUri enforces this.
    // =========================================================================
    [Fact]
    public async Task NonLoopbackRedirectUri_AtAuthorize_Returns400()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test" +
            "&redirect_uri=http://evil.example.com/callback" +
            "&code_challenge=abc&code_challenge_method=S256");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "non-loopback redirect_uri must be rejected (Seraph §7: exact-match loopback/registered-only)");
    }

    // =========================================================================
    // S2-07b (STUB — requires Tank T5 DCR)
    // Non-loopback redirect_uri at /oauth/register → 400.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T5 (DCR) — non-loopback redirect_uri must be rejected at /oauth/register")]
    public async Task NonLoopbackRedirectUri_AtRegistration_Returns400()
    {
        // POST /oauth/register with redirect_uris: ["https://evil.example.com/callback"]
        // Must return 400 invalid_redirect_uri
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-08 (STUB — requires Tank T2 + T6)
    // Authorization code is single-use: re-submitting the same code → 400.
    // Seraph §8 T3 acceptance criterion: "code single-use & ≤60 s".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T3 — authorization code must be single-use")]
    public async Task AuthorizationCode_IsSignleUse_SecondUseReturns400()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-09 (STUB — requires Tank T6)
    // MCP RS rejects an expired JWT access token with 401.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — expired access token must be rejected by MCP RS")]
    public async Task ExpiredAccessToken_MustBeRejected_WithWwwAuthenticate()
    {
        // Mint a JWT with exp = now - 1s (using McpTokenService with test key),
        // send to MCP RS, assert 401 with WWW-Authenticate.
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-10 (STUB — requires Tank T2 + T6)
    // MCP RS rejects a JWT with wrong audience (aud ≠ "https://{HOST}/mcp").
    // Seraph §7: "RS validates iss + aud strictly" (mix-up attack mitigation).
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — wrong aud JWT must be rejected")]
    public async Task WrongAudienceJwt_MustBeRejected_By_McpRs()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-11 (STUB — requires Tank T4)
    // Refresh token rotation: POST /oauth/token with grant_type=refresh_token
    // issues a new access token + new refresh token and invalidates the old refresh.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 (rotating refresh-token store) not yet implemented")]
    public async Task RefreshToken_Rotation_IssuesNewTokens_InvalidatesOldRefresh()
    {
        // 1. Complete PKCE flow → access_token_1, refresh_token_1
        // 2. POST /oauth/token grant_type=refresh_token&refresh_token=refresh_token_1
        //    → access_token_2, refresh_token_2 (access_token_1 still valid until exp, refresh_token_1 now invalid)
        // 3. Replay refresh_token_1 → 400 invalid_grant (rotation = reuse detection)
        // 4. refresh_token_2 is valid; access_token_2 is a proper JWT
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-12 (STUB — requires Tank T4)
    // Refresh-token reuse detection: replaying an already-rotated refresh token
    // revokes the ENTIRE token chain (both the new and old refresh tokens).
    // Seraph §4: "reuse detection revokes the whole chain on replay".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 — reuse detection must revoke entire chain")]
    public async Task RefreshTokenReuse_RevokesEntireChain()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-13 (STUB — requires Tank T4)
    // POST /oauth/revoke with a valid refresh token → 200, token is revoked.
    // Subsequent use of that refresh token → 400.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 — /oauth/revoke (RFC 7009) not yet implemented")]
    public async Task Revoke_RefreshToken_PreventsSubsequentUse()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S2-14 (STUB — requires Tank T4 + T6)
    // POST /oauth/revoke with an access token denylists its jti.
    // Subsequent MCP request with that access token → 401 before token expiry.
    // Seraph §4: "jti denylist on revoke; TTL = access-token lifetime".
    // =========================================================================
    [Fact(Skip = "TODO: Tank T4 (jti denylist) + T6 (MCP RS denylist check) not yet implemented")]
    public async Task Revoke_AccessToken_DenylistsJti_McpRsRejects_BeforeExpiry()
    {
        await Task.CompletedTask;
    }
}
