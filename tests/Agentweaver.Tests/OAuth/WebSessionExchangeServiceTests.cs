using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// F5 — Web sign-in one-time code exchange.
///
/// Validates that the GitHub access token is exchanged via a server-side single-use code instead
/// of being leaked in the redirect URL: happy-path redemption returns the token + login, codes are
/// single-use (replay fails), invalid/missing codes are rejected, and — critically — a code issued
/// on one replica is redeemable on a DIFFERENT replica (cross-instance visibility via the shared DB).
/// </summary>
public class WebSessionExchangeServiceTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public WebSessionExchangeServiceTests()
    {
        _connectionString = $"DataSource=file:webexchange-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        using var db = new MemoryDbContext(options);
        db.Database.EnsureCreated();
    }

    // Each call simulates a distinct replica: its own IServiceScopeFactory / DbContext over the shared DB.
    private WebSessionExchangeService NewService() =>
        new(MemoryDbScopeFactory.ForSqlite(_connectionString),
            NullLogger<WebSessionExchangeService>.Instance);

    [Fact]
    public async Task Issue_ThenRedeem_ReturnsTokenAndLogin()
    {
        var svc = NewService();
        var code = await svc.IssueAsync("gho_secret_token", "octocat");

        var (ok, token, login) = await svc.TryRedeemAsync(code);

        ok.Should().BeTrue();
        token.Should().Be("gho_secret_token");
        login.Should().Be("octocat");
    }

    [Fact]
    public async Task Redeem_IsSingleUse()
    {
        var svc = NewService();
        var code = await svc.IssueAsync("gho_secret_token", "octocat");

        (await svc.TryRedeemAsync(code)).Success.Should().BeTrue();
        (await svc.TryRedeemAsync(code)).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-code")]
    public async Task Redeem_InvalidOrMissingCode_ReturnsFalse(string? code)
    {
        var svc = NewService();
        await svc.IssueAsync("gho_secret_token", "octocat");

        var (ok, token, login) = await svc.TryRedeemAsync(code);
        ok.Should().BeFalse();
        token.Should().BeEmpty();
        login.Should().BeEmpty();
    }

    [Fact]
    public async Task Issue_GeneratesUniqueOpaqueCodes()
    {
        var svc = NewService();
        var a = await svc.IssueAsync("t1", "u1");
        var b = await svc.IssueAsync("t2", "u2");

        a.Should().NotBe(b);
        a.Should().NotContain("t1");
    }

    /// <summary>
    /// Cross-replica test: a code issued on one service instance (replica A) must be redeemable
    /// on a SEPARATE service instance (replica B) that shares only the database. This is the exact
    /// bug that the in-memory ConcurrentDictionary caused: replica B had no record of the code
    /// issued by replica A, so the POST /api/auth/session/exchange returned 401 ~50% of the time.
    /// </summary>
    [Fact]
    public async Task Issue_OnReplicaA_IsRedeemable_OnReplicaB_ThenSingleUse()
    {
        // Replica A issues the code (persisted to the shared DB).
        var replicaA = NewService();
        var code = await replicaA.IssueAsync("gho_cross_replica", "octocat");

        // Replica B (a SEPARATE scope factory / DbContext) redeems it.
        var replicaB = NewService();
        var (ok, token, login) = await replicaB.TryRedeemAsync(code);

        ok.Should().BeTrue("the code issued on replica A must be visible to replica B via the shared DB");
        token.Should().Be("gho_cross_replica");
        login.Should().Be("octocat");

        // Replica C tries to redeem the same code → single-use violation.
        var replicaC = NewService();
        (await replicaC.TryRedeemAsync(code)).Success.Should().BeFalse("codes are single-use across all replicas");

        // The row is gone from the shared store after the single successful redemption.
        var dbOptions = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        await using var verifyDb = new MemoryDbContext(dbOptions);
        (await verifyDb.WebSessionExchangeCodes.AnyAsync(c => c.Code == code))
            .Should().BeFalse("the row must be removed after single-use redemption");
    }

    /// <summary>
    /// Expired codes must be rejected even if still present in the database (e.g. purge hasn't run).
    /// </summary>
    [Fact]
    public async Task ExpiredCode_IsRejected()
    {
        // Seed an already-expired code directly.
        var expiredCode = WebSessionExchangeService.GenerateOpaqueCode();
        var dbOptions = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        await using (var seedDb = new MemoryDbContext(dbOptions))
        {
            seedDb.WebSessionExchangeCodes.Add(new WebSessionExchangeCode
            {
                Code = expiredCode,
                AccessToken = "gho_expired",
                Login = "octocat",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            });
            await seedDb.SaveChangesAsync();
        }

        var svc = NewService();
        var (ok, _, _) = await svc.TryRedeemAsync(expiredCode);
        ok.Should().BeFalse("expired codes must be rejected");
    }

    public void Dispose() => _keepAlive.Dispose();
}

