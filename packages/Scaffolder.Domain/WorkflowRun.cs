namespace Scaffolder.Domain;

/// <summary>
/// The stable "job envelope" for a user-submitted task.
/// A workflow run can contain one or more executions (runs) — initially one,
/// with more added when the reviewer requests changes (future: retrigger creates new execution).
/// </summary>
public sealed record WorkflowRun
{
    public required string Id { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string Task { get; init; }
    public required string SubmittingUser { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
