namespace Scaffolder.Domain;

/// <summary>Canonical event type strings used in the run event stream (FR-018).</summary>
public static class EventTypes
{
    public const string RunStarted   = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunFailed    = "run.failed";
    public const string RunBounded   = "run.bounded";

    public const string ReviewRequested = "review.requested";
    public const string ReviewApproved  = "review.approved";
    public const string ReviewDeclined  = "review.declined";

    public const string MergeStarted    = "merge.started";
    public const string MergeCompleted  = "merge.completed";
    public const string MergeFailed     = "merge.failed";
    /// <summary>
    /// Emitted when a merge attempt encounters conflicts that require human resolution.
    /// Payload: { conflicting_files: string[] }
    /// </summary>
    public const string MergeConflicted = "merge.conflicted";

    public const string AgentMessage      = "agent.message";
    public const string AgentMessageDelta = "agent.message.delta";
    public const string AgentIntent       = "agent.intent";
    public const string ToolCall          = "tool.call";
    public const string ToolResult        = "tool.result";
    public const string ToolError         = "tool.error";

    public const string ReviewChangesRequested = "review.changes_requested";
    public const string RevisionStarted        = "revision.started";
}
