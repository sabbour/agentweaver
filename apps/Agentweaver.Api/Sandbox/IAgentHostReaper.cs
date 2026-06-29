namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Reaps orphaned AgentHost <c>SandboxClaim</c>s (<c>agent-*</c> claims whose run is no longer
/// active). Each AgentHost pod reserves 2 CPU against the namespace quota, so claims left behind by
/// crashed/stalled coordinator runs silently exhaust the quota and make every subsequent run fail
/// with <c>ReconcilerError: exceeded quota</c>.
///
/// <para>
/// Implemented as a regular singleton (NOT a <c>BackgroundService</c>): its execution cadence is
/// driven by the coordinator heartbeat (<c>CoordinatorHeartbeatService</c>), which invokes
/// <see cref="SweepOrphanedPodsAsync"/> every N ticks. Registered only when the Kubernetes sandbox
/// is active; outside Kubernetes the service is absent and callers resolve it null-safely.
/// </para>
/// </summary>
public interface IAgentHostReaper
{
    /// <summary>
    /// Single reconciliation pass: deletes every AgentHost claim whose run is not currently active
    /// (InProgress/Pending). Best-effort and idempotent. Returns the number of claims reaped.
    /// </summary>
    Task<int> SweepOrphanedPodsAsync(CancellationToken ct = default);

    /// <summary>
    /// Snapshot of every AgentHost (<c>agent-*</c>) claim in the namespace, each tagged with whether
    /// it is orphaned (its run is no longer active) so the diagnostics cluster view can show what the
    /// reaper would clean up. Does not mutate cluster state.
    /// </summary>
    Task<IReadOnlyList<AgentHostClaimInfo>> GetClaimInventoryAsync(CancellationToken ct = default);
}

/// <summary>
/// A single AgentHost <c>SandboxClaim</c> as seen by the reaper. <see cref="RunId"/> is only
/// recoverable for active claims (the claim name is a lossy derivation of the run id), so it is null
/// for orphaned claims.
/// </summary>
public sealed record AgentHostClaimInfo(
    string ClaimName,
    string? RunId,
    string? PodName,
    bool Ready,
    DateTimeOffset? CreatedAt,
    bool Orphaned);
