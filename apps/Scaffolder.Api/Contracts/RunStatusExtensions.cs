using Scaffolder.Domain;

namespace Scaffolder.Api.Contracts;

/// <summary>
/// Maps <see cref="RunStatus"/> to the lower-case, snake-case API string used
/// in responses and operational records.
/// </summary>
public static class RunStatusExtensions
{
    public static string ToApiString(this RunStatus status) => status switch
    {
        RunStatus.Pending => "pending",
        RunStatus.InProgress => "in_progress",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Bounded => "bounded",
        RunStatus.Reviewing => "reviewing",
        RunStatus.Approved => "approved",
        RunStatus.Declined => "declined",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
