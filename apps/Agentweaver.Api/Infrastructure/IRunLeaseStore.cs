namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Durable, multi-replica-safe run lease store.
/// A worker that successfully claims a run is the ONLY process that may drive it
/// until the lease expires or is released. Expired leases (crash recovery) are
/// reclaimable by any worker.
/// </summary>
public interface IRunLeaseStore
{
    /// <summary>
    /// Attempts to atomically claim ownership of <paramref name="runId"/> for
    /// <paramref name="ownerId"/>. Returns <c>true</c> and the assigned
    /// <paramref name="fencingToken"/> if the claim succeeds (no current owner or
    /// current lease is expired). Returns <c>false</c> if another worker already
    /// holds a valid lease.
    /// </summary>
    Task<(bool Claimed, long FencingToken)> TryClaimAsync(
        string runId, string ownerId, TimeSpan leaseTtl, CancellationToken ct = default);

    /// <summary>
    /// Renews the lease for a run that this worker currently owns.
    /// Returns <c>false</c> if the fencing token does not match (lease was stolen).
    /// </summary>
    Task<bool> TryRenewAsync(
        string runId, string ownerId, long fencingToken, TimeSpan leaseTtl, CancellationToken ct = default);

    /// <summary>
    /// Releases the lease. Idempotent — a release on an already-expired/stolen lease succeeds.
    /// </summary>
    Task ReleaseAsync(string runId, string ownerId, long fencingToken, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if this worker currently owns a valid, unexpired lease for the run.
    /// </summary>
    Task<bool> IsLeaseOwnerAsync(string runId, string ownerId, long fencingToken, CancellationToken ct = default);
}
