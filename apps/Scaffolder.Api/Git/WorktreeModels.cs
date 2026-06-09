namespace Scaffolder.Api.Git;

/// <summary>Location of a run's worktree and the branch checked out into it.</summary>
public sealed record WorktreeInfo
{
    public required string WorktreePath { get; init; }
    public required string BranchName { get; init; }
}

/// <summary>Discriminated kind for a <see cref="MergeOutcome"/>.</summary>
public enum MergeOutcomeKind
{
    /// <summary>Merge succeeded; originating branch ref (and working tree if checked out) updated.</summary>
    Merged,

    /// <summary>
    /// Retriable precondition failure. No mutations occurred.
    /// Client should fix the condition and re-approve.
    /// </summary>
    Blocked,

    /// <summary>
    /// Terminal failure. Merge conflicts or invariant violations.
    /// The run transitions to MergeFailed; the worktree is preserved.
    /// </summary>
    Conflict,
}

/// <summary>
/// Outcome of an attempted merge back into the originating branch.
/// Use the <see cref="Kind"/> discriminator to determine which fields are populated.
/// </summary>
public sealed record MergeOutcome
{
    public required MergeOutcomeKind Kind { get; init; }

    // Merged fields
    /// <summary>Final commit SHA on the originating branch after the merge. Populated when Kind == Merged.</summary>
    public string? CommitHash { get; init; }

    /// <summary>"ref-only" when the branch was not checked out; "working-tree-reset" when it was. Populated when Kind == Merged.</summary>
    public string? MergeMode { get; init; }

    /// <summary>SHA of the originating branch tip before the merge. Populated when Kind == Merged.</summary>
    public string? PreviousHeadSha { get; init; }

    /// <summary>SHA of the originating branch tip after the merge. Populated when Kind == Merged.</summary>
    public string? NewHeadSha { get; init; }

    /// <summary>True when no merge commit was required. Populated when Kind == Merged.</summary>
    public bool WasFastForward { get; init; }

    // Blocked / Conflict fields
    /// <summary>
    /// Human-readable category reason. Safe for logging — never contains raw file content,
    /// absolute paths, or secrets.
    /// </summary>
    public string? Reason { get; init; }

    public static MergeOutcome Merged(
        string commitHash,
        string mergeMode,
        string previousHeadSha,
        string newHeadSha,
        bool wasFastForward) => new()
    {
        Kind           = MergeOutcomeKind.Merged,
        CommitHash     = commitHash,
        MergeMode      = mergeMode,
        PreviousHeadSha = previousHeadSha,
        NewHeadSha     = newHeadSha,
        WasFastForward = wasFastForward,
    };

    public static MergeOutcome Blocked(string reason) => new()
    {
        Kind   = MergeOutcomeKind.Blocked,
        Reason = reason,
    };

    public static MergeOutcome Conflict(string reason) => new()
    {
        Kind   = MergeOutcomeKind.Conflict,
        Reason = reason,
    };
}
