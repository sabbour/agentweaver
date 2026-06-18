using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for GitHubTokenRefreshService (IGitHubAccessTokenProvider) — transparent GitHub user
/// access-token refresh. Uses an offline counting HTTP handler (same pattern as
/// GitHubDeviceFlowTests.FakeHttpMessageHandler) so no real GitHub calls are made.
/// </summary>
public sealed class GitHubTokenRefreshServiceTests
{
    private const string RefreshSuccessJson =
        """{"access_token":"ghu_new_access","expires_in":28800,"refresh_token":"ghr_new_refresh","refresh_token_expires_in":15897600}""";

    // =========================================================================
    // TR-01: Valid (not near expiry) token -> returned unchanged, NO HTTP call
    // =========================================================================
    [Fact]
    public async Task ValidToken_ReturnedUnchanged_NoHttpCall()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken(
            "ghu_current", "ghr_refresh", DateTimeOffset.UtcNow.AddHours(4), "user", null, ["repo"]));

        var (svc, handler) = BuildService(store);

        var token = await svc.GetValidAccessTokenAsync(scope);

        token.Should().Be("ghu_current");
        handler.CallCount.Should().Be(0, "a token comfortably in the future must not trigger a refresh");
    }

    // =========================================================================
    // TR-02: Expired token WITH refresh token -> refresh once, rotated token persisted
    // =========================================================================
    [Fact]
    public async Task ExpiredToken_WithRefreshToken_RefreshesAndPersistsRotatedToken()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken(
            "ghu_old", "ghr_old_refresh", DateTimeOffset.UtcNow.AddSeconds(-10), "user", null, ["repo"]));

        var (svc, handler) = BuildService(store, RefreshSuccessJson);

        var token = await svc.GetValidAccessTokenAsync(scope);

        token.Should().Be("ghu_new_access");
        handler.CallCount.Should().Be(1, "the refresh endpoint must be called exactly once");

        var persisted = await store.GetTokenAsync(scope);
        persisted.Should().NotBeNull();
        persisted!.AccessToken.Should().Be("ghu_new_access");
        persisted.RefreshToken.Should().Be("ghr_new_refresh", "the rotated refresh token must be persisted");
        persisted.ExpiresAt.Should().NotBeNull();
        persisted.ExpiresAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(1),
            "the new ExpiresAt must come from the access-token expires_in");
    }

    // =========================================================================
    // TR-03: Non-expiring token (ExpiresAt null, no refresh token) -> unchanged, no refresh
    // =========================================================================
    [Fact]
    public async Task NonExpiringToken_ReturnedUnchanged_NoRefresh()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken("ghp_classic", null, null, "user", null, ["repo"]));

        var (svc, handler) = BuildService(store);

        var token = await svc.GetValidAccessTokenAsync(scope);

        token.Should().Be("ghp_classic");
        handler.CallCount.Should().Be(0, "a non-expiring (classic) token must never be refreshed");
    }

    // =========================================================================
    // TR-04: Refresh failure (GitHub error) -> re-auth required (null), no garbage persisted
    // =========================================================================
    [Fact]
    public async Task RefreshFailure_SignsOutAndReturnsNull()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken(
            "ghu_old", "ghr_revoked", DateTimeOffset.UtcNow.AddSeconds(-10), "user", null, ["repo"]));

        // GitHub returns an error body for a revoked/expired refresh token.
        var (svc, _) = BuildService(store, """{"error":"bad_refresh_token"}""");

        var token = await svc.GetValidAccessTokenAsync(scope);

        token.Should().BeNull("a failed refresh must surface a re-authentication-required outcome");

        // No partial/garbage token persisted; scope is in a clean signed-out state.
        var entry = await store.GetAsync(scope);
        entry.Status.Should().Be(GitHubTokenStatus.SignedOut);
        (await store.GetTokenAsync(scope)).Should().BeNull();
    }

    // =========================================================================
    // TR-05: Expired token with no refresh token -> re-auth required (null)
    // =========================================================================
    [Fact]
    public async Task ExpiredToken_NoRefreshToken_SignsOutAndReturnsNull()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken(
            "ghu_old", null, DateTimeOffset.UtcNow.AddSeconds(-10), "user", null, ["repo"]));

        var (svc, handler) = BuildService(store);

        var token = await svc.GetValidAccessTokenAsync(scope);

        token.Should().BeNull();
        handler.CallCount.Should().Be(0, "with no refresh token there is nothing to call");
        (await store.GetAsync(scope)).Status.Should().Be(GitHubTokenStatus.SignedOut);
    }

    // =========================================================================
    // TR-06: Concurrent callers on an expired token -> refresh called exactly once
    // =========================================================================
    [Fact]
    public async Task ConcurrentCallers_RefreshExactlyOnce_SameFreshToken()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        await store.SetAsync(scope, new GitHubToken(
            "ghu_old", "ghr_old_refresh", DateTimeOffset.UtcNow.AddSeconds(-10), "user", null, ["repo"]));

        var (svc, handler) = BuildService(store, RefreshSuccessJson, delayMs: 100);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => svc.GetValidAccessTokenAsync(scope))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        handler.CallCount.Should().Be(1, "the per-scope semaphore must collapse concurrent refreshes into one");
        results.Should().OnlyContain(t => t == "ghu_new_access",
            "every concurrent caller must observe the same fresh access token");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (GitHubTokenRefreshService Service, CountingHttpMessageHandler Handler) BuildService(
        IGitHubTokenStore store, string? refreshResponseJson = null, int delayMs = 0)
    {
        var handler = new CountingHttpMessageHandler(refreshResponseJson, delayMs);
        var factory = new SingleClientHttpClientFactory(handler);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:GitHub:BaseUrl"]      = "https://github.com",
                ["Auth:GitHub:ClientId"]     = "test-client-id",
                ["Auth:GitHub:ClientSecret"] = "test-client-secret",
            })
            .Build();

        var service = new GitHubTokenRefreshService(
            config, store, factory, NullLogger<GitHubTokenRefreshService>.Instance);

        return (service, handler);
    }

    /// <summary>HTTP handler that counts calls and returns a fixed JSON body (or 500 if none).</summary>
    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _json;
        private readonly int _delayMs;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public CountingHttpMessageHandler(string? json, int delayMs)
        {
            _json = json;
            _delayMs = delayMs;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_delayMs > 0)
                await Task.Delay(_delayMs, cancellationToken);

            if (_json is null)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
