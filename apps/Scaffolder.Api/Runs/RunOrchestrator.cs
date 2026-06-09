using System.Threading.Channels;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Coordinates the run lifecycle: worktree creation, persistence, and the agent turn.
/// After a successful agent turn the run transitions to AwaitingReview; the stream
/// remains open until the review decision is processed by the review endpoint.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly SqliteRunStore _runStore;
    private readonly IAgentRunner _agentRunner;
    private readonly RunStreamStore _streamStore;
    private readonly WorktreeManager _worktreeManager;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly CancellationToken _appStopping;

    public RunOrchestrator(
        SqliteRunStore runStore,
        IAgentRunner agentRunner,
        RunStreamStore streamStore,
        WorktreeManager worktreeManager,
        IHostApplicationLifetime lifetime,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _agentRunner = agentRunner;
        _streamStore = streamStore;
        _worktreeManager = worktreeManager;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    public async Task StartRunAsync(Run run, CancellationToken ct)
    {
        // Create an isolated worktree so the agent never writes to the originating
        // branch directly (FR-003). The run is persisted with the worktree coordinates.
        WorktreeInfo worktreeInfo;
        try
        {
            worktreeInfo = _worktreeManager.AddWorktree(run.RepositoryPath, run.OriginatingBranch, run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree for run {RunId}", run.Id);
            throw;
        }

        var started = run with
        {
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktreeInfo.WorktreePath,
            WorktreeBranch = worktreeInfo.BranchName,
        };

        await _runStore.InsertAsync(started, ct).ConfigureAwait(false);
        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
        _ = Task.Run(() => RunTurnAsync(started, entry, worktreeInfo), _appStopping);
    }

    private async Task RunTurnAsync(Run run, RunStreamEntry entry, WorktreeInfo worktreeInfo)
    {
        var recordingWriter = new RecordingChannelWriter(entry);
        var ct = _appStopping;
        try
        {
            // Run the agent inside the worktree (FR-003). Sequences are assigned
            // by the agent runner's internal counter during ExecuteAsync.
            await _agentRunner.ExecuteAsync(
                run.Task, worktreeInfo.WorktreePath, run.ModelSource,
                run.Id.ToString(), recordingWriter, ct).ConfigureAwait(false);

            // All agent Record() calls have completed by this point (A2).
            // Commit changes and compute the diff from the originating branch.
            string treeHash;
            try
            {
                treeHash = _worktreeManager.CommitChanges(worktreeInfo.WorktreePath, run.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit worktree changes for run {RunId}", run.Id);
                await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                var failSeq = entry.NextSequence();
                entry.Record(new RunEvent(failSeq, EventTypes.RunFailed, new { reason = "commit_failed" }));
                _streamStore.Complete(run.Id.ToString());
                return;
            }

            string diff;
            try
            {
                diff = _worktreeManager.GetDiff(run.RepositoryPath, run.OriginatingBranch, worktreeInfo.BranchName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute diff for run {RunId}; continuing with empty diff", run.Id);
                diff = string.Empty;
            }

            // Count tool.call events as the step count for display (stored at commit time).
            var stepCount = entry.GetSnapshotSince(0).Events.Count(e => e.Type == EventTypes.ToolCall);

            await _runStore.UpdateReviewReadyAsync(run.Id, treeHash, diff, stepCount, CancellationToken.None).ConfigureAwait(false);

            // Mark the entry as awaiting review BEFORE emitting events so the stale
            // sweep cannot evict the entry while a human decision is pending (A1).
            entry.MarkAwaitingReview();

            // Emit review.requested using NextSequence (A2: after ExecuteAsync has returned).
            var reviewSeq = entry.NextSequence();
            entry.Record(new RunEvent(reviewSeq, EventTypes.ReviewRequested, new { tree_hash = treeHash }));

            // Do NOT call _streamStore.Complete — the stream stays open for review/merge events.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent turn failed for run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            var failSeq = entry.NextSequence();
            entry.Record(new RunEvent(failSeq, EventTypes.RunFailed, new { reason = "agent_error" }));
            _streamStore.Complete(run.Id.ToString());
        }
    }

    /// <summary>
    /// On process restart, fails any run still InProgress (cannot be resumed),
    /// reverts any run interrupted in Merging back to AwaitingReview (merge did
    /// not complete; worktree is intact), and re-creates in-memory stream entries
    /// for all AwaitingReview runs so the review endpoint can still emit events
    /// to live SSE clients (A3 / MF3).
    /// </summary>
    public async Task RestartRecoveryAsync(CancellationToken ct)
    {
        var stranded = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in stranded)
        {
            _logger.LogWarning("Failing stranded run {RunId}", run.Id);
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }

        // Revert any run interrupted mid-merge. The DB CAS set Merging before calling
        // MergeWorktree; if the process died during or after the merge but before the
        // Merging→terminal transition, we revert to AwaitingReview. The worktree branch
        // is preserved so the run remains re-approvable.
        var merging = await _runStore.GetByStatusAsync(RunStatus.Merging, ct).ConfigureAwait(false);
        foreach (var run in merging)
        {
            _logger.LogWarning(
                "Reverting interrupted merge for run {RunId} back to awaiting_review", run.Id);
            await _runStore.RevertMergingAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }

        // Re-create stream entries for runs awaiting review (including those just reverted
        // from Merging). Historical agent events are not re-emitted (not persisted), but
        // review/merge events will flow to any client that reconnects after the restart (A3).
        var awaitingReview = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);
        foreach (var run in awaitingReview)
        {
            var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
            entry.MarkAwaitingReview();
        }
    }
}

/// <summary>
/// Adapts a <see cref="RunStreamEntry"/> into a <see cref="ChannelWriter{T}"/> for
/// the agent runner. Events are recorded directly into the entry's history list;
/// there is no separate channel — clients poll the history via
/// <see cref="RunStreamEntry.GetSnapshotSince"/>.
/// </summary>
file sealed class RecordingChannelWriter(RunStreamEntry entry) : ChannelWriter<RunEvent>
{
    public override bool TryWrite(RunEvent item)
    {
        entry.Record(item);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public override bool TryComplete(Exception? error = null) => true;
}
