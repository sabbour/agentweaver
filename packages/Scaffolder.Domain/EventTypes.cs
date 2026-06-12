namespace Scaffolder.Domain;

/// <summary>Canonical event type strings used in the run event stream (FR-018).</summary>
public static class EventTypes
{
    public const string RunStarted   = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunFailed    = "run.failed";
    public const string RunBounded   = "run.bounded";
    /// <summary>
    /// Non-terminal error event emitted when an operation fails but the run is
    /// reverted to a retryable state (e.g., AwaitingReview after merge InternalError).
    /// Does NOT mark the stream as terminal.
    /// </summary>
    public const string RunError = "run.error";

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
    /// <summary>
    /// Emitted when the agent calls report_outcome at the end of a run.
    /// Payload: { achieved: bool, reason: string }
    /// </summary>
    public const string RunOutcome        = "run.outcome";
    public const string ToolCall          = "tool.call";
    public const string ToolResult        = "tool.result";
    public const string ToolError         = "tool.error";
    /// <summary>
    /// Emitted when the sandbox intercepts a tool call (e.g. web_fetch) that requires
    /// operator approval before proceeding. The run stream pauses at the permission gate
    /// until the operator grants or denies via the tool-approvals/tool-denials endpoints.
    /// Payload: { requestId, toolName, url?, intention?, message }
    /// </summary>
    public const string ToolApprovalRequired = "tool.approval_required";

    public const string ReviewChangesRequested = "review.changes_requested";
    public const string RevisionStarted        = "revision.started";
    public const string RunCancelled           = "run.cancelled";
}
