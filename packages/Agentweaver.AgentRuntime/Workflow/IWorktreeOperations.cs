namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Abstraction over worktree git operations needed by workflow executors.
/// Implemented by WorktreeManager in the API project.
/// </summary>
public interface IWorktreeOperations
{
    void ApplyPreparedWriteback(
        string repositoryPath,
        string worktreePath,
        string worktreeBranch,
        string runId,
        PreparedWriteback writeback) =>
        throw new WorktreeWritebackException(
            "writeback_not_supported",
            "This worktree provider does not support pod-local write-back.");
    string CommitChanges(string worktreePath, string runId);
    string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch);
    int GetStepCount(string runId);
    MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash);
    void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch);
    bool WorktreeExists(string worktreePath);
    string? GetTreeHash(string worktreePath);

    /// <summary>
    /// CONSERVATIVELY clears a STALE <c>index.lock</c> for the given worktree between post-turn
    /// commit retries (the in-place-revision wedge root cause: a crashed/lingering process left the
    /// index locked). Resolves the ACTUAL gitdir (linked worktrees use a <c>.git</c> pointer file),
    /// only deletes when the lock is older than the configured stale threshold
    /// (<c>Coordinator:StaleLockThresholdSeconds</c>, default 15s) AND no live git process is
    /// detected — otherwise refuses (best-effort, never throws). Returns diagnostics for evidence.
    /// Default no-op for implementations that don't back a real git worktree (test fakes).
    /// </summary>
    IndexLockClearResult TryClearStaleIndexLock(string worktreePath) =>
        new(LockPresent: false, Cleared: false, LockAgeSeconds: null, LiveGitProcessDetected: false,
            LockPath: null, Detail: "no-op");
}

/// <summary>Typed failure while validating or applying a pod-prepared write-back commit.</summary>
public sealed class WorktreeWritebackException : Exception
{
    public WorktreeWritebackException(string reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public string Reason { get; }
}

/// <summary>Diagnostics from <see cref="IWorktreeOperations.TryClearStaleIndexLock"/>.</summary>
public sealed record IndexLockClearResult(
    bool LockPresent,
    bool Cleared,
    double? LockAgeSeconds,
    bool LiveGitProcessDetected,
    string? LockPath,
    string? Detail);

/// <summary>Simplified merge result for the workflow executor.</summary>
public sealed record MergeResult(
    MergeResultKind Kind,
    string? CommitHash,
    string? MergeMode,
    string? PreviousHeadSha,
    string? NewHeadSha,
    bool WasFastForward,
    string? Reason,
    IReadOnlyList<string>? ConflictingFiles = null);

public enum MergeResultKind
{
    Merged,
    Blocked,
    Conflict
}
