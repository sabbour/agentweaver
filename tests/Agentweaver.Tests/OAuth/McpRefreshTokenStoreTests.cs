using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// Tests for <see cref="McpRefreshTokenStore"/> rotation, reuse detection, atomic single-use
/// consumption, and the non-consuming <see cref="McpRefreshTokenStore.PeekAsync"/> used by the
/// refresh endpoint's fail-closed org re-check.
///
/// Security focus (Seraph findings-api-data Alert 5 / findings-auth Alert 7): refresh-token
/// consumption must be ATOMIC so a concurrent replay of the same token cannot establish two
/// independent live refresh branches. Each simulated concurrent request gets its OWN
/// <see cref="MemoryDbContext"/> over a shared in-memory SQLite database, mirroring distinct
/// scoped requests / API replicas.
/// </summary>
public sealed class McpRefreshTokenStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public McpRefreshTokenStoreTests()
    {
        _connectionString = $"DataSource=file:mcprefresh-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        using var db = NewDbContext();
        db.Database.EnsureCreated();
    }

    private MemoryDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(_connectionString).Options;
        return new MemoryDbContext(options);
    }

    private McpRefreshTokenStore NewStore() => new(NewDbContext());

    private static McpRefreshGrant SampleGrant() =>
        new("octocat", "octocat", "client-1", "mcp:invoke", "microsoft");

    // =========================================================================
    // Happy path: issue → rotate → new token carries the grant; old is consumed.
    // =========================================================================
    [Fact]
    public async Task RotateAsync_HappyPath_IssuesSuccessor_WithSameGrant()
    {
        var token = await NewStore().IssueAsync(SampleGrant());

        var rotation = await NewStore().RotateAsync(token, "client-1");

        rotation.Error.Should().BeNull();
        rotation.NewRefreshToken.Should().NotBeNullOrEmpty().And.NotBe(token);
        rotation.Grant!.GithubLogin.Should().Be("octocat");
        rotation.Grant.Org.Should().Be("microsoft");
    }

    // =========================================================================
    // Reuse detection: rotating the SAME token twice (sequentially) fails the
    // second time AND revokes the whole chain (the just-issued successor too).
    // =========================================================================
    [Fact]
    public async Task RotateAsync_ReusedToken_IsRejected_AndRevokesChain()
    {
        var token = await NewStore().IssueAsync(SampleGrant());

        var first = await NewStore().RotateAsync(token, "client-1");
        first.Error.Should().BeNull();

        // Replay the already-consumed original token.
        var replay = await NewStore().RotateAsync(token, "client-1");
        replay.Error.Should().Be("invalid_grant");

        // The successor minted by the first rotation must also be dead now (chain revoked).
        var successor = await NewStore().RotateAsync(first.NewRefreshToken, "client-1");
        successor.Error.Should().Be("invalid_grant");
    }

    // =========================================================================
    // ATOMIC single-use: many concurrent rotations of the SAME token must not
    // both succeed. At most one clean success is allowed; a non-atomic
    // read-modify-write (the pre-fix behavior) would let several succeed and
    // fork independent live branches.
    // =========================================================================
    [Fact]
    public async Task RotateAsync_ConcurrentReplayOfSameToken_YieldsAtMostOneSuccess()
    {
        var token = await NewStore().IssueAsync(SampleGrant());

        // Fire several rotations of the same token concurrently, each on its own DbContext.
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                return await NewStore().RotateAsync(token, "client-1");
            }
            catch (Exception ex) when (ex is DbUpdateException or SqliteException)
            {
                // A transient SQLite write-lock contention counts as a non-success, not a fork.
                return new RefreshRotationResult(null, null, "invalid_grant", ex.Message);
            }
        });

        var results = await Task.WhenAll(tasks);

        var successes = results.Count(r => r.Error is null && r.NewRefreshToken is not null);
        successes.Should().BeLessThanOrEqualTo(1,
            "atomic single-use consumption must never let a token be rotated to two live successors");
    }

    // =========================================================================
    // PeekAsync does NOT consume: the token remains rotatable afterwards.
    // =========================================================================
    [Fact]
    public async Task PeekAsync_DoesNotConsumeToken()
    {
        var token = await NewStore().IssueAsync(SampleGrant());

        var peek = await NewStore().PeekAsync(token, "client-1");
        peek.Error.Should().BeNull();
        peek.Grant!.GithubLogin.Should().Be("octocat");

        // Still usable — peek must be side-effect free w.r.t. consumption.
        var rotation = await NewStore().RotateAsync(token, "client-1");
        rotation.Error.Should().BeNull();
        rotation.NewRefreshToken.Should().NotBeNullOrEmpty();
    }

    // =========================================================================
    // PeekAsync enforces client_id binding and reuse detection.
    // =========================================================================
    [Fact]
    public async Task PeekAsync_WrongClientId_IsRejected()
    {
        var token = await NewStore().IssueAsync(SampleGrant());

        var peek = await NewStore().PeekAsync(token, "someone-else");

        peek.Error.Should().Be("invalid_grant");
        peek.Grant.Should().BeNull();
    }

    [Fact]
    public async Task PeekAsync_ConsumedToken_RevokesChain()
    {
        var token = await NewStore().IssueAsync(SampleGrant());
        var first = await NewStore().RotateAsync(token, "client-1");
        first.Error.Should().BeNull();

        // Peek the already-consumed original → reuse detection revokes the chain.
        var peek = await NewStore().PeekAsync(token, "client-1");
        peek.Error.Should().Be("invalid_grant");

        // Successor is now revoked as part of the chain.
        var successor = await NewStore().RotateAsync(first.NewRefreshToken, "client-1");
        successor.Error.Should().Be("invalid_grant");
    }

    public void Dispose() => _keepAlive.Dispose();
}
