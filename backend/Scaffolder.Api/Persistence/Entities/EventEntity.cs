namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// A single entry in the durable, append-only, per-run event log.
/// Events are NEVER updated or deleted. Ordering is by Sequence only.
/// Tool events carry a CallId correlating them to their originating ToolCall event.
/// </summary>
public sealed class EventEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>
    /// Monotonic sequence number within this run. Resumable SSE cursor.
    /// (RunId, Sequence) is unique. Used for ordering - NOT Timestamp.
    /// </summary>
    public long Sequence { get; set; }

    public EventType Type { get; set; }

    /// <summary>
    /// Informational timestamp only. MUST NOT be used for event ordering.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Required on all tool events (ToolCall, ToolResult, ToolRejected, ToolError).
    /// Null on lifecycle and AgentMessage events.
    /// </summary>
    public Guid? CallId { get; set; }

    /// <summary>
    /// JSON payload. MUST NOT contain secrets, credentials, or personal data (SC-009).
    /// </summary>
    public required string Payload { get; set; }

    public RunEntity Run { get; set; } = null!;
}
