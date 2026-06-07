namespace Scaffolder.Api.Persistence.Entities;

/// <summary>
/// Records the human review decision for a completed run.
/// One decision per run (unique FK on RunId).
/// The human-approval gate is enforced before any merge action (FR-015, FR-016).
/// </summary>
public sealed class ReviewDecisionEntity
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public ReviewDecisionType Decision { get; set; }

    /// <summary>
    /// Identity of the reviewer who made the approve/decline decision.
    /// </summary>
    public required string Reviewer { get; set; }

    /// <summary>
    /// Optional human comment accompanying the decision.
    /// </summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Outcome of the merge operation after an Approve decision.
    /// NotAttempted for Decline decisions or before merge is invoked.
    /// </summary>
    public MergeResult MergeResult { get; set; } = MergeResult.NotAttempted;

    // Navigation
    public RunEntity Run { get; set; } = null!;
}
