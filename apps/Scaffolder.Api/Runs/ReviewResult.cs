using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>Outcome category of a review submission.</summary>
public enum ReviewDecisionOutcome
{
    Approved,
    Declined,
    Rejected
}

/// <summary>
/// Result of <see cref="RunOrchestrator.SubmitReviewAsync"/>. <see cref="Rejected"/>
/// indicates the review could not be applied (for example the run is not awaiting
/// review, or a decision was already recorded).
/// </summary>
public sealed record ReviewResult
{
    public required ReviewDecisionOutcome Outcome { get; init; }
    public RunStatus Status { get; init; }
    public string? MergeResult { get; init; }
    public string? RejectionReason { get; init; }

    public static ReviewResult Rejected(string reason) => new()
    {
        Outcome = ReviewDecisionOutcome.Rejected,
        RejectionReason = reason
    };
}
