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
/// Replica-safety tests for the EF-backed <see cref="McpOAuthBrokerService"/>.
///
/// The broker persists pending authorizations and single-use authorization codes in
/// <see cref="MemoryDbContext"/> so the PKCE flow survives load-balancing across replicas. These
/// tests use a shared in-memory SQLite database and give EACH broker instance its OWN
/// <see cref="MemoryDbContext"/> (separate connection) to simulate distinct API replicas:
///   • a code issued on one "replica" can be redeemed on a DIFFERENT "replica" (the original bug:
///     the code was held in a single pod's memory and other pods returned invalid_grant);
///   • redemption is single-use and atomic — two concurrent redemptions of the same code resolve to
///     exactly one success even when each runs on its own DbContext;
///   • expiry, redirect_uri, client_id and PKCE checks still apply after the atomic consume.
/// </summary>
public sealed class McpOAuthBrokerStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public McpOAuthBrokerStoreTests()
    {
        // Shared-cache in-memory DB so each broker's scope opens its OWN connection (real, separate
        // replicas). The keep-alive connection keeps the shared in-memory database alive for the test.
        _connectionString = $"DataSource=file:mcpbroker-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var db = NewDbContext();
        db.Database.EnsureCreated();
    }

    private MemoryDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MemoryDbContext(options);
    }

    // Each broker simulates a distinct replica: its own DbContext over the shared database.
    private McpOAuthBrokerService NewBrokerOnSeparateReplica(MemoryDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var gitHub = new GitHubOAuthRedirectService(
            config, new NullGitHubTokenStore(), new NullHttpClientFactory(),
            MemoryDbScopeFactory.ForSqlite(_connectionString),
            NullLogger<GitHubOAuthRedirectService>.Instance);

        return new McpOAuthBrokerService(
            gitHub, new NotConfiguredOrgAuth(), new NullGitHubTokenStore(), db,
            NullLogger<McpOAuthBrokerService>.Instance);
    }

    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    // BASE64URL(SHA256(ASCII(Verifier))) — the RFC 7636 worked example.
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string RedirectUri = "http://127.0.0.1:5000/callback";
    private const string ClientId = "mcp_testclient";

    private async Task<string> SeedCodeAsync(DateTimeOffset? expiresAt = null)
    {
        var code = McpOAuthBrokerService.GenerateOpaqueToken();
        using var db = NewDbContext();
        db.McpAuthorizationCodes.Add(new McpAuthorizationCode
        {
            Code = code,
            Subject = "octocat",
            GithubLogin = "octocat",
            CodeChallenge = Challenge,
            RedirectUri = RedirectUri,
            ClientId = ClientId,
            Scope = "mcp:invoke",
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddSeconds(60),
        });
        await db.SaveChangesAsync();
        return code;
    }

    [Fact]
    public async Task CodeIssuedOnOneReplica_IsRedeemableOnAnotherReplica_ThenSingleUse()
    {
        // Replica A issues the code (persisted to the shared DB).
        var code = await SeedCodeAsync();

        // Replica B (a SEPARATE DbContext) redeems it — this is exactly what broke with in-memory state.
        using var dbB = NewDbContext();
        var (grant, error) = await NewBrokerOnSeparateReplica(dbB)
            .RedeemAuthorizationCode(code, Verifier, RedirectUri, ClientId);

        grant.Should().NotBeNull();
        error.Should().BeNull();
        grant!.GithubLogin.Should().Be("octocat");
        grant.Scope.Should().Be("mcp:invoke");

        // Replica C tries to redeem the same code again → single-use violation.
        using var dbC = NewDbContext();
        var (grant2, error2) = await NewBrokerOnSeparateReplica(dbC)
            .RedeemAuthorizationCode(code, Verifier, RedirectUri, ClientId);

        grant2.Should().BeNull();
        error2.Should().NotBeNull();
        error2!.Error.Should().Be("invalid_grant");
        error2.ErrorDescription.Should().Be("Authorization code is invalid or already used.");
    }

    [Fact]
    public async Task ConcurrentRedemptions_AcrossReplicas_ExactlyOneSucceeds()
    {
        var code = await SeedCodeAsync();

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            using var db = NewDbContext();
            return await NewBrokerOnSeparateReplica(db)
                .RedeemAuthorizationCode(code, Verifier, RedirectUri, ClientId);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Grant is not null).Should().Be(1, "an authorization code is single-use even across replicas");
        results.Count(r => r.Error is not null && r.Error.Error == "invalid_grant").Should().Be(15);
    }

    [Fact]
    public async Task ExpiredCode_IsConsumedButRejected()
    {
        var code = await SeedCodeAsync(expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        using var db = NewDbContext();
        var (grant, error) = await NewBrokerOnSeparateReplica(db)
            .RedeemAuthorizationCode(code, Verifier, RedirectUri, ClientId);

        grant.Should().BeNull();
        error!.Error.Should().Be("invalid_grant");
        error.ErrorDescription.Should().Be("Authorization code has expired.");

        // The expired row must still have been consumed (no lingering single-use code).
        using var verifyDb = NewDbContext();
        (await verifyDb.McpAuthorizationCodes.AnyAsync(c => c.Code == code)).Should().BeFalse();
    }

    [Fact]
    public async Task RedirectUriMismatch_Returns_InvalidGrant()
    {
        var code = await SeedCodeAsync();

        using var db = NewDbContext();
        var (grant, error) = await NewBrokerOnSeparateReplica(db)
            .RedeemAuthorizationCode(code, Verifier, "http://127.0.0.1:6000/callback", ClientId);

        grant.Should().BeNull();
        error!.Error.Should().Be("invalid_grant");
        error.ErrorDescription.Should().Contain("redirect_uri");
    }

    [Fact]
    public async Task WrongPkceVerifier_Returns_InvalidGrant()
    {
        var code = await SeedCodeAsync();

        using var db = NewDbContext();
        var (grant, error) = await NewBrokerOnSeparateReplica(db)
            .RedeemAuthorizationCode(code, "not-the-verifier", RedirectUri, ClientId);

        grant.Should().BeNull();
        error!.Error.Should().Be("invalid_grant");
        error.ErrorDescription.Should().Be("PKCE verification failed.");
    }

    [Fact]
    public async Task UnknownCode_Returns_InvalidGrant()
    {
        using var db = NewDbContext();
        var (grant, error) = await NewBrokerOnSeparateReplica(db)
            .RedeemAuthorizationCode("does-not-exist", Verifier, RedirectUri, ClientId);

        grant.Should().BeNull();
        error!.Error.Should().Be("invalid_grant");
        error.ErrorDescription.Should().Be("Authorization code is invalid or already used.");
    }

    [Fact]
    public async Task IsPendingState_SeesPersistedPendingRow_AcrossReplicas()
    {
        var state = McpOAuthBrokerService.GenerateOpaqueToken();
        using (var seedDb = NewDbContext())
        {
            seedDb.McpPendingAuthorizations.Add(new McpPendingAuthorization
            {
                State = state,
                ClientId = ClientId,
                RedirectUri = RedirectUri,
                CodeChallenge = Challenge,
                ClientState = "client-xyz",
                Scope = "mcp:invoke",
                Resource = null,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = NewDbContext();
        var broker = NewBrokerOnSeparateReplica(db);
        (await broker.IsPendingState(state)).Should().BeTrue();
        (await broker.IsPendingState("unknown-state")).Should().BeFalse();
    }

    public void Dispose() => _keepAlive.Dispose();

    private sealed class NotConfiguredOrgAuth : IGitHubOrgAuthorizationService
    {
        public bool IsConfigured => false;
        public Task<OrgAuthResult> CheckMembershipAsync(string accessToken, string login, CancellationToken ct) =>
            Task.FromResult(OrgAuthResult.NotConfigured);
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
