namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Coordinates merge operations between the workflow executor and the persistence layer.
/// Implemented in the API project where SqliteRunStore and RepositoryMergeLock live.
/// </summary>
public interface IMergeCoordinator
{
    /// <summary>
    /// Acquires the per-repository lock and performs the CAS transition AwaitingReview -> Merging.
    /// Returns a result indicating success or failure.
    /// </summary>
    Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct);

    /// <summary>Transitions the run from Merging to Merged with the given result. Returns false on concurrency conflict.</summary>
    Task<bool> CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct);

    /// <summary>Reverts the run from Merging back to AwaitingReview (on Blocked or exception).</summary>
    Task RevertMergeAsync(string runId, CancellationToken ct);

    /// <summary>Transitions the run from Merging to MergeFailed with the given result. Returns false on concurrency conflict.</summary>
    Task<bool> FailMergeAsync(string runId, string mergeResult, string? mergeConflictsJson, CancellationToken ct);

    /// <summary>
    /// Executes the full merge flow: acquire lock, CAS, merge worktree, transition run state.
    /// Callers requiring friendly pre-merge validation (worktree-exists, tree-hash match) must
    /// perform those checks BEFORE calling; ExecuteMergeAsync trusts the provided state.
    /// </summary>
    Task<MergeExecutionResult> ExecuteMergeAsync(MergeInput input, CancellationToken ct);
}

/// <summary>Result of attempting to acquire the merge lock + CAS gate.</summary>
public sealed class MergeLockResult
{
    public bool Acquired { get; init; }
    public string? Reason { get; init; }
    private readonly IDisposable? _lockHandle;

    private MergeLockResult(bool acquired, string? reason, IDisposable? lockHandle)
    {
        Acquired = acquired;
        Reason = reason;
        _lockHandle = lockHandle;
    }

    public void Release() => _lockHandle?.Dispose();

    public static MergeLockResult Success(IDisposable lockHandle) => new(true, null, lockHandle);
    public static MergeLockResult Failed(string reason) => new(false, reason, null);
}

/// <summary>Outcome of a consolidated merge execution.</summary>
public enum MergeExecutionOutcome
{
    Merged,
    Blocked,
    Conflict,
    LockFailed,
    InternalError
}

/// <summary>Result of ExecuteMergeAsync containing outcome details for caller mapping.</summary>
public sealed record MergeExecutionResult
{
    public required MergeExecutionOutcome Outcome { get; init; }
    public string? MergeResult { get; init; }
    public string? CommitHash { get; init; }
    public string? MergeMode { get; init; }
    public string? PreviousHeadSha { get; init; }
    public string? Reason { get; init; }
    public string? LockFailureReason { get; init; }
    /// <summary>
    /// Populated when Outcome == Conflict. Contains the list of file paths that conflicted.
    /// </summary>
    public IReadOnlyList<string>? ConflictingFiles { get; init; }
}
