namespace Agentweaver.Domain;

/// <summary>
/// The stable "job envelope" for a user-submitted task.
/// A workflow run can contain one or more executions (runs) — initially one,
/// with more added when the reviewer requests changes (future: retrigger creates new execution).
/// </summary>
public sealed record WorkflowRun
{
    public required string Id { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string Task { get; init; }
    public required string SubmittingUser { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Path to the single shared git worktree for this orchestration.
    /// All child runs in a multi-agent coordinator workflow share this sandbox root
    /// so agents can read each other's produced files without sandbox boundary violations.
    /// Null for single-agent (non-coordinator) workflow runs.
    /// </summary>
    public string? OrchestrationWorktreePath { get; init; }
}
