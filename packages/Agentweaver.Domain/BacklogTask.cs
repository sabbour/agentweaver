namespace Agentweaver.Domain;

public sealed record BacklogTask
{
    public required BacklogTaskId Id { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required BacklogTaskState State { get; init; }
    /// <summary>
    /// Lexicographic fractional ordering key (e.g. "n", "u", "g3"). Sorts ascending = top of bucket
    /// first = highest pickup priority. Unique per (project_id, state) bucket for the unclaimed
    /// buckets (backlog/ready).
    /// </summary>
    public required string OrderKey { get; init; }
    /// <summary>The accountable human (signed-in user) who captured the task (Principle IX). Becomes
    /// the coordinator run's SubmittingUser AND the confirmedBy on the unattended outcome-spec confirm.</summary>
    public required string CapturedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Set when the task is first moved Backlog -> Ready. Null while in Backlog. Also a
    /// pickup tie-breaker.</summary>
    public DateTimeOffset? CommittedAt { get; init; }
    /// <summary>Set atomically with the Ready -> Claimed transition.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }
    /// <summary>The 1:1 coordinator run this task produced. Non-null iff State == Claimed.</summary>
    public RunId? RunId { get; init; }
}
