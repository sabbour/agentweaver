using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Defers a run's terminal transition while a preview publication holds the run's lease (#1315).
///
/// Publishing a preview can spend up to the configured Gateway-convergence window before the HTTP health probe.
/// Its final <c>sandbox.preview_ready</c> batch commits only while the run row is still active. An
/// agent that finishes its work inside that window therefore cancels its own preview, and the
/// publication path tears the preview process down as <c>preview_not_published</c>.
///
/// This decorator keeps the run row active for the duration instead. Every transition that can make
/// a run terminal first waits for the lease to be released or to expire, so the publication commits
/// and the ordering invariant — <c>preview_ready</c> before the terminal event — still holds.
///
/// It deliberately sits OUTSIDE <see cref="RunActiveClaimGuardedRunStore"/>. That store's claim is
/// also taken by the conditional preview append, so waiting while holding it would deadlock the
/// very publication this decorator is waiting for.
///
/// The lease carries its own short expiry, so a replica that crashes mid-publication cannot park a
/// run indefinitely. Active publication renews it; explicit cancellation clears it before making
/// the run terminal.
/// </summary>
public sealed class PreviewPublicationLeaseRunStore(
    IRunStore inner,
    ILogger<PreviewPublicationLeaseRunStore>? logger = null) : IRunStore, IRunStoreDecorator
{
    /// <summary>
    /// How long a publication site claims the lease for. The lease is renewed during active
    /// convergence polling, while this short window remains the crash-recovery backstop.
    /// </summary>
    public static readonly TimeSpan PublicationLeaseWindow = TimeSpan.FromMinutes(3);

    /// <summary>How often active convergence polling extends the short publication lease.</summary>
    public static readonly TimeSpan PublicationLeaseRenewalInterval = TimeSpan.FromMinutes(1);

    /// <summary>Default interval at which the lease is re-read while a transition is deferred.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = TimeProvider.System;
    private readonly TimeSpan _pollInterval = DefaultPollInterval;

    public IRunStore Inner { get; } = inner;

    /// <summary>Test constructor: shrinks the wait so a deferral test does not take minutes.</summary>
    internal PreviewPublicationLeaseRunStore(
        IRunStore inner,
        TimeSpan pollInterval,
        ILogger<PreviewPublicationLeaseRunStore>? logger = null)
        : this(inner, logger)
    {
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// Blocks until no preview publication is in flight for <paramref name="runId"/>. Returns at
    /// once in the common case where no lease is held.
    /// </summary>
    private async Task AwaitPreviewPublicationAsync(RunId runId, CancellationToken ct)
    {
        var deferred = false;
        while (true)
        {
            var leaseUntil = await Inner.GetPreviewPublicationLeaseAsync(runId, ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (leaseUntil is null || leaseUntil <= now)
                break;
            if (!deferred)
            {
                deferred = true;
                logger?.LogInformation(
                    "Deferring terminal transition for run {RunId} until preview publication completes (lease until {LeaseUntil})",
                    runId.ToString(), leaseUntil);
            }

            var remaining = leaseUntil.Value - now;
            var wait = remaining < _pollInterval ? remaining : _pollInterval;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    // ---- Transitions that can make a run terminal: defer for an in-flight publication. ----

    public async Task UpdateStatusAsync(
        RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
    {
        if (Endpoints.EndpointHelpers.IsTerminal(status) || status == RunStatus.AssembleReady)
            await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        await Inner.UpdateStatusAsync(runId, status, endedAt, ct).ConfigureAwait(false);
    }

    public async Task UpdateResultAsync(
        RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        if (Endpoints.EndpointHelpers.IsTerminal(status) || status == RunStatus.AssembleReady)
            await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        await Inner.UpdateResultAsync(runId, status, result, endedAt, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null,
        CancellationToken ct = default)
    {
        if (Endpoints.EndpointHelpers.IsTerminal(toStatus))
            await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        return await Inner.TryTransitionReviewAsync(runId, toStatus, endedAt, result, reviewer, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> CompleteMergingAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null,
        CancellationToken ct = default, string? mergedCommitHash = null)
    {
        if (Endpoints.EndpointHelpers.IsTerminal(toStatus))
            await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        return await Inner
            .CompleteMergingAsync(runId, toStatus, endedAt, result, mergeConflicts, ct, mergedCommitHash)
            .ConfigureAwait(false);
    }

    public async Task<bool> SetAssembleReadyAsync(
        RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt,
        CancellationToken ct = default)
    {
        await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        return await Inner
            .SetAssembleReadyAsync(runId, treeHash, worktreeBranch, diff, stepCount, endedAt, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        await AwaitPreviewPublicationAsync(runId, ct).ConfigureAwait(false);
        return await Inner.TrySetTerminalStatusAsync(runId, toStatus, endedAt, result, ct).ConfigureAwait(false);
    }

    // ---- Lease management: never defers, or publication could not claim its own lease. ----

    public Task<bool> TryBeginPreviewPublicationAsync(
        RunId runId, DateTimeOffset leaseUntil, CancellationToken ct = default) =>
        Inner.TryBeginPreviewPublicationAsync(runId, leaseUntil, ct);

    public Task<bool> TryAcquirePreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default) =>
        Inner.TryAcquirePreviewPublicationAsync(runId, ownerId, leaseUntil, ct);

    public Task<bool> TryRenewPreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default) =>
        Inner.TryRenewPreviewPublicationAsync(runId, ownerId, leaseUntil, ct);

    public Task EndPreviewPublicationAsync(RunId runId, CancellationToken ct = default) =>
        Inner.EndPreviewPublicationAsync(runId, ct);

    public Task EndPreviewPublicationAsync(RunId runId, string ownerId, CancellationToken ct = default) =>
        Inner.EndPreviewPublicationAsync(runId, ownerId, ct);

    public Task<bool> IsPreviewPublicationOwnerAsync(
        RunId runId, string ownerId, CancellationToken ct = default) =>
        Inner.IsPreviewPublicationOwnerAsync(runId, ownerId, ct);

    public Task<DateTimeOffset?> GetPreviewPublicationLeaseAsync(RunId runId, CancellationToken ct = default) =>
        Inner.GetPreviewPublicationLeaseAsync(runId, ct);

    // ---- Everything else is a pure pass-through. ----

    public Task InsertAsync(Run run, CancellationToken ct = default) => Inner.InsertAsync(run, ct);

    public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => Inner.GetAsync(runId, ct);

    public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) =>
        Inner.GetByStatusAsync(status, ct);

    public Task UpdateAssemblyArtifactsAsync(
        RunId runId, string treeHash, string diff, CancellationToken ct = default) =>
        Inner.UpdateAssemblyArtifactsAsync(runId, treeHash, diff, ct);

    public Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default,
        DateTimeOffset? now = null) =>
        Inner.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount, ct, now);

    public Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        Inner.TryTransitionReviewToInProgressAsync(runId, ct, now);

    public Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        Inner.TryTransitionToCommittingAsync(runId, ct, now);

    public Task<bool> TryRevertCommittingAsync(
        RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        Inner.TryRevertCommittingAsync(runId, treeHash, ct, now);

    public Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        Inner.TryStartMergingAsync(runId, reviewer, ct, now);

    public Task<bool> RevertMergingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        Inner.RevertMergingAsync(runId, ct, now);

    public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) =>
        Inner.UpdateTreeHashAfterCommitAsync(runId, newTreeHash, ct);

    public Task<bool> TryTransitionToIdleAsync(RunId runId, CancellationToken ct = default) =>
        Inner.TryTransitionToIdleAsync(runId, ct);

    public Task<bool> TryWakeFromIdleAsync(RunId runId, CancellationToken ct = default) =>
        Inner.TryWakeFromIdleAsync(runId, ct);

    public Task UpdateToInProgressAsync(
        RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt,
        CancellationToken ct = default) =>
        Inner.UpdateToInProgressAsync(runId, worktreePath, worktreeBranch, startedAt, ct);

    public Task DeleteAsync(RunId runId, CancellationToken ct = default) => Inner.DeleteAsync(runId, ct);

    public Task UpdateWorktreeAsync(
        RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) =>
        Inner.UpdateWorktreeAsync(runId, worktreePath, worktreeBranch, ct);

    public Task SetSandboxInfoAsync(
        RunId runId, string? backend, string? claimName, string? podName, string? @namespace,
        CancellationToken ct = default) =>
        Inner.SetSandboxInfoAsync(runId, backend, claimName, podName, @namespace, ct);

    public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) =>
        Inner.ArchiveAsync(runId, archivedAt, ct);

    public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) =>
        Inner.FindActiveChildAsync(parentRunId, subtaskId, ct);

    public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) =>
        Inner.GetRunsByParentAsync(parentRunId, ct);

    public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(
        ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) =>
        Inner.GetRunsByProjectAsync(projectId, includeChildren, ct);

    public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
        ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) =>
        Inner.GetRunsByProjectAndStatusesAsync(projectId, statuses, ct);

    public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) =>
        Inner.TryCreateProjectRunAsync(run, ct);

    public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) =>
        Inner.GetByWorkflowRunIdAsync(workflowRunId, ct);

    public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) =>
        Inner.UpdateWorkflowSelectionReasonAsync(runId, reason, ct);

    public Task UpdateModelSourceAsync(RunId runId, ModelSource modelSource, CancellationToken ct = default) =>
        Inner.UpdateModelSourceAsync(runId, modelSource, ct);

    public Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default) =>
        Inner.GetRunsBySubmittingUserAsync(submittingUser, agentName, limit, ct);
}
