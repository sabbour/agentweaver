using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

using RunStatus = Scaffolder.Domain.RunStatus;
using WfRunStatus = Microsoft.Agents.AI.Workflows.RunStatus;

namespace Scaffolder.Api.Runs;

/// <summary>
/// On process startup, recovers runs that were active when the process died.
/// Uses MAF checkpoints to resume workflow runs at the review gate without
/// re-executing the agent turn. Replaces RunOrchestrator.RestartRecoveryAsync.
/// </summary>
public sealed class WorkflowRestartService
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly RunWorkflowFactory _factory;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly RunWatchLoopService _watchLoop;
    private readonly ILogger<WorkflowRestartService> _logger;

    public WorkflowRestartService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        RunWorkflowFactory factory,
        IWorktreeOperations worktreeOps,
        RunWatchLoopService watchLoop,
        ILogger<WorkflowRestartService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _watchLoop = watchLoop;
        _logger = logger;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        // 1. Fail stranded InProgress runs (agent was mid-execution; non-replayable).
        var inProgress = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in inProgress)
        {
            _logger.LogWarning("Failing stranded InProgress run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }

        // 2. Revert Merging -> AwaitingReview (merge did not complete).
        var merging = await _runStore.GetByStatusAsync(RunStatus.Merging, ct).ConfigureAwait(false);
        foreach (var run in merging)
        {
            _logger.LogWarning("Reverting interrupted merge for run {RunId} back to awaiting_review", run.Id);
            await _runStore.RevertMergingAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }

        // 3. Resume AwaitingReview runs from checkpoint.
        var awaiting = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);
        foreach (var run in awaiting)
        {
            var runIdStr = run.Id.ToString();
            var entry = _streamStore.Create(runIdStr, run.SubmittingUser);
            entry.MarkAwaitingReview();

            var checkpointInfo = _factory.GetLatestCheckpoint(runIdStr);
            if (checkpointInfo is null)
            {
                // No checkpoint — cannot resume the workflow, but the run is still
                // in AwaitingReview in the DB. Create the stream entry so the review
                // endpoint can still emit events to SSE clients.
                _logger.LogWarning("No checkpoint found for AwaitingReview run {RunId}; stream entry re-created only", run.Id);
                continue;
            }

            // Guardrail 1: Validate worktree before resuming.
            if (run.WorktreePath is null || !_worktreeOps.WorktreeExists(run.WorktreePath))
            {
                _logger.LogError("Worktree missing for run {RunId} at {Path}; failing run", run.Id, run.WorktreePath);
                await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                _streamStore.Complete(runIdStr);
                continue;
            }

            if (run.TreeHash is not null)
            {
                var currentTreeHash = _worktreeOps.GetTreeHash(run.WorktreePath);
                if (currentTreeHash is not null && !string.Equals(currentTreeHash, run.TreeHash, StringComparison.Ordinal))
                {
                    _logger.LogError("Worktree tree hash mismatch for run {RunId}: expected={Expected} actual={Actual}; failing run",
                        run.Id, run.TreeHash, currentTreeHash);
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }
            }

            try
            {
                var streamingRun = await _factory.ResumeAsync(checkpointInfo, ct).ConfigureAwait(false);
                _registry.Register(runIdStr, streamingRun);

                // Re-populate PendingRequestStore from the resumed run's status.
                var status = await streamingRun.GetStatusAsync(ct).ConfigureAwait(false);
                if (status == WfRunStatus.PendingRequests)
                {
                    // The workflow is paused at the request port. We need to extract the
                    // pending request. Watch the stream briefly for a RequestInfoEvent.
                    // The resumed run should immediately emit it.
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await foreach (var evt in streamingRun.WatchStreamAsync(cts.Token))
                        {
                            if (evt is RequestInfoEvent rie)
                            {
                                _pendingStore.Set(runIdStr, rie.Request, run.SubmittingUser);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* timeout is acceptable */ }
                }

                // Start the supervised watch loop.
                _watchLoop.StartWatching(runIdStr, streamingRun, entry, run.SubmittingUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume workflow for run {RunId}; failing run", run.Id);
                await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                _streamStore.Complete(runIdStr);
            }
        }
    }
}
