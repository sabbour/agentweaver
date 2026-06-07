namespace Scaffolder.Domain;

/// <summary>
/// Complete event taxonomy from FR-011. Every event written to the run log
/// uses one of these type strings.
/// </summary>
public static class EventType
{
    // Lifecycle
    public const string RunStarted = "run.started";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunBounded = "run.bounded";

    // Content
    public const string AgentMessage = "agent.message";
    public const string ToolCall = "tool.call";
    public const string ToolResult = "tool.result";
    public const string ToolRejected = "tool.rejected";
    public const string ToolError = "tool.error";

    // Review / Merge
    public const string ReviewRequested = "review.requested";
    public const string ReviewApproved = "review.approved";
    public const string ReviewDeclined = "review.declined";
    public const string MergeCompleted = "merge.completed";
    public const string MergeFailed = "merge.failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        RunStarted, RunCompleted, RunFailed, RunBounded,
        AgentMessage, ToolCall, ToolResult, ToolRejected, ToolError,
        ReviewRequested, ReviewApproved, ReviewDeclined, MergeCompleted, MergeFailed
    };
}
