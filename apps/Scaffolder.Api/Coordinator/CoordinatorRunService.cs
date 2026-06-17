using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;

using Run = Scaffolder.Domain.Run;
using RunStatus = Scaffolder.Domain.RunStatus;

namespace Scaffolder.Api.Coordinator;

/// <summary>
/// Phase 1 coordinator seam. This is the single service Tank's HTTP endpoints
/// (<c>POST /orchestrations</c>, <c>GET/confirm/revise outcome-spec</c>) call — there is no
/// orchestration logic inline in HTTP handlers (Principle III).
///
/// Lifecycle (Phase 1 only):
/// <list type="number">
/// <item><see cref="StartCoordinatorRunAsync"/> creates the coordinator (parent) run, starts the
/// coordinator workflow, and supervises it. The workflow drafts + persists an outcome spec,
/// emits <c>coordinator.outcome_spec</c>, and SUSPENDS at the await-confirmation request port.</item>
/// <item><see cref="ConfirmOutcomeSpecAsync"/> resumes the suspended run: the spec advances to
/// <c>confirmed</c> and the run terminates (no dispatch — that is Phase 2).</item>
/// <item><see cref="ReviseOutcomeSpecAsync"/> resumes the suspended run: the coordinator re-drafts
/// the spec and re-suspends at the gate.</item>
/// </list>
///
/// The suspend/resume mechanism mirrors the existing review-gate exactly: a MAF
/// <see cref="RequestPort"/> emits a <see cref="RequestInfoEvent"/> captured by the watch loop into
/// <see cref="PendingRequestStore"/>; the resume seam looks the run up in
/// <see cref="RunWorkflowRegistry"/>, atomically consumes the pending request, and calls
/// <c>SendResponseAsync</c> — identical to the review endpoint in <c>Program.cs</c>.
/// </summary>
public sealed class CoordinatorRunService
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly CoordinatorWorkflowFactory _factory;
    private readonly RunWorkflowFactory _runWorkflowFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoordinatorRunService> _logger;
    private readonly CancellationToken _appStopping;

    public CoordinatorRunService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        CoordinatorWorkflowFactory factory,
        RunWorkflowFactory runWorkflowFactory,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorRunService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _runWorkflowFactory = runWorkflowFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    /// <summary>
    /// Creates and starts a coordinator run for <paramref name="goal"/>. The returned run has
    /// <c>ParentRunId == null</c> and <c>AgentName == "Coordinator"</c>. The run drafts a
    /// confirmable outcome spec and blocks at the await-confirmation gate before returning any
    /// dispatch (there is none in Phase 1).
    /// </summary>
    public async Task<RunId> StartCoordinatorRunAsync(
        ProjectId projectId,
        string goal,
        string submittingUser,
        string repositoryPath,
        string originatingBranch,
        string? modelId,
        CancellationToken ct)
    {
        var runId = RunId.New();
        var now = DateTimeOffset.UtcNow;

        var run = new Run
        {
            Id = runId,
            RepositoryPath = repositoryPath,
            OriginatingBranch = originatingBranch,
            ModelSource = ModelSource.GitHubCopilot,
            Task = goal,
            SubmittingUser = submittingUser,
            Status = RunStatus.InProgress,
            StartedAt = now,
            ProjectId = projectId,
            ModelId = modelId,
            AgentName = "Coordinator",
            ParentRunId = null,
            SubtaskId = null,
        };

        await _runStore.InsertAsync(run, ct).ConfigureAwait(false);

        var entry = _streamStore.Create(runId.ToString(), submittingUser);
        entry.RecordNext(EventTypes.CoordinatorStarted, new { goal });

        var input = new CoordinatorDraftInput(
            runId.ToString(),
            projectId.ToString(),
            goal,
            submittingUser,
            repositoryPath,
            modelId);

        // Per-run CTS, registered so Abandon -> Cts.Cancel() can tear the run down (mirrors RunOrchestrator).
        var runCts = new CancellationTokenSource();
        var streamingRun = await _factory.StartAsync(input, runId.ToString(), runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(runId.ToString(), streamingRun, runCts);
        StartWatching(runId.ToString(), streamingRun, entry, submittingUser, runCt);

        return runId;
    }

    /// <summary>
    /// Resume seam — confirm. Advances the outcome spec to <c>confirmed</c> and lets the run
    /// finalize and terminate (Phase 1). Returns <see cref="CoordinatorGateOutcome"/> so the HTTP
    /// layer can map to 202 / 409 without holding any orchestration state.
    /// </summary>
    public Task<CoordinatorGateOutcome> ConfirmOutcomeSpecAsync(string runId, string confirmedBy, CancellationToken ct) =>
        SubmitDecisionAsync(
            runId,
            new CoordinatorOutcomeSpecDecision(Confirmed: true, Revise: false, ConfirmedBy: confirmedBy),
            ct);

    /// <summary>
    /// Resume seam — revise. The coordinator re-drafts the spec using <paramref name="feedback"/>
    /// and re-suspends at the gate. No dispatch occurs.
    /// </summary>
    public Task<CoordinatorGateOutcome> ReviseOutcomeSpecAsync(
        string runId, string feedback, string requestedBy, CancellationToken ct)
    {
        _logger.LogInformation("Coordinator outcome-spec revision requested for run {RunId} by {User}", runId, requestedBy);
        return SubmitDecisionAsync(
            runId,
            new CoordinatorOutcomeSpecDecision(Confirmed: false, Revise: true, ReviseFeedback: feedback),
            ct);
    }

    /// <summary>Reads the current persisted outcome spec for a coordinator run (convenience for the GET endpoint).</summary>
    public async Task<OutcomeSpec?> GetOutcomeSpecAsync(string runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.OutcomeSpecs
            .FirstOrDefaultAsync(s => s.CoordinatorRunId == runId, ct)
            .ConfigureAwait(false);
    }

    private async Task<CoordinatorGateOutcome> SubmitDecisionAsync(
        string runId, CoordinatorOutcomeSpecDecision decision, CancellationToken ct)
    {
        _ = ct;
        var streamingRun = _registry.Get(runId);
        if (streamingRun is null)
            return CoordinatorGateOutcome.RunNotActive;

        // Atomic consume for replay/double-POST protection (mirrors the review endpoint).
        var pending = _pendingStore.TryRemove(runId);
        if (pending is null)
            return CoordinatorGateOutcome.NoPendingGate;

        if (decision.Revise)
        {
            // Re-arm the entry as live; the re-draft will MarkAwaitingReview again on re-suspend.
            var entry = _streamStore.Get(runId);
            entry?.ClearAwaitingReview();
            entry?.RecordNext(EventTypes.RevisionStarted, new { });
        }

        var response = pending.Request.CreateResponse(decision);
        try
        {
            await streamingRun.SendResponseAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator SendResponseAsync failed for run {RunId}", runId);
            return CoordinatorGateOutcome.RunNotActive;
        }

        return CoordinatorGateOutcome.Accepted;
    }

    // -----------------------------------------------------------------------
    // Supervised watch loop (mirrors RunWatchLoopService for the coordinator graph)
    // -----------------------------------------------------------------------

    private void StartWatching(
        string runId, StreamingRun streamingRun, RunStreamEntry entry, string ownerUser, CancellationToken runCt)
    {
        _ = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCt, _appStopping);
            try
            {
                await WatchAsync(runId, streamingRun, entry, ownerUser, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App shutting down — not an error.
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested)
            {
                _logger.LogInformation("Coordinator run {RunId} abandoned", runId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coordinator watch loop failed for run {RunId}; transitioning to Failed", runId);
                await FailRunSafeAsync(runId, entry).ConfigureAwait(false);
            }
        }, _appStopping);
    }

    private async Task WatchAsync(
        string runId, StreamingRun streamingRun, RunStreamEntry entry, string ownerUser, CancellationToken ct)
    {
        await foreach (var evt in streamingRun.WatchStreamAsync(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case RequestInfoEvent rie:
                    // Suspended at the await-confirmation gate. The draft executor already emitted
                    // coordinator.outcome_spec and marked the entry awaiting-review.
                    if (_pendingStore.Get(runId) is not null)
                        break;
                    _pendingStore.Set(runId, rie.Request, ownerUser);
                    break;

                case WorkflowOutputEvent woe:
                    if (woe.Is<CoordinatorOutcome>(out var outcome))
                    {
                        await FinalizeRunAsync(runId, outcome!, entry).ConfigureAwait(false);
                        _registry.Abandon(runId);
                        _factory.DeleteCheckpoints(runId);
                        return;
                    }
                    break;
            }
        }
    }

    private async Task FinalizeRunAsync(string runId, CoordinatorOutcome outcome, RunStreamEntry entry)
    {
        var parsedRunId = RunId.Parse(runId);
        var status = outcome.Status == "confirmed" ? RunStatus.Completed : RunStatus.Declined;

        await _runStore.TrySetTerminalStatusAsync(
            parsedRunId, status, DateTimeOffset.UtcNow, outcome.Status, CancellationToken.None).ConfigureAwait(false);

        entry.RecordNext(EventTypes.RunCompleted, new { result = outcome.Status });
        _streamStore.Complete(runId);
        _ = _runWorkflowFactory.PersistRunEventsAsync(runId);
    }

    private async Task FailRunSafeAsync(string runId, RunStreamEntry entry)
    {
        try
        {
            await _runStore.TrySetTerminalStatusAsync(
                RunId.Parse(runId), RunStatus.Failed, DateTimeOffset.UtcNow, "watch_loop_error", CancellationToken.None)
                .ConfigureAwait(false);
            entry.RecordNext(EventTypes.RunFailed, new { reason = "watch_loop_error" });
            _streamStore.Complete(runId);
            _ = _runWorkflowFactory.PersistRunEventsAsync(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition coordinator run {RunId} to Failed", runId);
        }
        finally
        {
            _registry.Abandon(runId);
        }
    }
}

/// <summary>Result of a resume-seam call so the HTTP layer can map to a status code.</summary>
public enum CoordinatorGateOutcome
{
    /// <summary>Decision accepted and sent to the suspended run.</summary>
    Accepted,

    /// <summary>No live workflow is registered for this run (terminated, never started, or post-restart).</summary>
    RunNotActive,

    /// <summary>The run is not currently suspended at a confirmation gate (already consumed or not yet suspended).</summary>
    NoPendingGate,
}
