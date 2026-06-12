namespace Scaffolder.Domain;

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
}
