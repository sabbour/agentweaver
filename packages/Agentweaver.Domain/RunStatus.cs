namespace Agentweaver.Domain;

public enum RunStatus
{
    Pending,
    InProgress,
    Completed,      // retained for backward-compat; new runs go to AwaitingReview instead
    Failed,
    AwaitingReview,
    /// <summary>
    /// Transient intermediate state: a /commit request has won the CAS gate and
    /// is staging+committing worktree changes before merging. Reverted to
    /// AwaitingReview on process restart so the user can retry (TOCTOU fix).
    /// </summary>
    Committing,
    /// <summary>
    /// Transient intermediate state: an approve request has won the CAS gate and
    /// the merge operation is in progress. Reverted to AwaitingReview on process
    /// restart or on any non-terminal failure (MF3).
    /// </summary>
    Merging,
    Merged,
    Declined,
    MergeFailed,
    /// <summary>
    /// Terminal state for a coordinator CHILD run (ParentRunId != null). The child completed
    /// its agent turn + RAI and produced a tree the coordinator will collect and review/merge
    /// collectively in Phase 3. A child NEVER runs its own review gate, merge, or scribe; it
    /// stops here. Its <see cref="Run.WorktreeBranch"/> + <see cref="Run.TreeHash"/> are the
    /// hand-off contract the coordinator's assemble wave reads.
    /// </summary>
    AssembleReady,
}
