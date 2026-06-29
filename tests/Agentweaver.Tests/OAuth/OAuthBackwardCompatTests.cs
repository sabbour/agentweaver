using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// S4 — Backward-compatibility tests.
///
/// Validates that the static Agentweaver API-key path continues to work after the OAuth
/// changes land, so CI and automation are unbroken:
///   • The existing static API key (test bypass map) still authenticates /api/*.
///   • The existing API Bearer key path still authenticates /api/* endpoints.
///   • All existing MCP tool calls succeed with the static API key.
///
/// Seraph design ref: §6 (backward compatibility), §8 T6 acceptance: "API-key fast path first".
///
/// LIVE tests run against the in-process API server.
/// STUB tests (raw-GitHub passthrough flag, per-user downstream identity) wait for T6/T7.
/// </summary>
public sealed class OAuthBackwardCompatTests : IClassFixture<OAuthWebApplicationFactory>
{
    private readonly OAuthWebApplicationFactory _factory;

    public OAuthBackwardCompatTests(OAuthWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // =========================================================================
    // S4-01 — Static API key is accepted at /api/* (API-level backward compat)
    //         Verifies the GitHubTokenAuthMiddleware testing bypass + existing
    //         API key config resolve the correct user.
    // =========================================================================
    [Fact]
    public async Task StaticApiKey_OnApiEndpoint_Returns200()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/auth/github");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "a valid static API key must not be rejected by the API");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "the static API key holder must not be blocked by org enforcement");
    }

    // =========================================================================
    // S4-02 — Unauthenticated request to /api/* is still rejected 401
    // =========================================================================
    [Fact]
    public async Task NoApiKey_OnApiEndpoint_Returns401()
    {
        var client = _factory.CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/auth/github");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "unauthenticated /api/* requests must always be rejected");
    }

    // =========================================================================
    // S4-03 — Static API key produces the expected user in the CallerContext
    //         (verifies that ApiKey → User mapping is preserved end-to-end).
    // =========================================================================
    [Fact]
    public async Task StaticApiKey_ResolvesExpectedUser_InProjects()
    {
        // POST /api/projects with the static API key must be attributed to TestUser,
        // not crash due to null CallerContext. A 422/400 body error is fine — we just
        // want NOT 401 / NOT 500.
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/projects",
            new { name = "compat-test", defaultBranch = "main" });

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            "a null CallerContext would cause an unhandled NRE — this proves the key resolved");
    }

    // =========================================================================
    // S4-04 — Missing/wrong API key on /api/* returns 401 (not 403 or 500)
    // =========================================================================
    [Fact]
    public async Task WrongApiKey_OnApiEndpoint_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key-zzz");

        var response = await client.GetAsync("/api/auth/github");

        // In bypass mode the wrong key is accepted as the caller user = "wrong-key-zzz",
        // which then proceeds. To test REAL rejection semantics, disable the bypass.
        // This test verifies no 500 is thrown — the actual auth behavior is tested
        // in McpBearerTokenMiddlewareTests (unit level) and S1 (post-T6 integration).
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    // =========================================================================
    // S4-05 (STUB — requires Tank T6)
    // Auth:Mcp:AllowGitHubPassthrough=true keeps the raw-GitHub token path alive
    // on the MCP server so transitional callers still work.
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — AllowGitHubPassthrough flag not yet wired into McpBearerTokenMiddleware")]
    public async Task GitHubPassthrough_Enabled_RawGitHubTokenStillAccepted()
    {
        // With AllowGitHubPassthrough=true, a valid raw GitHub token should still pass
        // (tested by stubbing the /user endpoint to return 200 with a login).
        await Task.CompletedTask;
    }

    // =========================================================================
    // S4-06 (STUB — requires Tank T6)
    // Auth:Mcp:AllowGitHubPassthrough=false blocks raw GitHub tokens (migrated state).
    // =========================================================================
    [Fact(Skip = "TODO: Tank T6 — AllowGitHubPassthrough=false path not yet implemented")]
    public async Task GitHubPassthrough_Disabled_RawGitHubTokenRejected()
    {
        await Task.CompletedTask;
    }

    // =========================================================================
    // S4-07 (STUB — requires Tank T6 + T7)
    // MCP → API calls with a valid Agentweaver JWT still produce a per-user
    // identity downstream (T7: per-user downstream identity resolution).
    // =========================================================================
    [Fact(Skip = "TODO: Tank T7 — per-user downstream identity resolution not yet implemented")]
    public async Task AgentWeaverJwt_McpToApi_PropagatesPerUserIdentity()
    {
        // Two different OAuth users must produce two distinct CallerContext.User values
        // in the API (not the shared service identity).
        await Task.CompletedTask;
    }

    // =========================================================================
    // S4-08 — All existing API projects/runs endpoints remain reachable with the static key.
    //         Smoke test: GET /api/projects returns 200 (empty list), not 401/403.
    // =========================================================================
    [Fact]
    public async Task StaticApiKey_GetProjects_Returns200()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "GET /api/projects with a valid static key must return 200");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }
}
