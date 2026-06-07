namespace Scaffolder.Api.Persistence;

public enum ModelSource
{
    CopilotSdk,
    MicrosoftFoundry
}

public enum RunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Bounded,
    AwaitingReview,
    Approved,
    Declined,
    Merged,
    MergeConflict
}

public enum EventType
{
    // Lifecycle
    RunStarted,
    RunCompleted,
    RunFailed,
    RunBounded,
    // Content
    AgentMessage,
    ToolCall,
    ToolResult,
    ToolRejected,
    ToolError,
    // Review/merge
    ReviewRequested,
    ReviewApproved,
    ReviewDeclined,
    MergeCompleted,
    MergeFailed
}

public enum ToolName
{
    ReadFile,
    WriteFile
}

public enum ToolResult
{
    Success,
    Rejected,
    Error
}

public enum ToolErrorCode
{
    PathEscape,
    NotFound,
    Permission,
    Unknown
}

public enum ReviewDecisionType
{
    Approve,
    Decline
}

public enum MergeResult
{
    NotAttempted,
    Merged,
    Conflict,
    Failed
}

public enum RunOutcome
{
    Completed,
    Failed,
    Bounded,
    Merged,
    Declined,
    MergeConflict
}
