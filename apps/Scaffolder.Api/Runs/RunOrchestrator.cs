using System.Collections.Concurrent;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Streaming;
using Scaffolder.Domain;
using Scaffolder.Domain.Payloads;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Coordinates the full run lifecycle: persistence, worktree provisioning, the
/// agent loop, terminal events, the human-approval gate, and the merge back to
/// the originating branch. All governance (sandboxed worktree, bounded run,
/// human approval before merge, complete audit trail) is enforced here at the
/// backend, not in any client (Principles III, X, XI).
/// </summary>
public sealed class RunOrchestrator
{
    private readonly SqliteRunStore _runStore;
    private readonly SqliteEventStore _eventStore;
    private readonly IOperationalStore _operationalStore;
    private readonly RunEventEmitter _emitter;
    private readonly RunEventBroadcaster _broadcaster;
    private readonly WorktreeManager _worktree;
    private readonly IAgentRunner _agentRunner;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly CancellationToken _appStopping;

    private readonly ConcurrentDictionary<RunId, SemaphoreSlim> _reviewLocks = new();

    public RunOrchestrator(
        SqliteRunStore runStore,
        SqliteEventStore eventStore,
        IOperationalStore operationalStore,
        RunEventEmitter emitter,
        RunEventBroadcaster broadcaster,
        WorktreeManager worktree,
        IAgentRunner agentRunner,
        IHostApplicationLifetime lifetime,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _eventStore = eventStore;
        _operationalStore = operationalStore;
        _emitter = emitter;
        _broadcaster = broadcaster;
        _worktree = worktree;
        _agentRunner = agentRunner;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    private static IReadOnlyList<string> PolicyDecisions(Run run) => new[]
    {
        $"model-source-validated:{run.ModelSource.ToApiString()}",
        "sandbox-boundary:worktree-enforced",
        "run-bounds:steps-and-duration-enforced",
        "human-approval-gate:required-before-merge"
    };

    /// <summary>
    /// Persists the run, provisions its worktree, emits run.started, and launches
    /// the agent loop on a background task. Setup failures throw so the caller can
    /// surface them; the agent loop and terminal handling run in the background.
    /// </summary>
    public async Task StartRunAsync(Run run, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var persisted = run with { Status = RunStatus.InProgress, StartedAt = startedAt };

        await _runStore.InsertAsync(persisted, ct).ConfigureAwait(false);
        await _operationalStore.CreateAsync(
            persisted.Id,
            persisted.SubmittingUser,
            persisted.ModelSource,
            startedAt,
            PolicyDecisions(persisted),
            ct).ConfigureAwait(false);

        Run runWithWorktree;
        try
        {
            var worktree = _worktree.AddWorktree(persisted.RepositoryPath, persisted.OriginatingBranch, persisted.Id);
            await _runStore.UpdateWorktreeAsync(persisted.Id, worktree.WorktreePath, worktree.BranchName, ct)
                .ConfigureAwait(false);

            runWithWorktree = persisted with
            {
                WorktreePath = worktree.WorktreePath,
                WorktreeBranch = worktree.BranchName
            };

            await _emitter.EmitAsync(
                runWithWorktree.Id,
                EventType.RunStarted,
                new RunStartedPayload
                {
                    SubmittingUser = runWithWorktree.SubmittingUser,
                    ModelSource = runWithWorktree.ModelSource,
                    RepositoryPath = runWithWorktree.RepositoryPath,
                    OriginatingBranch = runWithWorktree.OriginatingBranch
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Setup failed (for example an invalid repository or branch). Finalize the
            // run as failed so it does not stay stranded, then rethrow for the caller.
            await FinalizeFailedRunAsync(persisted, ex.Message).ConfigureAwait(false);
            throw;
        }

        _ = Task.Run(() => RunAgentLoopAsync(runWithWorktree), _appStopping);
    }

    private async Task RunAgentLoopAsync(Run run)
    {
        var ct = _appStopping;
        try
        {
            await _agentRunner.ExecuteAsync(run, _broadcaster, _eventStore, ct).ConfigureAwait(false);
            await FinalizeAfterRunReturnAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinalizeFailedRunAsync(run, "cancelled").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBoundsExceeded(ex))
        {
            await FinalizeBoundedRunAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent loop failed for run {RunId}", run.Id);
            await FinalizeFailedRunAsync(run, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a run loop that returned without throwing. The agent runtime emits
    /// its own terminal lifecycle event; this inspects it. A run.completed (or no
    /// terminal event from a runner that does not emit one) means the run produced
    /// a diff and moves to the review gate. A run.bounded or run.failed terminal
    /// event is reflected in the run status and operational record without
    /// re-emitting the event.
    /// </summary>
    private async Task FinalizeAfterRunReturnAsync(Run run, CancellationToken ct)
    {
        var terminal = await _eventStore.GetLatestTerminalEventAsync(run.Id, ct).ConfigureAwait(false);
        switch (terminal?.Type)
        {
            case EventType.RunBounded:
                await SetTerminalStatusAsync(run, RunStatus.Bounded, "bounded").ConfigureAwait(false);
                break;
            case EventType.RunFailed:
                await SetTerminalStatusAsync(run, RunStatus.Failed, "failed").ConfigureAwait(false);
                break;
            default:
                await FinalizeCompletedRunAsync(run, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SetTerminalStatusAsync(Run run, RunStatus status, string outcome)
    {
        var ct = CancellationToken.None;
        var stepCount = await _eventStore.CountEventsOfTypeAsync(run.Id, EventType.ToolCall, ct).ConfigureAwait(false);
        await _runStore.UpdateStepCountAsync(run.Id, stepCount, ct).ConfigureAwait(false);

        var endedAt = DateTimeOffset.UtcNow;
        await _runStore.UpdateStatusAsync(run.Id, status, endedAt, ct).ConfigureAwait(false);
        await _operationalStore.CompleteAsync(run.Id, endedAt, stepCount, outcome, PolicyDecisions(run), ct)
            .ConfigureAwait(false);

        _broadcaster.Complete(run.Id);
    }

    private async Task FinalizeCompletedRunAsync(Run run, CancellationToken ct)
    {
        var treeHash = _worktree.CommitChanges(run.WorktreePath!, run.Id);
        await _runStore.UpdateCommittedTreeHashAsync(run.Id, treeHash, ct).ConfigureAwait(false);

        var stepCount = await _eventStore.CountEventsOfTypeAsync(run.Id, EventType.ToolCall, ct).ConfigureAwait(false);
        await _runStore.UpdateStepCountAsync(run.Id, stepCount, ct).ConfigureAwait(false);

        await _emitter.EmitAsync(
            run.Id,
            EventType.ReviewRequested,
            new ReviewRequestedPayload { TreeHash = treeHash },
            ct: ct).ConfigureAwait(false);

        var endedAt = DateTimeOffset.UtcNow;
        await _runStore.UpdateStatusAsync(run.Id, RunStatus.Completed, endedAt, ct).ConfigureAwait(false);
        await _operationalStore.CompleteAsync(run.Id, endedAt, stepCount, "completed", PolicyDecisions(run), ct)
            .ConfigureAwait(false);
    }

    private async Task FinalizeFailedRunAsync(Run run, string reason)
    {
        var ct = CancellationToken.None;
        var stepCount = await _eventStore.CountEventsOfTypeAsync(run.Id, EventType.ToolCall, ct).ConfigureAwait(false);

        // The agent runner may have already emitted a terminal event (e.g. content-safety failure
        // or explicit cancellation). Only emit run.failed when no terminal event was recorded yet
        // to avoid duplicate terminal events in the append-only audit log.
        var existing = await _eventStore.GetLatestTerminalEventAsync(run.Id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await _emitter.EmitAsync(run.Id, EventType.RunFailed, new RunFailedPayload { Reason = reason }, ct: ct)
                .ConfigureAwait(false);
        }

        var endedAt = DateTimeOffset.UtcNow;
        await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, endedAt, ct).ConfigureAwait(false);
        await _operationalStore.CompleteAsync(run.Id, endedAt, stepCount, "failed", PolicyDecisions(run), ct)
            .ConfigureAwait(false);

        _broadcaster.Complete(run.Id);
    }

    private async Task FinalizeBoundedRunAsync(Run run)
    {
        var ct = CancellationToken.None;
        var stepCount = await _eventStore.CountEventsOfTypeAsync(run.Id, EventType.ToolCall, ct).ConfigureAwait(false);

        await _emitter.EmitAsync(
            run.Id,
            EventType.RunBounded,
            new RunBoundedPayload { LimitType = "step-count", StepCount = stepCount },
            ct: ct).ConfigureAwait(false);

        var endedAt = DateTimeOffset.UtcNow;
        await _runStore.UpdateStatusAsync(run.Id, RunStatus.Bounded, endedAt, ct).ConfigureAwait(false);
        await _operationalStore.CompleteAsync(run.Id, endedAt, stepCount, "bounded", PolicyDecisions(run), ct)
            .ConfigureAwait(false);

        _broadcaster.Complete(run.Id);
    }

    /// <summary>
    /// At startup, fails any run still marked InProgress from a previous process,
    /// so no run is left in a non-terminal state across a restart (Principle X).
    /// </summary>
    public async Task RestartRecoveryAsync(CancellationToken ct)
    {
        var stranded = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in stranded)
        {
            _logger.LogWarning("Failing run {RunId} stranded InProgress across a process restart", run.Id);
            await FinalizeFailedRunAsync(run, "process-restart").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies a human review decision. Serialized per run; a decision is rejected
    /// if the run is not awaiting review or if a decision was already recorded
    /// (the human-approval gate, FR-016, Principle X). On approval the merge is
    /// attempted against the approved tree hash.
    /// </summary>
    public async Task<ReviewResult> SubmitReviewAsync(
        RunId runId,
        bool approved,
        string reviewerIdentity,
        CancellationToken ct)
    {
        var gate = _reviewLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ReviewResult.Rejected("Run not found.");
            }

            if (run.Status != RunStatus.Completed)
            {
                return ReviewResult.Rejected("Run is not awaiting review.");
            }

            if (!await _eventStore.HasEventOfTypeAsync(runId, EventType.ReviewRequested, ct).ConfigureAwait(false))
            {
                return ReviewResult.Rejected("Run has not requested review.");
            }

            if (await _eventStore.HasEventOfTypeAsync(runId, EventType.ReviewApproved, ct).ConfigureAwait(false) ||
                await _eventStore.HasEventOfTypeAsync(runId, EventType.ReviewDeclined, ct).ConfigureAwait(false))
            {
                return ReviewResult.Rejected("A review decision was already recorded for this run.");
            }

            return approved
                ? await ApproveAsync(run, reviewerIdentity, ct).ConfigureAwait(false)
                : await DeclineAsync(run, reviewerIdentity, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ReviewResult> ApproveAsync(Run run, string reviewerIdentity, CancellationToken ct)
    {
        var treeHash = run.CommittedTreeHash
            ?? throw new InvalidOperationException("Approved run has no committed tree hash.");

        await _emitter.EmitAsync(
            run.Id,
            EventType.ReviewApproved,
            new ReviewApprovedPayload { TreeHash = treeHash, ApprovedBy = reviewerIdentity },
            ct: ct).ConfigureAwait(false);

        var merge = _worktree.MergeWorktree(
            run.RepositoryPath,
            run.OriginatingBranch,
            run.WorktreeBranch!,
            treeHash);

        if (merge.Success)
        {
            await _emitter.EmitAsync(
                run.Id,
                EventType.MergeCompleted,
                new MergeCompletedPayload { MergedCommitHash = merge.MergedCommitHash! },
                ct: ct).ConfigureAwait(false);

            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Approved, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            _broadcaster.Complete(run.Id);
            return new ReviewResult
            {
                Outcome = ReviewDecisionOutcome.Approved,
                Status = RunStatus.Approved,
                MergeResult = $"merged:{merge.MergedCommitHash}"
            };
        }

        await _emitter.EmitAsync(
            run.Id,
            EventType.MergeFailed,
            new MergeFailedPayload { Reason = merge.ConflictDetails! },
            ct: ct).ConfigureAwait(false);

        // The run stays Approved (decision recorded) but the originating branch is untouched
        // and the worktree is preserved for inspection (FR-016).
        await _runStore.UpdateStatusAsync(run.Id, RunStatus.Approved, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        _broadcaster.Complete(run.Id);
        return new ReviewResult
        {
            Outcome = ReviewDecisionOutcome.Approved,
            Status = RunStatus.Approved,
            MergeResult = $"conflict:{merge.ConflictDetails}"
        };
    }

    private async Task<ReviewResult> DeclineAsync(Run run, string reviewerIdentity, CancellationToken ct)
    {
        await _emitter.EmitAsync(
            run.Id,
            EventType.ReviewDeclined,
            new ReviewDeclinedPayload { DeclinedBy = reviewerIdentity },
            ct: ct).ConfigureAwait(false);

        await _runStore.UpdateStatusAsync(run.Id, RunStatus.Declined, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        _broadcaster.Complete(run.Id);
        return new ReviewResult
        {
            Outcome = ReviewDecisionOutcome.Declined,
            Status = RunStatus.Declined,
            MergeResult = null
        };
    }

    // Forward-compatible seam: the agent runtime signals a bounds breach by throwing an
    // exception whose type name is RunBoundsExceededException. Until that runtime is wired
    // (Wave 2), only the placeholder runner runs, which throws NotImplementedException and is
    // handled as a failure.
    private static bool IsBoundsExceeded(Exception ex) =>
        ex.GetType().Name == "RunBoundsExceededException";
}
