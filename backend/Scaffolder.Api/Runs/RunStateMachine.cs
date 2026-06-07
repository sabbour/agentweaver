using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Enforces valid Run state transitions and emits lifecycle events.
/// Invalid transitions throw InvalidOperationException (mapped to 409 by middleware).
/// </summary>
public sealed class RunStateMachine
{
    private readonly IRunRepository _runRepository;
    private readonly EventLogService _eventLog;
    private readonly ILogger<RunStateMachine> _logger;

    public RunStateMachine(
        IRunRepository runRepository,
        EventLogService eventLog,
        ILogger<RunStateMachine> logger)
    {
        _runRepository = runRepository;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>
    /// Transitions a run from Queued to Running.
    /// Emits run.started event.
    /// </summary>
    public async Task<RunEntity> TransitionToRunningAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await GetAndValidateAsync(runId, RunStatus.Queued, RunStatus.Running, ct);
        run.StartedAt = DateTimeOffset.UtcNow;
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Running, ct);
        await _eventLog.AppendLifecycleEventAsync(
            runId, EventType.RunStarted,
            new { runId, status = "running", startedAt = updated.StartedAt }, ct);
        _logger.LogInformation("Run {RunId} transitioned to Running", runId);
        return updated;
    }

    /// <summary>
    /// Transitions a run from Running to Completed.
    /// Emits run.completed event.
    /// </summary>
    public async Task<RunEntity> TransitionToCompletedAsync(Guid runId, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Running, RunStatus.Completed, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Completed, ct);
        await _eventLog.AppendLifecycleEventAsync(
            runId, EventType.RunCompleted,
            new { runId, status = "completed", completedAt = DateTimeOffset.UtcNow }, ct);
        _logger.LogInformation("Run {RunId} transitioned to Completed", runId);
        return updated;
    }

    /// <summary>
    /// Transitions a run from Running to Failed with a reason.
    /// Emits run.failed event.
    /// </summary>
    public async Task<RunEntity> TransitionToFailedAsync(
        Guid runId,
        string failureReason,
        CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Running, RunStatus.Failed, ct);
        await _runRepository.UpdateFailureReasonAsync(runId, failureReason, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Failed, ct);
        await _eventLog.AppendLifecycleEventAsync(
            runId, EventType.RunFailed,
            new { runId, status = "failed", failureReason }, ct);
        _logger.LogInformation("Run {RunId} transitioned to Failed: {Reason}", runId, failureReason);
        return updated;
    }

    /// <summary>
    /// Transitions a run from Running to Bounded (step/duration limit reached).
    /// Emits run.bounded event.
    /// </summary>
    public async Task<RunEntity> TransitionToBoundedAsync(
        Guid runId,
        string boundReason,
        CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Running, RunStatus.Bounded, ct);
        await _runRepository.UpdateFailureReasonAsync(runId, boundReason, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Bounded, ct);
        await _eventLog.AppendLifecycleEventAsync(
            runId, EventType.RunBounded,
            new { runId, status = "bounded", boundReason }, ct);
        _logger.LogInformation("Run {RunId} transitioned to Bounded: {Reason}", runId, boundReason);
        return updated;
    }

    /// <summary>
    /// Transitions a completed run to AwaitingReview.
    /// Emits review.requested event.
    /// </summary>
    public async Task<RunEntity> TransitionToAwaitingReviewAsync(Guid runId, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Completed, RunStatus.AwaitingReview, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.AwaitingReview, ct);
        await _eventLog.AppendReviewEventAsync(
            runId, EventType.ReviewRequested,
            new { runId, status = "awaiting_review" }, ct);
        return updated;
    }

    /// <summary>
    /// Transitions AwaitingReview to Approved.
    /// Emits review.approved event.
    /// </summary>
    public async Task<RunEntity> TransitionToApprovedAsync(Guid runId, string reviewer, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.AwaitingReview, RunStatus.Approved, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Approved, ct);
        await _eventLog.AppendReviewEventAsync(
            runId, EventType.ReviewApproved,
            new { runId, reviewer }, ct);
        return updated;
    }

    /// <summary>
    /// Transitions AwaitingReview to Declined. Terminal state.
    /// Emits review.declined event.
    /// </summary>
    public async Task<RunEntity> TransitionToDeclinedAsync(Guid runId, string reviewer, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.AwaitingReview, RunStatus.Declined, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Declined, ct);
        await _eventLog.AppendReviewEventAsync(
            runId, EventType.ReviewDeclined,
            new { runId, reviewer }, ct);
        return updated;
    }

    /// <summary>
    /// Transitions Approved to Merged. Terminal state.
    /// Emits merge.completed event.
    /// </summary>
    public async Task<RunEntity> TransitionToMergedAsync(Guid runId, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Approved, RunStatus.Merged, ct);
        var updated = await _runRepository.UpdateStatusAsync(runId, RunStatus.Merged, ct);
        await _eventLog.AppendReviewEventAsync(
            runId, EventType.MergeCompleted,
            new { runId, status = "merged" }, ct);
        return updated;
    }

    /// <summary>
    /// Transitions Approved to MergeConflict. Terminal state.
    /// </summary>
    public async Task<RunEntity> TransitionToMergeConflictAsync(Guid runId, CancellationToken ct = default)
    {
        await GetAndValidateAsync(runId, RunStatus.Approved, RunStatus.MergeConflict, ct);
        return await _runRepository.UpdateStatusAsync(runId, RunStatus.MergeConflict, ct);
    }

    private async Task<RunEntity> GetAndValidateAsync(
        Guid runId,
        RunStatus expectedCurrent,
        RunStatus targetStatus,
        CancellationToken ct)
    {
        var run = await _runRepository.GetByIdAsync(runId, ct)
            ?? throw new KeyNotFoundException($"Run {runId} not found.");

        if (run.Status != expectedCurrent)
        {
            throw new InvalidOperationException(
                $"Run {runId} cannot transition to {targetStatus}: " +
                $"expected status {expectedCurrent} but found {run.Status}.");
        }

        return run;
    }
}
