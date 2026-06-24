namespace Agentweaver.Domain;

public enum ClaimReserveResult { Won, Lost, ProjectUnavailable }

/// <summary>
/// Project-scoped persistence for backlog tasks. Every read and mutation includes the
/// <see cref="ProjectId"/> in its WHERE clause, so a task can never be read or mutated through the
/// wrong project route.
/// </summary>
public interface IBacklogTaskStore
{
    Task InsertAsync(BacklogTask task, CancellationToken ct = default);

    /// <summary>Project-scoped get. Returns null if the id does not exist OR belongs to another project.</summary>
    Task<BacklogTask?> GetAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default);

    /// <summary>Durable run-id -> backlog task lookup over the unique idx_backlog_tasks_run index.
    /// Returns the (at most one) Claimed task whose run_id == <paramref name="runId"/>, or null.</summary>
    Task<BacklogTask?> GetByRunIdAsync(RunId runId, CancellationToken ct = default);

    /// <summary>All tasks for a project (Backlog + Ready + Claimed), ordered by (state, order_key).</summary>
    Task<IReadOnlyList<BacklogTask>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>Ready, unclaimed tasks for a project ordered by (order_key ASC, committed_at ASC,
    /// task_id ASC), capped at <paramref name="limit"/>. Deterministic top-N claim candidates.</summary>
    Task<IReadOnlyList<BacklogTask>> ListReadyForClaimAsync(
        ProjectId projectId, int limit, CancellationToken ct = default);

    /// <summary>Updates title/description only, gated on project_id. Returns false if not found in project.</summary>
    Task<bool> UpdateContentAsync(
        ProjectId projectId, BacklogTaskId id, string title, string? description, CancellationToken ct = default);

    /// <summary>Deletes a task, gated on project_id AND state IN ('backlog','ready') AND run_id IS NULL.
    /// Returns false if Claimed (cannot delete a run-backed task) or not found in project.</summary>
    Task<bool> TryDeleteAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default);

    /// <summary>Archives a task off the active board. Claimed tasks archive their linked coordinator run
    /// in the same transaction so the run card also disappears from board projections.</summary>
    Task<bool> TryArchiveAsync(
        ProjectId projectId, BacklogTaskId id, DateTimeOffset archivedAt, CancellationToken ct = default);

    /// <summary>Atomic Backlog -> Ready. Sets committed_at and the destination order_key. Gated on
    /// project_id AND state = 'backlog'. Retries on order_key UNIQUE conflict.</summary>
    Task<bool> TryMoveToReadyAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default);

    /// <summary>
    /// Atomic bulk Backlog -> Ready for an ENTIRE project. Moves every state='backlog' task to
    /// state='ready' in ONE transaction, appended AFTER existing Ready items while preserving the
    /// tasks' relative backlog order, stamping committed_at = <paramref name="committedAt"/> on each.
    /// Idempotent: returns 0 when the backlog bucket is empty. Returns the count of tasks moved.
    /// </summary>
    Task<int> MoveAllBacklogToReadyAsync(
        ProjectId projectId, DateTimeOffset committedAt, CancellationToken ct = default);

    /// <summary>Atomic Ready -> Backlog, permitted only while unclaimed. Gated on project_id AND
    /// state = 'ready' AND run_id IS NULL. Returns false if already claimed or not found.</summary>
    Task<bool> TryMoveToBacklogAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, CancellationToken ct = default);

    /// <summary>Reorders a task within its CURRENT bucket by assigning a new order_key. Gated on
    /// project_id AND state = $expectedState AND run_id IS NULL. Retries on UNIQUE conflict.</summary>
    Task<bool> TryReorderAsync(
        ProjectId projectId, BacklogTaskId id, BacklogTaskState expectedState, string newOrderKey, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, when <paramref name="workflowId"/> is null) the per-task workflow override
    /// (Feature 010, FR-042). Gated on project_id AND state IN ('backlog','ready') — an override may
    /// only be chosen while the task is unclaimed. Returns false if Claimed or not found in project.
    /// </summary>
    Task<bool> UpdateWorkflowOverrideAsync(
        ProjectId projectId, BacklogTaskId id, string? workflowId, CancellationToken ct = default);

    /// <summary>
    /// Atomic, exactly-once claim + coordinator-run reservation. In ONE transaction:
    /// (a) Ready -> Claimed gated on project_id AND state='ready' AND run_id IS NULL, binding run_id;
    /// (b) INSERT the coordinator <paramref name="coordinatorRun"/> row gated on the project being
    ///     active, stamping the durable run-origin marker origin='backlog_pickup'.
    /// Returns Won/Lost/ProjectUnavailable. The reserved run is identity-shaped EXACTLY like an
    /// interactive coordinator run (workflow_run_id IS NULL, no workflow_runs envelope) so every
    /// coordinator-run detail endpoint resolves it by run_id.
    /// </summary>
    Task<ClaimReserveResult> TryClaimAndReserveCoordinatorRunAsync(
        ProjectId projectId,
        BacklogTaskId id,
        Run coordinatorRun,
        DateTimeOffset claimedAt,
        CancellationToken ct = default);
}
