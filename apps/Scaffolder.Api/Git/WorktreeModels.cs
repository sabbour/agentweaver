namespace Scaffolder.Api.Git;

/// <summary>Location of a run's worktree and the branch checked out into it.</summary>
public sealed record WorktreeInfo
{
    public required string WorktreePath { get; init; }
    public required string BranchName { get; init; }
}

/// <summary>Outcome of an attempted merge back into the originating branch.</summary>
public sealed record MergeOutcome
{
    public required bool Success { get; init; }
    public string? ConflictDetails { get; init; }
    public string? MergedCommitHash { get; init; }

    public static MergeOutcome Merged(string commitHash) => new()
    {
        Success = true,
        MergedCommitHash = commitHash
    };

    public static MergeOutcome Conflict(string details) => new()
    {
        Success = false,
        ConflictDetails = details
    };
}
