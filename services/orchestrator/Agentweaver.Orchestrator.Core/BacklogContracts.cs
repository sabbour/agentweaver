using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public enum BacklogTaskState
{
    Backlog,
    Ready,
    Claimed
}

public sealed record BacklogTaskReference(string ProjectId, string TaskId);

public sealed record BacklogTaskSnapshot(
    BacklogTaskReference Reference,
    long Revision,
    BacklogTaskState State,
    bool IsArchived = false,
    bool IsAutomationInvocationPending = false);

public sealed record BacklogDependencyEdge(
    BacklogTaskReference Task,
    BacklogTaskReference Prerequisite);

public sealed record BacklogDependencyGraphSnapshot(
    string ProjectId,
    long Revision,
    ImmutableArray<BacklogTaskSnapshot> Tasks,
    ImmutableArray<BacklogDependencyEdge> Dependencies);

public enum BacklogIssueCode
{
    InvalidProjectId,
    InvalidTaskReference,
    InvalidGraphRevision,
    InvalidTaskRevision,
    InvalidTaskState,
    InvalidTaskCollection,
    InvalidDependencyCollection,
    DuplicateTask,
    MissingTask,
    CrossProjectDependency,
    SelfDependency,
    DuplicateDependency,
    DependencyCycle,
    TaskNotEditable,
    PrerequisiteUnavailable,
    StaleGraphRevision,
    GraphRevisionExhausted,
    TaskRevisionExhausted,
    InvalidPrerequisiteCollection,
    InvalidPrerequisiteSnapshot,
    DuplicatePrerequisiteSnapshot,
    MissingPrerequisiteSnapshot,
    UnexpectedPrerequisiteSnapshot,
    StaleTaskRevision,
    TaskNotEligible,
    StalePrerequisite,
    PrerequisiteMissing,
    PrerequisiteNotMerged,
    PrerequisiteArchived,
    PrerequisiteCancelled,
    PrerequisiteDelegated,
    PrerequisiteFailed,
    PrerequisiteIndeterminate,
    OutputProofMissing,
    OutputProofIndeterminate,
    OutputProofStale
}

public sealed record BacklogIssue(
    BacklogIssueCode Code,
    string Path,
    string Message);

public sealed class BacklogCoreResult<T> where T : class
{
    private BacklogCoreResult(T? value, ImmutableArray<BacklogIssue> issues) =>
        (Value, Issues) = (value, issues);

    public T? Value { get; }
    public ImmutableArray<BacklogIssue> Issues { get; }
    public bool IsSuccess => Value is not null && Issues.IsEmpty;

    public static BacklogCoreResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), []);

    public static BacklogCoreResult<T> Failure(ImmutableArray<BacklogIssue> issues)
    {
        if (issues.IsEmpty)
            throw new ArgumentException("A failed backlog operation requires at least one issue.", nameof(issues));
        return new BacklogCoreResult<T>(null, issues);
    }
}

public sealed record BacklogDependencyGraphEdit(
    BacklogDependencyGraph Graph,
    bool Changed);

public sealed record BacklogTaskEdit(
    BacklogDependencyGraph Graph,
    bool Changed);

public sealed record BacklogTaskClaimEdit(
    BacklogDependencyGraph Graph,
    BacklogReadinessEvaluation Readiness);

public enum BacklogPrerequisiteExecutionState
{
    Missing,
    Pending,
    Merged,
    Failed,
    Archived,
    Cancelled,
    Delegated,
    Indeterminate,
    Stale,
    Completed
}

public enum BacklogOutputProofState
{
    NotRequired,
    Verified,
    Missing,
    Indeterminate,
    Stale
}

public sealed record BacklogPrerequisiteExecutionSnapshot(
    BacklogTaskReference Prerequisite,
    long TaskRevision,
    long ExecutionRevision,
    BacklogPrerequisiteExecutionState State,
    BacklogOutputProofState OutputProofState,
    string? OutputProofReference);

public sealed record BacklogReadinessEvaluation(
    BacklogTaskReference Task,
    long GraphRevision,
    long TaskRevision,
    ImmutableArray<BacklogPrerequisiteExecutionSnapshot> Prerequisites,
    ImmutableArray<BacklogIssue> Blockers)
{
    public bool IsReady => Blockers.IsEmpty;
}
