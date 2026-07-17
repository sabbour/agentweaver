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
    /// <summary>
    /// Non-terminal DORMANT state for an Assistant/Operator conversation that has gone idle beyond
    /// the idle-timeout with NO pending human approval. The conversation is PAUSED, not ended: its
    /// durable event stream is NOT sealed (no terminal <c>run.completed</c> marker) and it can be
    /// transparently woken back to <see cref="InProgress"/> on the next message (see
    /// AssistantRunService.RehydrateRunAsync), continuing as the SAME run id with prior history
    /// intact. Distinct from <see cref="Completed"/>, which is genuinely terminal and unresumable.
    /// MUST remain the LAST enum value: <c>Run.Status</c> is persisted by string name
    /// (RunStatusExtensions.ToApiString/ParseStatus), so ordinal position is not load-bearing, but
    /// appending is kept as a defensive invariant so any future ordinal-based persistence cannot
    /// silently renumber the pre-existing values.
    /// </summary>
    Idle,
}
