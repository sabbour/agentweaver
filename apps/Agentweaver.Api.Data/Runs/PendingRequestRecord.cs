using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Runs;

/// <summary>
/// A pending human-in-the-loop (HITL) request gate for a run, persisted so it survives
/// load-balancing across API replicas.
///
/// The background workflow watch loop arms this gate when the MAF runtime suspends at the
/// request port (<c>RequestInfoEvent</c>); a LATER HTTP review/confirm request consumes it.
/// At <c>replicas:2</c> the consuming request may land on a DIFFERENT pod than the one that
/// armed the gate, so the gate must live in <c>MemoryDbContext</c> (Postgres in prod, SQLite in
/// dev) rather than per-pod memory. Single-consume is enforced atomically across replicas by a
/// conditional <c>ExecuteDeleteAsync</c> on <see cref="RunId"/> (read-then-conditional-delete):
/// the caller whose delete affected the row wins; a zero-rows result means the gate was already
/// consumed (replay / double-POST protection — at-most-once delivery).
/// </summary>
public sealed class PendingRequestRecord
{
    [Key] public int Id { get; set; }

    /// <summary>The run this gate belongs to. Unique — at most one pending gate per run.</summary>
    public required string RunId { get; set; }

    /// <summary>
    /// The serialized MAF <c>ExternalRequest</c> (port info + request id) needed to build the
    /// response and resume the suspended workflow on the pod that owns the live streaming run.
    /// </summary>
    public required string RequestJson { get; set; }

    /// <summary>The submitting user that owns this run (IDOR defense-in-depth on consume).</summary>
    public required string OwnerUser { get; set; }

    /// <summary>When this gate was armed.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional expiry for opportunistic garbage collection; null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
