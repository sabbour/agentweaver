namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// No-op run lease store for SQLite / single-replica deployments.
/// All claims succeed immediately — there is no contention because only one replica exists.
/// </summary>
public sealed class NoOpRunLeaseStore : IRunLeaseStore
{
    public Task<(bool Claimed, long FencingToken)> TryClaimAsync(
        string runId, string ownerId, TimeSpan leaseTtl, CancellationToken ct = default)
        => Task.FromResult((true, 1L));

    public Task<bool> TryRenewAsync(
        string runId, string ownerId, long fencingToken, TimeSpan leaseTtl, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task ReleaseAsync(string runId, string ownerId, long fencingToken, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> IsLeaseOwnerAsync(string runId, string ownerId, long fencingToken, CancellationToken ct = default)
        => Task.FromResult(true);
}
