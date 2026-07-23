using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// Integration tests for the FAIL-CLOSED org re-check on the <c>/oauth/token</c> refresh grant
/// (Seraph findings-api-data Alert 3 / findings-auth Alert 4).
///
/// A user removed from the required org must NOT be able to keep minting access tokens through the
/// refresh chain by revoking/expiring their GitHub token or by racing a GitHub outage. The refresh
/// endpoint therefore denies when the org membership cannot be positively re-confirmed:
///   • GitHub token missing (revoked/expired, provider returns null) → 403, token NOT consumed.
///   • Membership INCONCLUSIVE (GitHub unreachable / rate-limited)    → 403, token NOT consumed.
///   • Membership DENIED / OrgAccessNotGranted                       → 403, refresh chain revoked.
///   • Membership ALLOWED                                            → 200, rotated tokens issued.
/// </summary>
public sealed class OAuthRefreshOrgRevalidationTests
{
    private const string ClientId = "client-refresh-test";
    private const string Login = "octocat";

    // -------------------------------------------------------------------------
    [Fact]
    public async Task Refresh_WhenGitHubTokenMissing_FailsClosed_AndDoesNotConsumeToken()
    {
        await using var h = new Harness(orgResult: OrgAuthResult.Allowed, gitHubToken: null);
        var token = await h.SeedRefreshTokenAsync();

        var resp = await h.PostRefreshAsync(token);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a missing GitHub token means membership cannot be re-verified — fail closed");
        (await h.IsTokenConsumedOrRevokedAsync(token)).Should().BeFalse(
            "a fail-closed denial must not consume/revoke the token, so a later valid refresh can succeed");
    }

    [Fact]
    public async Task Refresh_WhenMembershipInconclusive_FailsClosed_AndDoesNotConsumeToken()
    {
        await using var h = new Harness(orgResult: OrgAuthResult.Inconclusive, gitHubToken: "ghu_live");
        var token = await h.SeedRefreshTokenAsync();

        var resp = await h.PostRefreshAsync(token);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an inconclusive membership re-check must fail closed, not fall back to the issuance-time claim");
        (await h.IsTokenConsumedOrRevokedAsync(token)).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_WhenMembershipDenied_Returns403_AndRevokesChain()
    {
        await using var h = new Harness(orgResult: OrgAuthResult.Denied, gitHubToken: "ghu_live");
        var token = await h.SeedRefreshTokenAsync();

        var resp = await h.PostRefreshAsync(token);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("access_denied");
        (await h.IsTokenConsumedOrRevokedAsync(token)).Should().BeTrue(
            "a definitive non-membership must revoke the refresh chain");
    }

    [Fact]
    public async Task Refresh_WhenMembershipAllowed_Returns200_WithRotatedTokens()
    {
        await using var h = new Harness(orgResult: OrgAuthResult.Allowed, gitHubToken: "ghu_live");
        var token = await h.SeedRefreshTokenAsync();

        var resp = await h.PostRefreshAsync(token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("access_token").And.Contain("refresh_token");
    }

    [Fact]
    public async Task Refresh_FailClosedThenRecovered_SucceedsWithSameToken()
    {
        // Simulate a transient GitHub outage (inconclusive) followed by recovery (allowed) on the
        // SAME refresh token — proving the fail-closed path left the token usable.
        await using var h = new Harness(orgResult: OrgAuthResult.Inconclusive, gitHubToken: "ghu_live");
        var token = await h.SeedRefreshTokenAsync();

        (await h.PostRefreshAsync(token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        h.OrgAuth.Result = OrgAuthResult.Allowed; // GitHub recovers
        (await h.PostRefreshAsync(token)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the previously fail-closed token must still be redeemable once membership can be confirmed");
    }

    // -------------------------------------------------------------------------
    // Test harness: an OAuth app wired with controllable org-auth + GitHub-token stubs and
    // Auth:GitHub:AllowedOrg set so the endpoint's org re-check path is active.
    // -------------------------------------------------------------------------
    private sealed class Harness : IAsyncDisposable
    {
        private readonly OAuthWebApplicationFactory _baseFactory = new();
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public StubOrgAuth OrgAuth { get; } = new();
        public StubTokenProvider TokenProvider { get; } = new();

        public Harness(OrgAuthResult orgResult, string? gitHubToken)
        {
            OrgAuth.Result = orgResult;
            TokenProvider.Token = gitHubToken;

            _factory = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:GitHub:AllowedOrg"] = "microsoft",
                    }));

                builder.ConfigureServices(services =>
                {
                    Replace<IGitHubOrgAuthorizationService>(services, OrgAuth);
                    Replace<IGitHubAccessTokenProvider>(services, TokenProvider);
                });
            });

            _client = _factory.CreateClient();
        }

        public async Task<string> SeedRefreshTokenAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<McpRefreshTokenStore>();
            return await store.IssueAsync(new McpRefreshGrant(Login, Login, ClientId, "mcp:invoke", "microsoft"));
        }

        public Task<HttpResponseMessage> PostRefreshAsync(string refreshToken) =>
            _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
            }));

        public async Task<bool> IsTokenConsumedOrRevokedAsync(string plaintext)
        {
            var hash = HashToken(plaintext);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.McpRefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash);
            return row is null || row.ConsumedAt is not null || row.RevokedAt is not null;
        }

        private static string HashToken(string token) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token)));

        private static void Replace<T>(IServiceCollection services, T instance) where T : class
        {
            for (var i = services.Count - 1; i >= 0; i--)
                if (services[i].ServiceType == typeof(T))
                    services.RemoveAt(i);
            services.AddSingleton(instance);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
            await _baseFactory.DisposeAsync();
        }
    }

    private sealed class StubOrgAuth : IGitHubOrgAuthorizationService
    {
        public OrgAuthResult Result { get; set; } = OrgAuthResult.Allowed;
        public bool IsConfigured => true;
        public Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    private sealed class StubTokenProvider : IGitHubAccessTokenProvider
    {
        public string? Token { get; set; }
        public Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            Task.FromResult(Token);
    }
}
