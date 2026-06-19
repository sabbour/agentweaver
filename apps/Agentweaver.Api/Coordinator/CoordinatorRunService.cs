using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Coordinator;

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
    private readonly CoordinatorDispatchService _dispatchService;
    private readonly CoordinatorAssemblyStore _assemblyStore;
    private readonly ICoordinatorAssembly _assembly;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunOptionsStore _runOptions;
    private readonly IBacklogTaskStore _backlogStore;
    private readonly ILogger<CoordinatorRunService> _logger;
    private readonly bool _autoDispatch;
    private readonly CancellationToken _appStopping;

    public CoordinatorRunService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        CoordinatorWorkflowFactory factory,
        RunWorkflowFactory runWorkflowFactory,
        CoordinatorDispatchService dispatchService,
        CoordinatorAssemblyStore assemblyStore,
        ICoordinatorAssembly assembly,
        IServiceScopeFactory scopeFactory,
        IRunOptionsStore runOptions,
        IBacklogTaskStore backlogStore,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<CoordinatorRunService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _runWorkflowFactory = runWorkflowFactory;
        _dispatchService = dispatchService;
        _assemblyStore = assemblyStore;
        _assembly = assembly;
        _scopeFactory = scopeFactory;
        _runOptions = runOptions;
        _backlogStore = backlogStore;
        _logger = logger;
        // Auto-dispatch is ON in production: confirming a spec launches and tracks child runs.
        // Hermetic web tests (non-git workspaces, signed-out tokens) disable it so the Phase 1
        // confirm/decline lifecycle and the decompose+persist contract stay deterministic; the
        // dispatch-frontier logic is covered by a focused unit test instead.
        _autoDispatch = configuration.GetValue("Coordinator:AutoDispatch", true);
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
        bool autoApproveTools,
        bool autopilot,
        CancellationToken ct,
        string? retriedFrom = null)
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
            RetriedFrom = retriedFrom,
        };

        await _runStore.InsertAsync(run, ct).ConfigureAwait(false);

        // Interactive runs share the same activation body as unattended backlog-pickup runs, but they
        // do NOT schedule the unattended confirm: a human confirms/revises the spec via the HTTP
        // endpoints.
        await ActivateAsync(run, new RunOptions(AutoApproveTools: autoApproveTools, Autopilot: autopilot))
            .ConfigureAwait(false);

        return runId;
    }

    /// <summary>
    /// Retriggers a FAILED backlog-pickup coordinator run (POST /api/runs/{id}/retry) as a FRESH
    /// unattended coordinator run. Mints a NEW <see cref="RunId"/>, preserves the durable
    /// <see cref="RunOrigin.BacklogPickup"/> origin and the accountable <paramref name="source"/>
    /// SubmittingUser (the original CapturedBy, Principle IX), and is identity-shaped exactly like the
    /// pickup path (WorkflowRunId null, resolved by run_id). It does NOT re-claim a backlog task — the
    /// task is already Claimed — so there is no claim+reserve transaction here; the run row is inserted
    /// directly. Like the heartbeat pickup, it schedules the unattended outcome-spec confirmation on
    /// behalf of the accountable human. Returns the new run id.
    /// </summary>
    public async Task<RunId> StartRetriedPickupCoordinatorRunAsync(
        Run source, bool autoApproveTools, bool autopilot, CancellationToken ct)
    {
        var runId = RunId.New();
        var now = DateTimeOffset.UtcNow;

        var run = new Run
        {
            Id = runId,
            RepositoryPath = source.RepositoryPath,
            OriginatingBranch = source.OriginatingBranch,
            ModelSource = ModelSource.GitHubCopilot,
            ModelId = source.ModelId,
            Task = source.Task,
            SubmittingUser = source.SubmittingUser,    // accountable human carried through (Principle IX)
            Status = RunStatus.InProgress,
            StartedAt = now,
            ProjectId = source.ProjectId,
            AgentName = "Coordinator",
            ParentRunId = null,
            SubtaskId = null,
            WorkflowRunId = null,                      // identity parity: detail page resolves by run_id
            Origin = RunOrigin.BacklogPickup,          // preserve durable pickup origin marker
            RetriedFrom = source.Id.ToString(),
        };

        await _runStore.InsertAsync(run, ct).ConfigureAwait(false);

        await ActivateAsync(run, new RunOptions(AutoApproveTools: autoApproveTools, Autopilot: autopilot))
            .ConfigureAwait(false);

        // Unattended confirm on behalf of the accountable human, mirroring the heartbeat pickup path.
        ScheduleUnattendedConfirm(runId.ToString(), source.SubmittingUser);

        return runId;
    }

    /// <summary>
    /// Activates a coordinator run whose row is ALREADY persisted (reserved) by the atomic
    /// claim+reserve transaction (Feature 009, section 1.5) — used by unattended heartbeat pickup. It
    /// does NOT insert the run row again (Tank's transaction already did); it seeds RunOptions, opens
    /// the stream, starts + supervises the coordinator workflow, then performs the Phase 1
    /// outcome-spec confirmation UNATTENDED on behalf of <paramref name="confirmedBy"/> (the
    /// accountable human), because Autopilot does not bypass the confirmation gate.
    /// </summary>
    public async Task StartReservedCoordinatorRunAsync(
        Run reservedRun, bool autoApproveTools, bool autopilot, string confirmedBy, CancellationToken ct)
    {
        await ActivateAsync(reservedRun, new RunOptions(AutoApproveTools: autoApproveTools, Autopilot: autopilot))
            .ConfigureAwait(false);

        // Fire-and-forget bounded loop: confirm the spec once it arms. Autopilot only auto-answers
        // child clarifying questions and does NOT bypass this confirmation gate, so the pickup path
        // must confirm the reversible PLAN on behalf of the accountable human (Principle IX). The
        // destructive/irreversible tool gates, child-run permission approvals and the Phase-3
        // assembly human-review gate remain enforced by the safety floor (Principle X).
        ScheduleUnattendedConfirm(reservedRun.Id.ToString(), confirmedBy);
    }

    /// <summary>
    /// Shared coordinator activation body (interactive and reserved). The run row is assumed already
    /// persisted. Seeds the per-run options so the coordinator's own model turns (and the cascade to
    /// child runs) honor the launch flags, opens the live stream, starts the MAF workflow under a
    /// per-run CTS (registered so Abandon -> Cts.Cancel() tears the run down, mirroring
    /// RunOrchestrator), and starts the supervised watch loop.
    /// </summary>
    private async Task ActivateAsync(Run run, RunOptions options)
    {
        var runId = run.Id.ToString();
        _runOptions.Set(runId, options);

        var entry = _streamStore.Create(runId, run.SubmittingUser);
        entry.RecordNext(EventTypes.CoordinatorStarted, new { goal = run.Task });

        var input = new CoordinatorDraftInput(
            runId,
            run.ProjectId!.Value.ToString(),
            run.Task,
            run.SubmittingUser,
            run.RepositoryPath,
            run.ModelId);

        var runCts = new CancellationTokenSource();
        var streamingRun = await _factory.StartAsync(input, runId, runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(runId, streamingRun, runCts);
        StartWatching(runId, streamingRun, entry, run.SubmittingUser, runCt);
    }

    /// <summary>
    /// Bounded, fire-and-forget loop that performs the Phase 1 outcome-spec confirmation unattended
    /// (no human present). Polls <see cref="GetOutcomeSpecAsync"/> until the spec is
    /// <c>awaiting_confirmation</c>, then resumes via the same <see cref="ConfirmOutcomeSpecAsync"/>
    /// seam a human uses, attributed to <paramref name="confirmedBy"/> (= the backlog task's
    /// CapturedBy, Principle IX). Stops on success, on a human beating it to the gate, on app
    /// shutdown, or after a 5-minute deadline so it can never spin forever.
    /// </summary>
    private void ScheduleUnattendedConfirm(string runId, string confirmedBy)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
                while (DateTimeOffset.UtcNow < deadline && !_appStopping.IsCancellationRequested)
                {
                    var spec = await GetOutcomeSpecAsync(runId, _appStopping).ConfigureAwait(false);
                    if (spec?.Status == "awaiting_confirmation")
                    {
                        var outcome = await ConfirmOutcomeSpecAsync(runId, confirmedBy, _appStopping).ConfigureAwait(false);
                        if (outcome == CoordinatorGateOutcome.Accepted)
                            return;
                    }
                    else if (spec?.Status is "confirmed" or "declined")
                    {
                        return;   // already advanced (e.g. a human confirmed first)
                    }

                    await Task.Delay(500, _appStopping).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "Unattended confirm timed out for coordinator run {RunId}; left for a human", runId);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App shutting down — not an error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unattended confirm loop failed for coordinator run {RunId}", runId);
            }
        }, _appStopping);
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

    // After a draft/re-draft, the spec is persisted as awaiting_confirmation and
    // coordinator.outcome_spec is emitted (so the UI enables Confirm) BEFORE the MAF runtime
    // suspends at the request port and the watch loop arms _pendingStore. A fast confirm in that
    // window finds no pending gate even though one is imminent. We poll for the gate to arm for a
    // bounded interval before reporting NoPendingGate.
    private const int GateArmWaitTimeoutMs = 3000;
    private const int GateArmPollIntervalMs = 50;

    private async Task<CoordinatorGateOutcome> SubmitDecisionAsync(
        string runId, CoordinatorOutcomeSpecDecision decision, CancellationToken ct)
    {
        var streamingRun = _registry.Get(runId);
        if (streamingRun is null)
            return CoordinatorGateOutcome.RunNotActive;

        // Atomic consume for replay/double-POST protection (mirrors the review endpoint).
        var pending = _pendingStore.TryRemove(runId);
        if (pending is null)
        {
            // The gate may simply not be armed YET (the ordering race described above). Wait for it
            // to arm — but ONLY while the persisted spec is still awaiting_confirmation. If the spec
            // is already confirmed/declined (a genuine double-submit, or a drained gate after the
            // dispatch hand-off), there is no gate coming, so we return NoPendingGate immediately and
            // preserve replay/double-POST protection.
            pending = await WaitForGateToArmAsync(runId, ct).ConfigureAwait(false);
            if (pending is null)
                return CoordinatorGateOutcome.NoPendingGate;
        }

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

    /// <summary>
    /// Bounded wait for the confirmation gate to arm, closing the ordering race where the spec is
    /// already persisted/emitted as <c>awaiting_confirmation</c> but the MAF runtime has not yet
    /// suspended at the request port (so the watch loop has not yet called
    /// <see cref="PendingRequestStore.Set"/>). Returns the pending entry if it arms within
    /// <see cref="GateArmWaitTimeoutMs"/>, otherwise <c>null</c>.
    ///
    /// We only wait while the persisted spec is still <c>awaiting_confirmation</c>: a spec that has
    /// already advanced to <c>confirmed</c>/<c>declined</c> means the gate was genuinely consumed
    /// (double-submit / drained gate after dispatch hand-off), so there is no gate coming and we
    /// fall through to NoPendingGate immediately — preserving replay/double-POST protection.
    /// </summary>
    private async Task<PendingEntry?> WaitForGateToArmAsync(string runId, CancellationToken ct)
    {
        var spec = await GetOutcomeSpecAsync(runId, ct).ConfigureAwait(false);
        if (spec is null || spec.Status != "awaiting_confirmation")
            return null;

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(GateArmWaitTimeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(GateArmPollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            var pending = _pendingStore.TryRemove(runId);
            if (pending is not null)
                return pending;
        }

        return null;
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
                        // Phase 2: on confirm, if a work plan was persisted and auto-dispatch is on,
                        // DON'T terminate the coordinator run. Hand off to the dispatch + observe
                        // engine, which keeps the run in progress and the stream open while it
                        // launches and tracks child runs (subtask.* + coordinator.topology events).
                        if (outcome!.Status == "confirmed"
                            && _autoDispatch
                            && await TryHandOffToDispatchAsync(runId).ConfigureAwait(false))
                        {
                            // MAF coordinator workflow is done; release its registry slot + checkpoints,
                            // but leave the run InProgress and the stream open for dispatch/observe.
                            _registry.Abandon(runId);
                            _factory.DeleteCheckpoints(runId);
                            return;
                        }

                        await FinalizeRunAsync(runId, outcome!, entry).ConfigureAwait(false);
                        _registry.Abandon(runId);
                        _factory.DeleteCheckpoints(runId);
                        return;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// If a work plan with at least one subtask was persisted for this confirmed run, starts the
    /// dispatch + observe engine (which keeps the coordinator run in progress) and returns true so
    /// the caller skips the Phase 1 finalize/complete path. Returns false when there is no plan or
    /// no subtasks, so the run finalizes normally.
    /// </summary>
    private async Task<bool> TryHandOffToDispatchAsync(string runId)
    {
        Run? run;
        bool hasSubtasks;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var workPlan = await db.WorkPlans.AsNoTracking()
                .FirstOrDefaultAsync(w => w.CoordinatorRunId == runId).ConfigureAwait(false);
            if (workPlan is null)
                return false;
            hasSubtasks = await db.Subtasks.AsNoTracking()
                .AnyAsync(s => s.WorkPlanId == workPlan.Id).ConfigureAwait(false);
        }

        if (!hasSubtasks)
            return false;

        run = await _runStore.GetAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
        if (run is null)
            return false;

        _dispatchService.StartDispatch(new CoordinatorDispatchContext(
            CoordinatorRunId: runId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId));

        _logger.LogInformation("Coordinator run {RunId} confirmed; handed off to dispatch + observe", runId);
        return true;
    }

    // -----------------------------------------------------------------------
    // Restart recovery — survive a process restart mid-orchestration.
    //
    // The spec/confirm phase is a checkpointed MAF workflow and resumes from its checkpoint. Once the
    // human confirms, the coordinator hands off to the non-MAF dispatch + collective-assembly engines
    // (D3 — service-driven). Those background loops are in-memory DRIVERS over state that is fully
    // PERSISTED in the relational store (WorkPlan.Status / AssemblyStage / IntegrationBranch, Subtask
    // rows + child run rows). So we don't need MAF checkpoints for them: on restart we recreate the
    // run's stream entry and RE-ARM the correct engine from the persisted work-plan status. Every
    // engine entry point is idempotent (in-memory guard + DB CAS), so re-arming is safe.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Recovers coordinator (parent) runs that were <see cref="RunStatus.InProgress"/> when the
    /// process died. Called ONCE at startup, AFTER <c>WorkflowRestartService.RecoverAsync</c> (which
    /// has already failed any stranded child runs), so a re-dispatched subtask always launches a
    /// fresh child. Each interrupted coordinator is routed by its persisted work-plan status; a
    /// failure to recover one run never aborts the others.
    /// </summary>
    public async Task RecoverInterruptedRunsAsync(CancellationToken ct)
    {
        var inProgress = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in inProgress)
        {
            if (run.ParentRunId is not null || !string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal))
                continue;

            try
            {
                await RecoverOneAsync(run, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coordinator restart recovery failed for run {RunId}; failing it", run.Id);
                var entry = _streamStore.Get(run.Id.ToString()) ?? _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
                await FailRunSafeAsync(run.Id.ToString(), entry).ConfigureAwait(false);
            }
        }
    }

    private async Task RecoverOneAsync(Run run, CancellationToken ct)
    {
        var runId = run.Id.ToString();

        WorkPlanAssemblyState? planState;
        int? workPlanId;
        bool hasSubtasks;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking()
                .FirstOrDefaultAsync(w => w.CoordinatorRunId == runId, ct).ConfigureAwait(false);
            workPlanId = plan?.Id;
            planState = plan is null ? null : new WorkPlanAssemblyState(plan.Id, plan.Status, plan.AssemblyStage, plan.IntegrationBranch);
            hasSubtasks = plan is not null && await db.Subtasks.AsNoTracking()
                .AnyAsync(s => s.WorkPlanId == plan.Id, ct).ConfigureAwait(false);
        }

        // (a) No work plan yet — still in the checkpointed spec draft/confirm phase. Resume the MAF
        // workflow so the user can confirm / revise the outcome spec exactly as before.
        var action = CoordinatorRecoveryRouter.Route(planState is not null, hasSubtasks, planState?.Status);

        if (action == CoordinatorRecoveryAction.ResumeSpecPhase)
        {
            await RecoverSpecPhaseAsync(run, ct).ConfigureAwait(false);
            await TryRearmUnattendedConfirmAsync(run, ct).ConfigureAwait(false);
            return;
        }

        // (b) Plan exists but produced no subtasks — nothing to dispatch; finalize per the spec.
        if (action == CoordinatorRecoveryAction.FinalizeNoSubtasks)
        {
            var spec = await GetOutcomeSpecAsync(runId, ct).ConfigureAwait(false);
            var terminal = spec?.Status == "declined" ? RunStatus.Declined : RunStatus.Completed;
            var entry0 = _streamStore.Get(runId) ?? _streamStore.Create(runId, run.SubmittingUser);
            await _runStore.TrySetTerminalStatusAsync(
                run.Id, terminal, DateTimeOffset.UtcNow, spec?.Status ?? "confirmed", ct).ConfigureAwait(false);
            entry0.RecordNext(EventTypes.RunCompleted, new { result = spec?.Status ?? "confirmed" });
            _streamStore.Complete(runId);
            _factory.DeleteCheckpoints(runId);
            return;
        }

        var context = new CoordinatorDispatchContext(
            CoordinatorRunId: runId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId);

        // Recreate the live stream entry BEFORE re-arming any engine: the dispatch / assembly loops
        // emit onto _streamStore.Get(runId) and silently no-op if the entry is absent.
        var entry = _streamStore.Get(runId) ?? _streamStore.Create(runId, run.SubmittingUser);
        entry.RecordNext(EventTypes.CoordinatorRecovered, new { status = planState!.Status });

        switch (action)
        {
            // (c) Children still in flight — reset the runs that crashed mid-execution back to pending
            // (their child runs were already failed by the generic restart sweep) and re-arm dispatch.
            // assemble_ready / completed / failed / rai_flagged subtasks keep their terminal status.
            case CoordinatorRecoveryAction.Dispatch:
                await ResetInFlightSubtasksAsync(workPlanId!.Value, ct).ConfigureAwait(false);
                _dispatchService.StartDispatch(context);
                _logger.LogInformation("Recovered coordinator run {RunId} into dispatch (status was {Status})", runId, planState.Status);
                break;

            // (d) All children terminal, awaiting collective assembly — re-arm assembly (the DB CAS
            // claims awaiting_assembly -> assembling exactly once).
            case CoordinatorRecoveryAction.Assemble:
                _assembly.StartAssembly(context);
                _logger.LogInformation("Recovered coordinator run {RunId} into collective assembly (awaiting_assembly)", runId);
                break;

            // (e) Crashed mid-assembly or while parked at the collective human-review gate. The
            // integration-branch build + RAI + review-gate arming all live in memory and are gone, so
            // reset the plan back to awaiting_assembly and re-run the (idempotent) assembly core — it
            // rebuilds the integration branch and re-arms the review gate, identical to a request-changes
            // wave. This is the only safe way to restore the in-memory review gate the HTTP review
            // endpoint completes against.
            case CoordinatorRecoveryAction.ReArmAssembly:
                await _assemblyStore.SetStatusAndStageAsync(
                    workPlanId!.Value, WorkPlanStatus.AwaitingAssembly, null, ct).ConfigureAwait(false);
                _assembly.StartAssembly(context);
                _logger.LogInformation("Recovered coordinator run {RunId} into collective assembly (re-armed from {Status})", runId, planState.Status);
                break;

            // (f) The plan reached a terminal/parked state but the coordinator run row was never flipped
            // off InProgress (a crash between the plan write and the run finalize). Settle the run row.
            case CoordinatorRecoveryAction.SettleComplete:
                await _runStore.TrySetTerminalStatusAsync(run.Id, RunStatus.Completed, DateTimeOffset.UtcNow, "complete", ct).ConfigureAwait(false);
                entry.RecordNext(EventTypes.RunCompleted, new { result = "complete" });
                _streamStore.Complete(runId);
                _factory.DeleteCheckpoints(runId);
                break;

            case CoordinatorRecoveryAction.SettleFailed:
                await _runStore.TrySetTerminalStatusAsync(
                    run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, run.Result ?? planState.Status, ct).ConfigureAwait(false);
                entry.RecordNext(EventTypes.RunFailed, new { reason = run.Result ?? planState.Status });
                _streamStore.Complete(runId);
                _factory.DeleteCheckpoints(runId);
                break;
        }
    }

    /// <summary>
    /// Resumes a coordinator run that was suspended at the confirmation gate (spec draft/confirm phase)
    /// from its MAF checkpoint, mirroring <c>WorkflowRestartService</c> step 4: recreate the stream
    /// entry, resume the workflow, repopulate the pending request so confirm/revise works, then start
    /// the supervised watch loop. If no checkpoint exists the run cannot be replayed, so it is failed.
    /// </summary>
    private async Task RecoverSpecPhaseAsync(Run run, CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var entry = _streamStore.Get(runId) ?? _streamStore.Create(runId, run.SubmittingUser);
        entry.MarkAwaitingReview();

        var checkpointInfo = _factory.GetLatestCheckpoint(runId);
        if (checkpointInfo is null)
        {
            _logger.LogWarning(
                "Coordinator run {RunId} was drafting its spec at restart with no checkpoint; failing it", run.Id);
            await FailRunSafeAsync(runId, entry).ConfigureAwait(false);
            return;
        }

        var runCts = new CancellationTokenSource();
        var streamingRun = await _factory.ResumeAsync(checkpointInfo, runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(runId, streamingRun, runCts);

        // Repopulate the pending confirmation request so the confirm/revise endpoints find a live gate.
        var status = await streamingRun.GetStatusAsync(ct).ConfigureAwait(false);
        if (status == Microsoft.Agents.AI.Workflows.RunStatus.PendingRequests)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await foreach (var evt in streamingRun.WatchStreamAsync(cts.Token).ConfigureAwait(false))
                {
                    if (evt is RequestInfoEvent rie)
                    {
                        if (_pendingStore.Get(runId) is null)
                            _pendingStore.Set(runId, rie.Request, run.SubmittingUser);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* timeout is acceptable */ }
        }

        StartWatching(runId, streamingRun, entry, run.SubmittingUser, runCt);
        _logger.LogInformation("Recovered coordinator run {RunId} at the spec confirmation gate", run.Id);
    }

    /// <summary>
    /// Unattended-confirm recovery, keyed on the DURABLE <see cref="RunOrigin"/> marker (Feature 009,
    /// section 3.6), never on inference from per-project pickup settings. For a recovered coordinator
    /// parent run that is parked at <c>awaiting_confirmation</c>:
    /// <list type="bullet">
    /// <item>If <c>Origin == BacklogPickup</c>, resolve the accountable human from the 1:1 backlog
    /// task pointing at this run (<c>backlog_tasks.run_id == run.Id</c>) and re-arm
    /// <see cref="ScheduleUnattendedConfirm"/> with <c>confirmedBy = task.CapturedBy</c> (Principle
    /// IX). If the backlog task is missing (e.g. project deleted), leave the run awaiting confirmation
    /// rather than auto-confirming without an accountable human.</item>
    /// <item>If <c>Origin == Interactive</c>, a human owns this gate — do NOT auto-confirm; the run
    /// stays awaiting confirmation, exactly as before the restart (Principles IX/X/XI).</item>
    /// </list>
    /// </summary>
    private async Task TryRearmUnattendedConfirmAsync(Run run, CancellationToken ct)
    {
        if (run.Origin != RunOrigin.BacklogPickup)
            return;   // Interactive: a human owns this gate; never auto-confirm.

        var spec = await GetOutcomeSpecAsync(run.Id.ToString(), ct).ConfigureAwait(false);
        if (spec?.Status != "awaiting_confirmation")
            return;   // Not parked at the confirmation gate; nothing to re-arm.

        var task = await _backlogStore.GetByRunIdAsync(run.Id, ct).ConfigureAwait(false);
        if (task is null)
        {
            _logger.LogWarning(
                "Recovered backlog-pickup coordinator run {RunId} is awaiting confirmation but its backlog "
                + "task is missing (project deleted?); left for a human", run.Id);
            return;
        }

        ScheduleUnattendedConfirm(run.Id.ToString(), task.CapturedBy);
        _logger.LogInformation(
            "Re-armed unattended confirm for recovered backlog-pickup coordinator run {RunId}", run.Id);
    }

    /// <summary>
    /// Resets subtasks that were mid-flight (<c>dispatched</c> / <c>running</c>) back to
    /// <c>pending</c> so the dispatch frontier re-launches them with fresh child runs. Terminal
    /// subtasks (assemble_ready / completed / failed / rai_flagged) are left untouched — their work
    /// (and child branches) survive the restart and feed collective assembly.
    /// </summary>
    private async Task ResetInFlightSubtasksAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Subtasks
            .Where(s => s.WorkPlanId == workPlanId
                && (s.Status == SubtaskStatus.Dispatched || s.Status == SubtaskStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SubtaskStatus.Pending)
                .SetProperty(s => s.ChildRunId, (string?)null)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // API seams for the HTTP wave (Tank): GET /children + GET /plan. These are read-only
    // projections over the persisted work plan; they do not mutate dispatch state.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Work plan view for <c>GET /plan</c>: the <see cref="WorkPlan"/> plus its subtasks (id, title,
    /// scope, assigned agent, selected model, phase, isolation, status, childRunId) and the
    /// dependency edges (subtaskId depends on dependsOnSubtaskId). Returns null when no plan exists
    /// for the coordinator run.
    /// </summary>
    public async Task<CoordinatorWorkPlanView?> GetWorkPlanAsync(string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var plan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null) return null;

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == plan.Id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var edges = await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false);

        // Surface the failure reason for a terminal/blocked plan from the coordinator run's result so
        // the UI can render "Failed: <reason>" without a separate round-trip.
        string? statusReason = null;
        if (plan.Status is WorkPlanStatus.AssemblyBlocked
                        or WorkPlanStatus.AssemblyFailed
                        or WorkPlanStatus.AssemblyDeclined
            && RunId.TryParse(coordinatorRunId, out var coordRunId))
        {
            var run = await _runStore.GetAsync(coordRunId, ct).ConfigureAwait(false);
            statusReason = run?.Result;
        }

        return new CoordinatorWorkPlanView(
            plan.Id,
            plan.CoordinatorRunId,
            plan.OutcomeSpecId,
            plan.Status,
            plan.IsolationSummary,
            subtasks.Select(s => new CoordinatorSubtaskView(
                s.Id, s.Title, s.Scope, s.AssignedAgent, s.SelectedModelId,
                s.Phase, s.IsolationStrategy, s.Status, s.ChildRunId)).ToList(),
            edges.Select(e => new CoordinatorDependencyView(e.SubtaskId, e.DependsOnSubtaskId)).ToList(),
            plan.AssemblyStage,
            statusReason);
    }

    /// <summary>
    /// Children view for <c>GET /children</c>: one row per subtask that has a dispatched child run,
    /// pairing the child run's persisted lifecycle (status, worktree branch, tree hash, step count)
    /// with the subtask's coordinator-side status. Returns an empty list when nothing has been
    /// dispatched yet (or no plan exists).
    /// </summary>
    public async Task<IReadOnlyList<CoordinatorChildView>> GetChildrenAsync(string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var plan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null) return [];

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == plan.Id && s.ChildRunId != null)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var children = new List<CoordinatorChildView>(subtasks.Count);
        foreach (var s in subtasks)
        {
            Run? child = RunId.TryParse(s.ChildRunId, out var cid)
                ? await _runStore.GetAsync(cid, ct).ConfigureAwait(false)
                : null;

            children.Add(new CoordinatorChildView(
                s.Id,
                s.ChildRunId!,
                s.Status,
                s.AssignedAgent,
                s.SelectedModelId,
                child?.Status.ToString(),
                child?.WorktreeBranch,
                child?.TreeHash,
                child?.StepCount ?? 0));
        }

        return children;
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

/// <summary>Read-only work plan projection for the <c>GET /plan</c> endpoint.</summary>
public sealed record CoordinatorWorkPlanView(
    int WorkPlanId,
    string CoordinatorRunId,
    int OutcomeSpecId,
    string Status,
    string? IsolationSummary,
    IReadOnlyList<CoordinatorSubtaskView> Subtasks,
    IReadOnlyList<CoordinatorDependencyView> Dependencies,
    string? AssemblyStage = null,
    string? StatusReason = null);

/// <summary>A subtask row in <see cref="CoordinatorWorkPlanView"/>.</summary>
public sealed record CoordinatorSubtaskView(
    int SubtaskId,
    string Title,
    string Scope,
    string AssignedAgent,
    string SelectedModelId,
    string Phase,
    string Isolation,
    string Status,
    string? ChildRunId);

/// <summary>A dependency edge: <see cref="SubtaskId"/> depends on <see cref="DependsOnSubtaskId"/>.</summary>
public sealed record CoordinatorDependencyView(int SubtaskId, int DependsOnSubtaskId);

/// <summary>A dispatched child run paired with its subtask status, for the <c>GET /children</c> endpoint.</summary>
public sealed record CoordinatorChildView(
    int SubtaskId,
    string ChildRunId,
    string SubtaskStatus,
    string AssignedAgent,
    string SelectedModelId,
    string? ChildRunStatus,
    string? WorktreeBranch,
    string? TreeHash,
    int StepCount);
