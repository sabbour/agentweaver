using Agentweaver.Api.Infrastructure;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Infrastructure;

public class RunLeaseStoreTests
{
    [Fact]
    public async Task NoOpStore_AlwaysClaims()
    {
        var store = new NoOpRunLeaseStore();
        var (claimed, token) = await store.TryClaimAsync("run-1", "worker-1", TimeSpan.FromMinutes(1));
        Assert.True(claimed);
        Assert.True(token > 0);
    }

    [Fact]
    public async Task NoOpStore_AlwaysRenews()
    {
        var store = new NoOpRunLeaseStore();
        var (_, token) = await store.TryClaimAsync("run-1", "worker-1", TimeSpan.FromMinutes(1));
        var renewed = await store.TryRenewAsync("run-1", "worker-1", token, TimeSpan.FromMinutes(1));
        Assert.True(renewed);
    }

    [Fact]
    public async Task NoOpStore_AlwaysOwner()
    {
        var store = new NoOpRunLeaseStore();
        var (_, token) = await store.TryClaimAsync("run-1", "worker-1", TimeSpan.FromMinutes(1));
        Assert.True(await store.IsLeaseOwnerAsync("run-1", "worker-1", token));
    }

    [Fact]
    public async Task NoOpStore_ReleaseSucceeds()
    {
        var store = new NoOpRunLeaseStore();
        var (_, token) = await store.TryClaimAsync("run-1", "worker-1", TimeSpan.FromMinutes(1));
        await store.ReleaseAsync("run-1", "worker-1", token);
    }

    [Fact]
    public async Task SqliteStore_ReturnsOnlyTheCurrentUnexpiredClaim()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(testDb.Db);
        var run = new Agentweaver.Domain.Run
        {
            Id = Agentweaver.Domain.RunId.New(),
            RepositoryPath = ".",
            OriginatingBranch = "dev",
            ModelSource = Agentweaver.Domain.ModelSource.GitHubCopilot,
            Task = "lease boundary",
            SubmittingUser = "user-1",
            Status = Agentweaver.Domain.RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            LifecycleGeneration = 3,
        };
        await runStore.InsertAsync(run);
        var store = new SqliteRunLeaseStore(testDb.Db);
        var (_, token) = await store.TryClaimAsync(
            run.Id.ToString(), "worker-1", TimeSpan.FromMinutes(1));

        var claim = await store.GetActiveClaimAsync(run.Id.ToString());

        claim.Should().NotBeNull();
        claim!.OwnerId.Should().Be("worker-1");
        claim.FencingToken.Should().Be(token);
        await store.ReleaseAsync(run.Id.ToString(), "worker-1", token);
        (await store.GetActiveClaimAsync(run.Id.ToString())).Should().BeNull();
    }
}
