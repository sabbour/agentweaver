using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// The latest coordinator-heartbeat tick outcome for a single API pod, persisted so the
/// diagnostics endpoint can report a consistent, cross-replica view (FR-017).
///
/// At <c>replicas:2</c> the heartbeat background service writes on each pod while the diagnostics
/// HTTP read may land on a different pod; a purely per-pod in-memory store would then show
/// <c>waiting_first_tick</c>/stale on the reader pod even though another pod is ticking healthily.
/// One row per pod, UPSERTED each tick (bounded to the number of pods, so write load stays low).
/// </summary>
public sealed class HeartbeatStatusRecord
{
    /// <summary>The pod identity (Kubernetes pod name, falling back to the machine/host name). Primary key.</summary>
    [Key] public required string PodName { get; set; }

    /// <summary>UTC timestamp of this pod's last completed tick.</summary>
    public DateTimeOffset LastTickUtc { get; set; }

    /// <summary>Backlog tasks acted on during the last tick.</summary>
    public int ActedCount { get; set; }

    /// <summary>Errors caught during the last tick.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Duration of the last tick in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Last error message seen by this pod, or null when error-free.</summary>
    public string? Error { get; set; }

    /// <summary>Whether the heartbeat is enabled on this pod (Coordinator:HeartbeatEnabled).</summary>
    public bool Enabled { get; set; }

    /// <summary>The configured tick interval in seconds on this pod.</summary>
    public int IntervalSeconds { get; set; }
}
