using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Lifecycle state for a project-owned backlog task.</summary>
public enum BacklogTaskState
{
    Backlog,
    Ready,
    Claimed
}

/// <summary>Stable project and task identity used by the project-scoped dependency graph.</summary>
public sealed record BacklogTaskReference(string ProjectId, string TaskId);

/// <summary>Owner-projected task state and revision consumed by graph and readiness checks.</summary>
public sealed record BacklogTaskSnapshot(
    BacklogTaskReference Reference,
    long Revision,
    BacklogTaskState State,
    bool IsArchived = false,
    bool IsAutomationInvocationPending = false);

/// <summary>Directed prerequisite relation: <see cref="Task"/> cannot proceed before <see cref="Prerequisite"/>.</summary>
public sealed record BacklogDependencyEdge(
    BacklogTaskReference Task,
    BacklogTaskReference Prerequisite);

/// <summary>Serializable owner snapshot used to validate and rebuild an immutable backlog graph.</summary>
public sealed record BacklogDependencyGraphSnapshot(
    string ProjectId,
    long Revision,
    ImmutableArray<BacklogTaskSnapshot> Tasks,
    ImmutableArray<BacklogDependencyEdge> Dependencies);

/// <summary>Structured validation and readiness failures returned by backlog operations.</summary>
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

/// <summary>A backlog issue with a machine-readable code, data path, and explanation.</summary>
public sealed record BacklogIssue(
    BacklogIssueCode Code,
    string Path,
    string Message);

/// <summary>Success-or-issues result for pure backlog operations; failures never carry a value.</summary>
public sealed class BacklogCoreResult<T> where T : class
{
    private BacklogCoreResult(T? value, ImmutableArray<BacklogIssue> issues) =>
        (Value, Issues) = (value, issues);

    public T? Value { get; }
    public ImmutableArray<BacklogIssue> Issues { get; }
    public bool IsSuccess => Value is not null && Issues.IsEmpty;

    /// <summary>Creates a successful result and rejects a null value.</summary>
    public static BacklogCoreResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), []);

    /// <summary>Creates a failed result; an empty issue list is rejected as invalid use.</summary>
    public static BacklogCoreResult<T> Failure(ImmutableArray<BacklogIssue> issues)
    {
        if (issues.IsEmpty)
            throw new ArgumentException("A failed backlog operation requires at least one issue.", nameof(issues));
        return new BacklogCoreResult<T>(null, issues);
    }
}

/// <summary>Result of an edge edit, including whether an effective graph change occurred.</summary>
public sealed record BacklogDependencyGraphEdit(
    BacklogDependencyGraph Graph,
    bool Changed);

/// <summary>Result of a task mutation, including whether revisions advanced.</summary>
public sealed record BacklogTaskEdit(
    BacklogDependencyGraph Graph,
    bool Changed);

/// <summary>Claim result paired with the readiness evidence used for the claim.</summary>
public sealed record BacklogTaskClaimEdit(
    BacklogDependencyGraph Graph,
    BacklogReadinessEvaluation Readiness);

/// <summary>Owner-reported state of a task that is a prerequisite for another task.</summary>
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

/// <summary>Owner-reported verification state for a prerequisite's captured outputs.</summary>
public enum BacklogOutputProofState
{
    NotRequired,
    Verified,
    Missing,
    Indeterminate,
    Stale
}

/// <summary>Versioned execution and output-proof evidence for one prerequisite task.</summary>
public sealed record BacklogPrerequisiteExecutionSnapshot(
    BacklogTaskReference Prerequisite,
    long TaskRevision,
    long ExecutionRevision,
    BacklogPrerequisiteExecutionState State,
    BacklogOutputProofState OutputProofState,
    string? OutputProofReference);

/// <summary>Readiness decision bound to graph/task revisions and the complete prerequisite snapshot set.</summary>
public sealed record BacklogReadinessEvaluation(
    BacklogTaskReference Task,
    long GraphRevision,
    long TaskRevision,
    ImmutableArray<BacklogPrerequisiteExecutionSnapshot> Prerequisites,
    ImmutableArray<BacklogIssue> Blockers)
{
    /// <summary>True only when no task-eligibility or prerequisite blocker was found.</summary>
    public bool IsReady => Blockers.IsEmpty;
}
