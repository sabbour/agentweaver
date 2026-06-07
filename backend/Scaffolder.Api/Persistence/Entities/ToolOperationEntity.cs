namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// Audit record for a sandboxed file tool invocation (read_file or write_file).
/// Correlates to the ToolCall event via CallId.
/// </summary>
public sealed class ToolOperationEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>
    /// Matches CallId on the ToolCall event and its corresponding result event.
    /// </summary>
    public Guid CallId { get; set; }

    public ToolName ToolName { get; set; }

    /// <summary>Path as received from the agent (before sandbox resolution).</summary>
    public required string RequestedPath { get; set; }

    /// <summary>Canonical absolute path after sandbox resolution. Null when rejected.</summary>
    public string? ResolvedPath { get; set; }

    public Persistence.ToolResult Result { get; set; }
    public ToolErrorCode? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public RunEntity? Run { get; set; }
}
