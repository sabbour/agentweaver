namespace Scaffolder.Domain;

/// <summary>
/// A single agent run over a local git repository. Each run owns its own
/// isolated worktree (FR-002).
/// </summary>
public sealed record Run
{
    public required RunId Id { get; init; }
    public required string RepositoryPath { get; init; }    // absolute path to local git repo (FR-002)
    public required string OriginatingBranch { get; init; } // branch within that repo (FR-002)
    public required ModelSource ModelSource { get; init; }
    public required string Task { get; init; }              // natural-language prompt (FR-008)
    public required string SubmittingUser { get; init; }    // FR-024
    public required RunStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int StepCount { get; init; }
    public string? WorktreePath { get; init; }              // resolved at runtime
    public string? WorktreeBranch { get; init; }            // branch name for this run's worktree
    public string? CommittedTreeHash { get; init; }         // set after commit step
}
