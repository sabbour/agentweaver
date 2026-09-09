using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Decorates an <see cref="IRunStore"/> (in practice <see cref="SqliteRunStore"/>) so every
/// method able to move a run's status away from <see cref="RunStatus.InProgress"/> acquires the
/// same per-run <see cref="RunActiveClaimGuard"/> claim that
/// <see cref="Runs.DurableToolApprovalGate"/>'s active-run check holds while persisting a
/// durable non-once approval scope or evaluating a run/session policy. This closes PR #972's
/// lifecycle races: on SQLite the active-run read and the policy operation cannot share one ACID
/// transaction with the run store, so a real mutual-exclusion claim -- not another racy pre-read
/// -- is required.
///
/// Conditional preview batches also hold this claim. Guarded methods include every transition
/// away from InProgress, terminal transitions from review/merging, and deletion.
/// Every other member is a pure pass-through; this store introduces no other behavior change.
/// </summary>
public sealed class RunActiveClaimGuardedRunStore(IRunStore inner, RunActiveClaimGuard guard) : IRunStore
{
    // SQLite's run and event databases are separate. Retain the existing lifecycle claim for the
    // entire conditional append, including any wait for the event database's write transaction.
    internal async Task<bool> TryWhileRunActiveAsync(
        RunId runId, Func<Task> append, CancellationToken ct)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        var run = await inner.GetAsync(runId, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (run is null || Endpoints.EndpointHelpers.IsTerminal(run.Status))
            return false;
        await append().ConfigureAwait(false);
        return true;
    }

    public Task InsertAsync(Run run, CancellationToken ct = default) =>
        inner.InsertAsync(run, ct);

    public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) =>
        inner.GetAsync(runId, ct);

    public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) =>
        inner.GetByStatusAsync(status, ct);

    public async Task UpdateStatusAsync(
        RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        await inner.UpdateStatusAsync(runId, status, endedAt, ct).ConfigureAwait(false);
    }

    public async Task UpdateResultAsync(
        RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        await inner.UpdateResultAsync(runId, status, result, endedAt, ct).ConfigureAwait(false);
    }

    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        await inner.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount, ct, now).ConfigureAwait(false);
    }

    public Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        inner.TryTransitionReviewToInProgressAsync(runId, ct, now);

    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        return await inner.TryTransitionReviewAsync(runId, toStatus, endedAt, result, reviewer, ct).ConfigureAwait(false);
    }

    public Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        inner.TryTransitionToCommittingAsync(runId, ct, now);

    public Task<bool> TryRevertCommittingAsync(
        RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        inner.TryRevertCommittingAsync(runId, treeHash, ct, now);

    public Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        inner.TryStartMergingAsync(runId, reviewer, ct, now);

    public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        inner.RevertMergingAsync(runId, ct, now);

    public async Task<bool> CompleteMergingAsync(
        RunId runId,
        RunStatus toStatus,
        DateTimeOffset endedAt,
        string? result,
        string? mergeConflicts = null,
        CancellationToken ct = default,
        string? mergedCommitHash = null)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        return await inner.CompleteMergingAsync(
            runId, toStatus, endedAt, result, mergeConflicts, ct, mergedCommitHash).ConfigureAwait(false);
    }

    public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) =>
        inner.UpdateTreeHashAfterCommitAsync(runId, newTreeHash, ct);

    public async Task<bool> SetAssembleReadyAsync(
        RunId runId,
        string treeHash,
        string worktreeBranch,
        string diff,
        int stepCount,
        DateTimeOffset endedAt,
        CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        return await inner
            .SetAssembleReadyAsync(runId, treeHash, worktreeBranch, diff, stepCount, endedAt, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        return await inner.TrySetTerminalStatusAsync(runId, toStatus, endedAt, result, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryTransitionToIdleAsync(RunId runId, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        return await inner.TryTransitionToIdleAsync(runId, ct).ConfigureAwait(false);
    }

    public Task<bool> TryWakeFromIdleAsync(RunId runId, CancellationToken ct = default) =>
        inner.TryWakeFromIdleAsync(runId, ct);

    public Task UpdateToInProgressAsync(
        RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) =>
        inner.UpdateToInProgressAsync(runId, worktreePath, worktreeBranch, startedAt, ct);

    public async Task DeleteAsync(RunId runId, CancellationToken ct = default)
    {
        await using var claim = await guard.AcquireAsync(runId, ct).ConfigureAwait(false);
        await inner.DeleteAsync(runId, ct).ConfigureAwait(false);
    }

    public Task UpdateWorktreeAsync(
        RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) =>
        inner.UpdateWorktreeAsync(runId, worktreePath, worktreeBranch, ct);

    public Task SetSandboxInfoAsync(
        RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) =>
        inner.SetSandboxInfoAsync(runId, backend, claimName, podName, @namespace, ct);

    public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) =>
        inner.ArchiveAsync(runId, archivedAt, ct);

    public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) =>
        inner.FindActiveChildAsync(parentRunId, subtaskId, ct);

    public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) =>
        inner.GetRunsByParentAsync(parentRunId, ct);

    public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(
        ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) =>
        inner.GetRunsByProjectAsync(projectId, includeChildren, ct);

    public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
        ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) =>
        inner.GetRunsByProjectAndStatusesAsync(projectId, statuses, ct);

    public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) =>
        inner.TryCreateProjectRunAsync(run, ct);

    public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) =>
        inner.GetByWorkflowRunIdAsync(workflowRunId, ct);

    public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) =>
        inner.UpdateWorkflowSelectionReasonAsync(runId, reason, ct);

    public Task UpdateModelSourceAsync(RunId runId, ModelSource modelSource, CancellationToken ct = default) =>
        inner.UpdateModelSourceAsync(runId, modelSource, ct);

    public Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default) =>
        inner.GetRunsBySubmittingUserAsync(submittingUser, agentName, limit, ct);
}
