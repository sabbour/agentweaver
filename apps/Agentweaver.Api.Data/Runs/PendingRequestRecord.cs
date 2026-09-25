using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Runs;

/// <summary>
/// A pending human-in-the-loop (HITL) request gate for a run, persisted so it survives
/// load-balancing across API replicas.
///
/// The background workflow watch loop arms this gate when the MAF runtime suspends at the
/// request port (<c>RequestInfoEvent</c>); a LATER HTTP review/confirm request resolves it.
/// At <c>replicas:2</c> the consuming request may land on a DIFFERENT pod than the one that
/// armed the gate, so the gate must live in <c>MemoryDbContext</c> (Postgres in prod, SQLite in
/// dev) rather than per-pod memory. Delivery is fenced by a state machine on this same row: the
/// exact request id and decision identity are retained until the workflow response is acknowledged.
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

    /// <summary>The MAF request id from <see cref="RequestJson"/>; fences stale decisions for old gates.</summary>
    public string? RequestId { get; set; }

    /// <summary>The submitting user that owns this run (IDOR defense-in-depth on consume).</summary>
    public required string OwnerUser { get; set; }

    /// <summary>Durable delivery state: waiting, ready, delivering, or delivered.</summary>
    public string DeliveryState { get; set; } = PendingRequestDeliveryStates.Waiting;

    /// <summary>Stable kind discriminator for the serialized response payload.</summary>
    public string? DeliveryKind { get; set; }

    /// <summary>Stable identity of the decision/result being delivered to this exact request id.</summary>
    public string? DecisionIdentity { get; set; }

    /// <summary>Serialized response payload that will be passed to <c>ExternalRequest.CreateResponse</c>.</summary>
    public string? ResponseJson { get; set; }

    /// <summary>Current delivery owner while <see cref="DeliveryState"/> is delivering.</summary>
    public string? DeliveryClaimOwner { get; set; }

    /// <summary>When the current delivery claim was acquired.</summary>
    public DateTimeOffset? DeliveryClaimedAt { get; set; }

    /// <summary>When the workflow response was acknowledged.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>When this gate was armed.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optional expiry for opportunistic garbage collection; null = no expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

public static class PendingRequestDeliveryStates
{
    public const string Waiting = "waiting";
    public const string Ready = "ready";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
}

public static class PendingRequestDeliveryKinds
{
    public const string WorkflowReview = "workflow_review";
    public const string CoordinatorOutcomeSpec = "coordinator_outcome_spec";
}
