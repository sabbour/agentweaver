using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// Replica-safety tests for the EF-backed CSRF <c>state</c> store in
/// <see cref="GitHubOAuthRedirectService"/> (the web sign-in / browser GitHub OAuth flow).
///
/// The service persists each authorization <c>state</c> in <see cref="MemoryDbContext"/> so the
/// browser callback can validate it on ANY replica — the original P0 bug was that the state lived
/// in a single pod's in-memory dictionary, so a callback load-balanced to a different pod returned
/// "Invalid or expired OAuth state" ~50% of the time at replicas:2.
///
/// Each test uses a shared in-memory SQLite database and gives EACH service instance its OWN
/// <see cref="IServiceScopeFactory"/> (separate connection) to simulate distinct API replicas:
///   • a state armed on one "replica" can be redeemed on a DIFFERENT "replica" (the original bug);
///   • redemption is single-use and atomic — redeeming the same state twice fails the second time;
///   • an expired state is rejected.
/// </summary>
public sealed class GitHubOAuthStateStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public GitHubOAuthStateStoreTests()
    {
        // Shared-cache in-memory DB so each service's scope opens its OWN connection (real, separate
        // replicas). The keep-alive connection keeps the shared in-memory database alive for the test.
        _connectionString = $"DataSource=file:oauthstate-store-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        using var db = new MemoryDbContext(options);
        db.Database.EnsureCreated();
    }

    private MemoryDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        return new MemoryDbContext(options);
    }

    // Each service simulates a distinct replica: its own scope factory (own DbContext) over the shared DB.
    private GitHubOAuthRedirectService NewServiceOnSeparateReplica(string? tokenJson = null, string? userJson = null)
    {
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

        IHttpClientFactory httpFactory = tokenJson is null
            ? new NullHttpClientFactory()
            : new SingleClientHttpClientFactory(new RoutingHttpMessageHandler(tokenJson, userJson ?? "{}"));

        return new GitHubOAuthRedirectService(
            config, new NullGitHubTokenStore(), httpFactory,
            MemoryDbScopeFactory.ForSqlite(_connectionString),
            NullLogger<GitHubOAuthRedirectService>.Instance);
    }

    private static string ExtractState(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?');
        var pair = query.Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    [Fact]
    public async Task StateArmedOnOneReplica_IsRedeemableOnAnotherReplica_ThenSingleUse()
    {
        // Replica A arms the state (persisted to the shared DB).
        var replicaA = NewServiceOnSeparateReplica();
        var state = ExtractState(await replicaA.BeginAuthorizationAsync());

        // Replica B (a SEPARATE scope factory / DbContext) redeems it — this is exactly what broke
        // with the in-memory dictionary: a different pod had no record of the state.
        var replicaB = NewServiceOnSeparateReplica(
            tokenJson: """{"access_token":"ghu_access","expires_in":28800}""",
            userJson: """{"login":"octocat","avatar_url":"https://avatars/x"}""");
        var (login, accessToken) = await replicaB.ExchangeCodeAsync("the-code", state);

        login.Should().Be("octocat");
        accessToken.Should().Be("ghu_access");

        // Replica C tries to redeem the same state again → single-use violation (at-most-once).
        var replicaC = NewServiceOnSeparateReplica(
            tokenJson: """{"access_token":"ghu_access"}""",
            userJson: """{"login":"octocat"}""");
        var act = async () => await replicaC.ExchangeCodeAsync("the-code", state);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid or expired OAuth state.");

        // The row is gone from the shared store after the single successful redemption.
        using var verifyDb = NewDbContext();
        (await verifyDb.OAuthStates.AnyAsync(s => s.State == state)).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownState_IsRejected()
    {
        var replica = NewServiceOnSeparateReplica();
        var act = async () => await replica.ExchangeCodeAsync("the-code", "never-armed-state");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid or expired OAuth state.");
    }

    [Fact]
    public async Task ExpiredState_IsRejected_AndConsumed()
    {
        // Seed an already-expired state directly (BeginAuthorization always arms a 10-min TTL).
        var state = "expired-state-token";
        using (var seedDb = NewDbContext())
        {
            seedDb.OAuthStates.Add(new OAuthState
            {
                State = state,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            });
            await seedDb.SaveChangesAsync();
        }

        var replica = NewServiceOnSeparateReplica();
        var act = async () => await replica.ExchangeCodeAsync("the-code", state);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid or expired OAuth state.");

        // The expired row is purged (best-effort cleanup) and never redeemable.
        using var verifyDb = NewDbContext();
        (await verifyDb.OAuthStates.AnyAsync(s => s.State == state)).Should().BeFalse();
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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
