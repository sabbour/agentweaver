namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// Represents the git worktree session for a run. One session per run.
/// </summary>
public sealed class SessionEntity
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    /// <summary>
    /// Absolute path to the artifact directory (the worktree checkout directory).
    /// Must resolve under the configured run-root.
    /// </summary>
    public required string ArtifactDir { get; set; }

    /// <summary>
    /// Absolute path to the git worktree. Same as ArtifactDir in our implementation.
    /// </summary>
    public required string WorktreePath { get; set; }

    /// <summary>
    /// The SHA of the commit on originatingBranch at worktree creation time.
    /// Used as the base for diffing.
    /// </summary>
    public required string OriginatingCommit { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public RunEntity Run { get; set; } = null!;
}