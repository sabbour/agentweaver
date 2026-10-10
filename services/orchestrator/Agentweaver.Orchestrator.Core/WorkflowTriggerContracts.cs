using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public enum WorkflowScheduleInterval
{
    Daily,
    Weekly,
    Monthly
}

public sealed record WorkflowScheduleDefinition
{
    public required string Id { get; init; }
    public required WorkflowScheduleInterval? Interval { get; init; }
    public required TimeOnly? TimeOfDay { get; init; }
    public DayOfWeek? DayOfWeek { get; init; }
    public int? DayOfMonth { get; init; }
}

public enum WorkflowScheduleIssueCode
{
    MissingSchedule,
    InvalidTriggerId,
    InvalidInterval,
    MissingTimeOfDay,
    InvalidDayOfWeek,
    InvalidDayOfMonth,
    UnexpectedDayOfWeek,
    UnexpectedDayOfMonth,
    OccurrenceOutsideDateRange
}

public sealed record WorkflowScheduleIssue(
    WorkflowScheduleIssueCode Code,
    string Path,
    string Message);

public sealed record WorkflowScheduleOccurrence(
    string TriggerId,
    string PeriodKey,
    DateTimeOffset ScheduledAtUtc)
{
    public string GetIdempotencyKey(string workflowId)
    {
        if (!WorkflowValidationSupport.IsStableId(workflowId))
            throw new ArgumentException("Workflow ID must be a stable identifier.", nameof(workflowId));
        if (!WorkflowValidationSupport.IsStableId(TriggerId))
            throw new InvalidOperationException("Trigger ID must be a stable identifier.");

        return $"workflow-schedule-trigger:{Uri.EscapeDataString(workflowId)}:" +
               $"{Uri.EscapeDataString(TriggerId)}:{PeriodKey}";
    }
}

public sealed record WorkflowScheduleEvaluation(
    WorkflowScheduleOccurrence? Occurrence,
    WorkflowScheduleIssue? Issue)
{
    public bool IsValid => Issue is null;
    public bool IsDue => Occurrence is not null;

    internal static WorkflowScheduleEvaluation Due(WorkflowScheduleOccurrence occurrence) =>
        new(occurrence, null);

    internal static WorkflowScheduleEvaluation NotDue() => new(null, null);

    internal static WorkflowScheduleEvaluation Invalid(
        WorkflowScheduleIssueCode code,
        string path,
        string message) =>
        new(null, new WorkflowScheduleIssue(code, path, message));
}

public enum WorkflowTriggerReviewState
{
    Approved,
    ChangesRequested,
    Commented
}

public enum WorkflowTriggerMatchMode
{
    Equals,
    Prefix
}

public sealed record WorkflowTriggerPredicate
{
    public WorkflowTriggerLabelPredicate? HasLabel { get; init; }
    public WorkflowTriggerLabelPredicate? IsNotLabeledWith { get; init; }
    public WorkflowTriggerBaseBranchPredicate? BaseBranch { get; init; }
    public WorkflowTriggerReviewStatePredicate? ReviewState { get; init; }
    public WorkflowTriggerRefPredicate? Ref { get; init; }
    public WorkflowTriggerCategoryPredicate? Category { get; init; }
    public WorkflowTriggerCommentMatchesPredicate? CommentMatches { get; init; }
    public IReadOnlyList<WorkflowTriggerPredicate>? Or { get; init; }
    public WorkflowTriggerPredicate? Not { get; init; }
}

public sealed record WorkflowTriggerLabelPredicate
{
    public required string Label { get; init; }
}

public sealed record WorkflowTriggerBaseBranchPredicate
{
    public required string Branch { get; init; }
}

public sealed record WorkflowTriggerReviewStatePredicate
{
    public required WorkflowTriggerReviewState State { get; init; }
}

public sealed record WorkflowTriggerRefPredicate
{
    public required string Branch { get; init; }
    public required WorkflowTriggerMatchMode MatchMode { get; init; }
}

public sealed record WorkflowTriggerCategoryPredicate
{
    public required string Name { get; init; }
}

public sealed record WorkflowTriggerCommentMatchesPredicate
{
    public required string Pattern { get; init; }
}

public sealed record WorkflowTriggerEventContext
{
    public required string EventName { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? PullRequestBaseBranch { get; init; }
    public string? ReviewState { get; init; }
    public string? Ref { get; init; }
    public string? DiscussionCategory { get; init; }
    public string? CommentBody { get; init; }
}

public enum WorkflowTriggerPredicateIssueCode
{
    InvalidEventName,
    InvalidPredicateShape,
    UnsupportedPredicateForEvent,
    MissingValue,
    InvalidEnumValue,
    InvalidRegex,
    PredicateLimitExceeded
}

public sealed record WorkflowTriggerPredicateIssue(
    WorkflowTriggerPredicateIssueCode Code,
    string Path,
    string Message);

public sealed record WorkflowTriggerPredicateValidationResult(
    ImmutableArray<WorkflowTriggerPredicateIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;

    internal static WorkflowTriggerPredicateValidationResult Success() => new([]);
}

public sealed record WorkflowTriggerPredicateEvaluation(
    bool Matches,
    ImmutableArray<WorkflowTriggerPredicateIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}
