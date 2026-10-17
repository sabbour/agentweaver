using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Supported schedule cadence; monthly dates are limited to 1–28 to exist in every month.</summary>
public enum WorkflowScheduleInterval
{
    Daily,
    Weekly,
    Monthly
}

/// <summary>UTC schedule fields for one stable trigger identity.</summary>
public sealed record WorkflowScheduleDefinition
{
    public required string Id { get; init; }
    public required WorkflowScheduleInterval? Interval { get; init; }
    public required TimeOnly? TimeOfDay { get; init; }
    public DayOfWeek? DayOfWeek { get; init; }
    public int? DayOfMonth { get; init; }
}

/// <summary>Reasons a schedule cannot be evaluated.</summary>
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

/// <summary>One schedule validation issue with the affected field path.</summary>
public sealed record WorkflowScheduleIssue(
    WorkflowScheduleIssueCode Code,
    string Path,
    string Message);

/// <summary>A due schedule occurrence with stable period identity for replay-safe admission.</summary>
public sealed record WorkflowScheduleOccurrence(
    string TriggerId,
    string PeriodKey,
    DateTimeOffset ScheduledAtUtc)
{
    /// <summary>Builds a stable key that distinguishes workflow, trigger, and occurrence period.</summary>
    /// <exception cref="ArgumentException">The workflow ID is not stable.</exception>
    /// <exception cref="InvalidOperationException">The trigger ID is not stable.</exception>
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

/// <summary>Schedule result; invalidity is separate from whether an occurrence is due.</summary>
public sealed record WorkflowScheduleEvaluation(
    WorkflowScheduleOccurrence? Occurrence,
    WorkflowScheduleIssue? Issue)
{
    /// <summary>True when no schedule validation issue was found.</summary>
    public bool IsValid => Issue is null;
    /// <summary>True when an occurrence exists at or before the evaluation time.</summary>
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

/// <summary>Review state accepted by GitHub pull-request-review predicates.</summary>
public enum WorkflowTriggerReviewState
{
    Approved,
    ChangesRequested,
    Commented
}

/// <summary>String comparison used to match a push ref.</summary>
public enum WorkflowTriggerMatchMode
{
    Equals,
    Prefix
}

/// <summary>One event predicate; exactly one condition or one logical combinator must be set.</summary>
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

/// <summary>Case-insensitive label-presence predicate.</summary>
public sealed record WorkflowTriggerLabelPredicate
{
    public required string Label { get; init; }
}

/// <summary>Exact pull-request base-branch predicate.</summary>
public sealed record WorkflowTriggerBaseBranchPredicate
{
    public required string Branch { get; init; }
}

/// <summary>Expected state of a pull-request review event.</summary>
public sealed record WorkflowTriggerReviewStatePredicate
{
    public required WorkflowTriggerReviewState State { get; init; }
}

/// <summary>Push-ref value and exact or prefix comparison mode.</summary>
public sealed record WorkflowTriggerRefPredicate
{
    public required string Branch { get; init; }
    public required WorkflowTriggerMatchMode MatchMode { get; init; }
}

/// <summary>Discussion category name to match.</summary>
public sealed record WorkflowTriggerCategoryPredicate
{
    public required string Name { get; init; }
}

/// <summary>Bounded regular-expression condition for an issue-comment body.</summary>
public sealed record WorkflowTriggerCommentMatchesPredicate
{
    public required string Pattern { get; init; }
}

/// <summary>Normalized event fields available to the predicate evaluator.</summary>
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

/// <summary>Structured predicate parsing, event compatibility, and runtime failure codes.</summary>
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

/// <summary>Predicate validation or evaluation issue with the relevant predicate path.</summary>
public sealed record WorkflowTriggerPredicateIssue(
    WorkflowTriggerPredicateIssueCode Code,
    string Path,
    string Message);

/// <summary>Validation result for a complete predicate set.</summary>
public sealed record WorkflowTriggerPredicateValidationResult(
    ImmutableArray<WorkflowTriggerPredicateIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;

    internal static WorkflowTriggerPredicateValidationResult Success() => new([]);
}

/// <summary>Event match result; malformed predicates are invalid and always do not match.</summary>
public sealed record WorkflowTriggerPredicateEvaluation(
    bool Matches,
    ImmutableArray<WorkflowTriggerPredicateIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}
