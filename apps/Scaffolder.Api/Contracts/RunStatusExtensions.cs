using Scaffolder.Domain;

namespace Scaffolder.Api.Contracts;

public static class RunStatusExtensions
{
    public static string ToApiString(this RunStatus status) => status switch
    {
        RunStatus.Pending    => "pending",
        RunStatus.InProgress => "in_progress",
        RunStatus.Completed  => "completed",
        RunStatus.Failed     => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
