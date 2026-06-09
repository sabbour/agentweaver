namespace Scaffolder.AgentRuntime.Workflow;

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

    /// <summary>Transitions the run from Merging to Merged with the given result.</summary>
    Task CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct);

    /// <summary>Reverts the run from Merging back to AwaitingReview (on Blocked or exception).</summary>
    Task RevertMergeAsync(string runId, CancellationToken ct);

    /// <summary>Transitions the run from Merging to MergeFailed with the given result.</summary>
    Task FailMergeAsync(string runId, string mergeResult, CancellationToken ct);
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
