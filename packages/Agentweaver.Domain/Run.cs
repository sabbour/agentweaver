namespace Agentweaver.Domain;

/// <summary>
/// A single agent run. Each run is a task sent to the agent; the result is
/// stored when the run completes.
/// </summary>
public sealed record Run
{
    public required RunId Id { get; init; }
    public required string RepositoryPath { get; init; }
    public required string OriginatingBranch { get; init; }
    public required ModelSource ModelSource { get; init; }
    public required string Task { get; init; }
    public required string SubmittingUser { get; init; }
    public required RunStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Result { get; init; }
    public string? WorktreePath { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? TreeHash { get; init; }
    public int StepCount { get; init; }
    public string? Diff { get; init; }
    /// <summary>
    /// JSON array of conflicting file paths when the run is in MergeFailed due to a merge conflict.
    /// Null when there are no conflicts or when the status is not MergeFailed.
    /// Example: ["src/foo.cs","src/bar.cs"]
    /// </summary>
    public string? MergeConflicts { get; init; }
    public ProjectId? ProjectId { get; init; }
    public string? ModelId { get; init; }
    /// <summary>The cast team member name executing this run (e.g. "morpheus"). Null for non-team runs.</summary>
    public string? AgentName { get; init; }
    /// <summary>The agent's charter content injected as system prompt. Null for non-team runs.</summary>
    public string? AgentCharter { get; init; }
    /// <summary>The GitHub login of the user who reviewed this run. Null until the run is reviewed.</summary>
    public string? ReviewedBy { get; init; }
    /// <summary>The workflow run this execution belongs to. Null for legacy runs created before this field was introduced.</summary>
    public string? WorkflowRunId { get; init; }
    /// <summary>
    /// The commit SHA on the originating branch produced by the merge.
    /// Populated once the run transitions to Merged; null for older runs or non-merged runs.
    /// Used by the workspace endpoint to serve file content from git after the worktree is deleted.
    /// </summary>
    public string? MergedCommitHash { get; init; }
    /// <summary>The coordinator run that launched this child run. Null for the coordinator run itself and for ordinary single-agent runs.</summary>
    public string? ParentRunId { get; init; }
    /// <summary>The Subtask.Id this child run executes. Null for non-orchestrated runs.</summary>
    public string? SubtaskId { get; init; }
    /// <summary>Durable provenance marker. Defaults to <see cref="RunOrigin.Interactive"/>; only the
    /// backlog-pickup claim+reserve transaction stamps <see cref="RunOrigin.BacklogPickup"/>.</summary>
    public RunOrigin Origin { get; init; } = RunOrigin.Interactive;

    /// <summary>
    /// The run_id of the FAILED run this run was retriggered from (POST /api/runs/{id}/retry).
    /// Null for runs that were not produced by a retry. Forms a provenance chain (a retry of a retry
    /// points at its immediate predecessor) used to surface lineage in the UI and to enforce the
    /// soft retry-depth cap.
    /// </summary>
    public string? RetriedFrom { get; init; }
}
