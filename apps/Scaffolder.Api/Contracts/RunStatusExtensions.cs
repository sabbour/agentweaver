using Scaffolder.Domain;

namespace Scaffolder.Api.Contracts;

public static class RunStatusExtensions
{
    public static string ToApiString(this RunStatus status) => status switch
    {
        RunStatus.Pending        => "pending",
        RunStatus.InProgress     => "in_progress",
        RunStatus.Completed      => "completed",
        RunStatus.Failed         => "failed",
        RunStatus.AwaitingReview => "awaiting_review",
        RunStatus.Merging        => "merging",
        RunStatus.Merged         => "merged",
        RunStatus.Declined       => "declined",
        RunStatus.MergeFailed    => "merge_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static RunStatus ParseStatus(string value) => value switch
    {
        "pending"         => RunStatus.Pending,
        "in_progress"     => RunStatus.InProgress,
        "completed"       => RunStatus.Completed,
        "failed"          => RunStatus.Failed,
        "awaiting_review" => RunStatus.AwaitingReview,
        "merging"         => RunStatus.Merging,
        "merged"          => RunStatus.Merged,
        "declined"        => RunStatus.Declined,
        "merge_failed"    => RunStatus.MergeFailed,
        _ => throw new ArgumentException($"Unknown run status: {value}", nameof(value))
    };
}
