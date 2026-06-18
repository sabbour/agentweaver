using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Regression tests for GitHubOAuthRedirectService.ExchangeCodeAsync.
/// Specifically verifies the ExpiresAt bug fix: ExpiresAt must come from the access token's
/// <c>expires_in</c> (TTL ~8h), NOT from <c>refresh_token_expires_in</c> (~6 months).
/// Uses an offline URL-routing HTTP handler so no real GitHub calls are made.
/// </summary>
public sealed class GitHubOAuthRedirectServiceTests
{
    // =========================================================================
    // OAR-01: ExchangeCodeAsync sets ExpiresAt from access-token expires_in
    // =========================================================================
    [Fact]
    public async Task ExchangeCodeAsync_SetsExpiresAt_FromAccessTokenExpiresIn()
    {
        var store = new InMemoryGitHubTokenStore();
        var (svc, _) = BuildService(store,
            // expires_in = 28800s (8h) for the access token; refresh_token_expires_in = ~6 months.
            tokenJson: """{"access_token":"ghu_access","refresh_token":"ghr_refresh","expires_in":28800,"refresh_token_expires_in":15897600}""",
            userJson: """{"login":"octocat","avatar_url":"https://avatars/x"}""");

        var state = ExtractState(svc.BeginAuthorization());

        var before = DateTimeOffset.UtcNow;
        var login = await svc.ExchangeCodeAsync("the-code", state);
        var after = DateTimeOffset.UtcNow;

        login.Should().Be("octocat");

        var token = await store.GetTokenAsync(GitHubTokenScope.Installation);
        token.Should().NotBeNull();
        token!.AccessToken.Should().Be("ghu_access");
        token.RefreshToken.Should().Be("ghr_refresh");
        token.ExpiresAt.Should().NotBeNull();

        // ExpiresAt must be ~8h out (from expires_in), NOT ~6 months (refresh_token_expires_in).
        token.ExpiresAt!.Value.Should().BeOnOrAfter(before.AddSeconds(28800));
        token.ExpiresAt!.Value.Should().BeOnOrBefore(after.AddSeconds(28800));
        token.ExpiresAt!.Value.Should().BeBefore(before.AddDays(1),
            "ExpiresAt must reflect the access-token TTL, not the refresh-token TTL");
    }

    // =========================================================================
    // OAR-02: ExpiresAt is null when GitHub omits expires_in (classic OAuth app)
    // =========================================================================
    [Fact]
    public async Task ExchangeCodeAsync_NoExpiresIn_LeavesExpiresAtNull()
    {
        var store = new InMemoryGitHubTokenStore();
        var (svc, _) = BuildService(store,
            tokenJson: """{"access_token":"ghp_classic"}""",
            userJson: """{"login":"octocat"}""");

        var state = ExtractState(svc.BeginAuthorization());
        await svc.ExchangeCodeAsync("the-code", state);

        var token = await store.GetTokenAsync(GitHubTokenScope.Installation);
        token.Should().NotBeNull();
        token!.AccessToken.Should().Be("ghp_classic");
        token.ExpiresAt.Should().BeNull("a classic OAuth app token has no expiry");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string ExtractState(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?');
        var pair = query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    private static (GitHubOAuthRedirectService Service, RoutingHttpMessageHandler Handler) BuildService(
        IGitHubTokenStore store, string tokenJson, string userJson)
    {
        var handler = new RoutingHttpMessageHandler(tokenJson, userJson);
        var factory = new SingleClientHttpClientFactory(handler);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:GitHub:BaseUrl"]      = "https://github.com",
                ["Auth:GitHub:ClientId"]     = "test-client-id",
                ["Auth:GitHub:ClientSecret"] = "test-client-secret",
                ["Auth:GitHub:CallbackUrl"]  = "https://app/callback",
                ["Auth:GitHub:Scopes"]       = "repo read:user",
            })
            .Build();

        var service = new GitHubOAuthRedirectService(
            config, store, factory, NullLogger<GitHubOAuthRedirectService>.Instance);

        return (service, handler);
    }

    /// <summary>Routes the access-token POST and the /user GET to distinct JSON bodies.</summary>
    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _tokenJson;
        private readonly string _userJson;

        public RoutingHttpMessageHandler(string tokenJson, string userJson)
        {
            _tokenJson = tokenJson;
            _userJson = userJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsoluteUri.Contains("/login/oauth/access_token")
                ? _tokenJson
                : _userJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
