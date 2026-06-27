using Agentweaver.Api.Infrastructure;

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
}
