using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Git;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Hand-off seam from the dispatch engine to Phase 3 collective assembly. Lets
/// <see cref="CoordinatorDispatchService"/> trigger assembly without a hard dependency on the
/// concrete service (and lets tests assert the hand-off without launching the real pipeline).
/// </summary>
public interface ICoordinatorAssembly
{
    /// <summary>Launches the collective-assembly pipeline for a coordinator run (fire-and-forget).</summary>
    void StartAssembly(CoordinatorDispatchContext context);
}

/// <summary>
/// Feature 008 Phase 3 COLLECTIVE ASSEMBLY engine. Picks up where
/// <see cref="CoordinatorDispatchService.FinalizeDispatchAsync"/> stops (the work plan is left at
/// <see cref="WorkPlanStatus.AwaitingAssembly"/>) and runs ONE collective pipeline over the COMBINED
/// output of all children:
/// <c>eligibility gate → integration branch → collective RAI → ONE human review → ONE merge → ONE scribe</c>,
/// flowing back to the coordinator.
///
/// <para><b>D3 — service-driven, not a MAF graph.</b> The collective pipeline starts from
/// already-assembled GIT STATE (no agent turn to anchor a workflow), the human review routes BACK to
/// the coordinator (re-dispatch) rather than looping to a MAF agent, and the exactly-once/integration
/// build/HITL concerns are coordinator-owned. So this service sequences the steps directly and REUSES
/// the existing executors through <see cref="ICollectiveAssemblyPipeline"/>.</para>
///
/// <para><b>D4 — exactly-once.</b> <see cref="CoordinatorAssemblyStore.TryStartAssemblyAsync"/> is a
/// DB compare-and-swap (<c>awaiting_assembly → assembling</c>); only the winner proceeds. The
/// in-memory <see cref="_active"/> guard is a cheap first line, not the source of truth.</para>
///
/// <para><b>D2 — no partial assembly.</b> Every subtask must be assembly-eligible; any conflict while
/// building the integration branch stops with <c>coordinator.assembly_blocked</c> and no merge.</para>
///
/// <para><b>D6 — rejection routing.</b> On request_changes the coordinator infers which children to
/// redo from the files referenced in the feedback (+ their dependents) and re-dispatches via
/// <see cref="CoordinatorDispatchService.StartDispatch"/> (resolved lazily to avoid a DI cycle).</para>
/// </summary>
public sealed class CoordinatorAssemblyService : ICoordinatorAssembly
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly CoordinatorAssemblyStore _assemblyStore;
    private readonly AssemblyReviewGate _reviewGate;
    private readonly ICollectiveAssemblyPipeline _pipeline;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CoordinatorAssemblyService> _logger;
    private readonly CancellationToken _appStopping;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new();

    public CoordinatorAssemblyService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        CoordinatorAssemblyStore assemblyStore,
        AssemblyReviewGate reviewGate,
        ICollectiveAssemblyPipeline pipeline,
        IServiceScopeFactory scopeFactory,
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorAssemblyService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _assemblyStore = assemblyStore;
        _reviewGate = reviewGate;
        _pipeline = pipeline;
        _scopeFactory = scopeFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    /// <summary>The integration branch name (D1) derived from the coordinator run id.</summary>
    public static string IntegrationBranchName(string coordinatorRunId) =>
        $"agentweaver/integration/{coordinatorRunId}";

    /// <summary>
    /// Launches the collective-assembly pipeline for a coordinator run on a supervised background task
    /// (mirrors <see cref="CoordinatorDispatchService.StartDispatch"/>). Returns immediately. The
    /// in-memory guard short-circuits a duplicate concurrent launch; the DB CAS (D4) is the real
    /// exactly-once authority.
    /// </summary>
    public void StartAssembly(CoordinatorDispatchContext context)
    {
        if (!_active.TryAdd(context.CoordinatorRunId, 0))
        {
            _logger.LogInformation(
                "Collective assembly already active for run {RunId}; skipping", context.CoordinatorRunId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAssemblyAsync(context, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App shutting down — not an error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Collective assembly failed for run {RunId}", context.CoordinatorRunId);
            }
            finally
            {
                _active.TryRemove(context.CoordinatorRunId, out _);
            }
        }, _appStopping);
    }

    /// <summary>
    /// Drives the collective pipeline end to end. Exposed (internal) so tests can await the full run
    /// deterministically rather than racing the fire-and-forget background task.
    /// </summary>
    internal async Task RunAssemblyAsync(CoordinatorDispatchContext context, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null)
        {
            _logger.LogWarning(
                "Collective assembly: no work plan for run {RunId}; nothing to assemble", context.CoordinatorRunId);
            return;
        }

        var (workPlanId, subtasks, edges) = plan.Value;

        try
        {
            await RunAssemblyCoreAsync(context, workPlanId, subtasks, edges, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown / run abandon — leave the plan recoverable, no terminal write.
            throw;
        }
        catch (Exception ex)
        {
            // An UNEXPECTED fault (integration merge, pipeline, store, or emit path threw outside the
            // handled terminals). Never swallow it leaving subtasks parked with no signal: record a
            // human-readable terminal on the coordinator run, mark the plan failed, and emit the
            // terminal event so the UI shows the reason and the next action.
            await FailUnexpectedAsync(context, workPlanId, edges, ex, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The collective-assembly state machine (claim -&gt; integration -&gt; RAI -&gt; review -&gt; merge/scribe).
    /// Wrapped by <see cref="RunAssemblyAsync"/> so any unexpected fault is terminalized rather than
    /// leaving the children parked at assemble_ready with no signal.
    /// </summary>
    private async Task RunAssemblyCoreAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        List<Subtask> subtasks,
        List<(int, int)> edges,
        CancellationToken ct)
    {
        var integrationBranch = IntegrationBranchName(context.CoordinatorRunId);

        // D4 exactly-once claim: awaiting_assembly -> assembling.
        if (!await _assemblyStore.TryStartAssemblyAsync(workPlanId, integrationBranch, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Collective assembly: run {RunId} already claimed (not in awaiting_assembly); skipping",
                context.CoordinatorRunId);
            return;
        }

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyStarted, new
        {
            workPlanId,
            integrationBranch,
            subtaskCount = subtasks.Count,
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);

        // D2 eligibility gate — NO partial assembly.
        var statusById = subtasks.ToDictionary(s => s.Id, s => s.Status);
        var ineligible = AssemblyPlanning.IneligibleSubtasks(statusById);
        if (ineligible.Count > 0)
        {
            await BlockAsync(context, workPlanId, edges, "ineligible_subtasks", new
            {
                workPlanId,
                reason = "ineligible_subtasks",
                ineligibleSubtaskIds = ineligible,
            }, ct).ConfigureAwait(false);
            return;
        }

        // Eligible child branches in dependency (topological) order (D1). Only assemble_ready children
        // with a worktree branch + changes contribute a branch; no-change completed children are valid
        // eligible no-ops that contribute nothing to merge.
        var orderedIds = AssemblyPlanning.TopologicalOrder(subtasks.Select(s => s.Id).ToList(), edges);
        var childRunBySubtask = subtasks
            .Where(s => !string.IsNullOrEmpty(s.ChildRunId))
            .ToDictionary(s => s.Id, s => s.ChildRunId!);

        var branchesInOrder = new List<string>();
        var touchedFilesBySubtask = new Dictionary<int, IReadOnlySet<string>>();
        var includedSubtaskIds = new List<int>();
        foreach (var id in orderedIds)
        {
            if (!childRunBySubtask.TryGetValue(id, out var childRunId)) continue;
            if (!RunId.TryParse(childRunId, out var parsed)) continue;
            var run = await _runStore.GetAsync(parsed, ct).ConfigureAwait(false);
            if (run is null) continue;
            touchedFilesBySubtask[id] = AssemblyPlanning.ExtractTouchedFiles(run.Diff);
            if (!string.IsNullOrEmpty(run.WorktreeBranch)
                && !string.IsNullOrEmpty(run.Diff))
            {
                branchesInOrder.Add(run.WorktreeBranch);
                includedSubtaskIds.Add(id);
            }
        }

        // D1 — build the COMBINED integration branch.
        IntegrationBranchResult integration;
        try
        {
            integration = _pipeline.BuildIntegrationBranch(new CollectiveIntegrationRequest(
                context.RepositoryPath, context.OriginatingBranch, integrationBranch, branchesInOrder));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collective assembly: integration branch build threw for run {RunId}",
                context.CoordinatorRunId);
            await BlockAsync(context, workPlanId, edges, "integration_build_error", new
            {
                workPlanId,
                reason = "integration_build_error",
            }, ct).ConfigureAwait(false);
            return;
        }

        if (integration.Outcome == IntegrationBranchOutcome.Conflict)
        {
            // D2 — merging child branches into the integration branch conflicted: STOP, no merge.
            await BlockAsync(context, workPlanId, edges, "integration_conflict", new
            {
                workPlanId,
                reason = "integration_conflict",
                conflictingBranch = integration.ConflictingBranch,
                conflictingFiles = integration.ConflictingFiles,
            }, ct).ConfigureAwait(false);
            return;
        }

        var aggregateDiff = integration.Diff ?? string.Empty;
        var aggregateTreeHash = integration.TreeHash ?? string.Empty;

        // ── Collective RAI (advisory) ────────────────────────────────────────────────────────────
        await _assemblyStore.SetStageAsync(workPlanId, AssemblyStage.Rai, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyRaiStarted, new { workPlanId, integrationBranch });

        var rai = await _pipeline.RunRaiAsync(
            new CollectiveRaiRequest(context.CoordinatorRunId, context.RepositoryPath, aggregateDiff), ct)
            .ConfigureAwait(false);

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyRaiCompleted, new
        {
            workPlanId,
            raiSafetyFlagged = rai.SafetyFlagged,
        });

        // ── ONE human review gate (D5) ───────────────────────────────────────────────────────────
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.InReview, AssemblyStage.Review, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
        {
            workPlanId,
            integrationBranch,
            treeHash = aggregateTreeHash,
            includedSubtaskIds,
            raiSafetyFlagged = rai.SafetyFlagged,
            hasChanges = integration.HasChanges,
        });

        var decisionTask = _reviewGate.ArmAsync(context.CoordinatorRunId, context.SubmittingUser, ct);
        AssemblyReviewDecision decision;
        try
        {
            decision = await decisionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Collective assembly: review wait cancelled for run {RunId}; leaving in_review",
                context.CoordinatorRunId);
            return;
        }

        if (decision.Approved)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewApproved, new { workPlanId });
            await CompleteAfterApprovalAsync(
                context, workPlanId, edges, integrationBranch, aggregateTreeHash, ct).ConfigureAwait(false);
            return;
        }

        if (decision.RequestChanges)
        {
            await RequestChangesAsync(
                context, workPlanId, edges, decision, touchedFilesBySubtask, ct).ConfigureAwait(false);
            return;
        }

        // Pure decline (neither approved nor request_changes): terminal assembly_declined.
        const string declineReason = "assembly_declined";
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.AssemblyDeclined, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyDeclined, new
        {
            workPlanId,
            reason = declineReason,
            reviewer = decision.Reviewer,
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyDeclined, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Declined, declineReason, ct).ConfigureAwait(false);
        _streamStore.Complete(context.CoordinatorRunId);
        _logger.LogInformation("Collective assembly: run {RunId} declined", context.CoordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // Post-approval: ONE merge -> ONE scribe -> complete.
    // -----------------------------------------------------------------------

    private async Task CompleteAfterApprovalAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string integrationBranch,
        string aggregateTreeHash,
        CancellationToken ct)
    {
        // in_review -> assembling (during merge/scribe).
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Assembling, AssemblyStage.Merge, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyMergeStarted, new { workPlanId, integrationBranch });

        var merge = await _pipeline.MergeAsync(new CollectiveMergeRequest(
            context.CoordinatorRunId, context.RepositoryPath, context.OriginatingBranch,
            integrationBranch, aggregateTreeHash), ct).ConfigureAwait(false);

        if (merge.Outcome != CollectiveMergeOutcome.Merged)
        {
            var mergeReason = merge.Reason ?? merge.Outcome.ToString().ToLowerInvariant();
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyMergeFailed, new
            {
                workPlanId,
                reason = mergeReason,
                conflictingFiles = merge.ConflictingFiles,
            });
            await _assemblyStore.SetStatusAndStageAsync(
                workPlanId, WorkPlanStatus.AssemblyFailed, null, ct).ConfigureAwait(false);
            await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
            await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
                .ConfigureAwait(false);
            await TerminalizeCoordinatorRunAsync(
                context.CoordinatorRunId, RunStatus.MergeFailed, $"assembly_merge_failed: {mergeReason}", ct)
                .ConfigureAwait(false);
            _streamStore.Complete(context.CoordinatorRunId);
            _logger.LogWarning("Collective assembly: merge failed for run {RunId} ({Reason})",
                context.CoordinatorRunId, merge.Reason);
            return;
        }

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyMergeCompleted, new
        {
            workPlanId,
            commitHash = merge.CommitHash,
        });

        // ── ONE collective scribe ────────────────────────────────────────────────────────────────
        await _assemblyStore.SetStageAsync(workPlanId, AssemblyStage.Scribe, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeStarted, new { workPlanId });

        try
        {
            await _pipeline.RunScribeAsync(new CollectiveScribeRequest(
                context.CoordinatorRunId,
                context.ProjectId?.Value.ToString(),
                AgentName: "coordinator",
                context.RepositoryPath,
                ModelSource.GitHubCopilot.ToString(),
                ModelId: null,
                RunStartedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Scribe is best-effort; a failure must not fail the (already merged) assembly.
            _logger.LogWarning(ex, "Collective assembly: scribe pass failed for run {RunId} (non-fatal)",
                context.CoordinatorRunId);
        }

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeCompleted, new { workPlanId });

        // ── Complete ─────────────────────────────────────────────────────────────────────────────
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Complete, AssemblyStage.Done, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyCompleted, new
        {
            workPlanId,
            integrationBranch,
            commitHash = merge.CommitHash,
        });
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.Complete, edges, ct)
            .ConfigureAwait(false);

        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Completed, "assembly_complete", ct).ConfigureAwait(false);

        _streamStore.Complete(context.CoordinatorRunId);
        _logger.LogInformation("Collective assembly complete for run {RunId}", context.CoordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // Request-changes: infer affected children (D6), reset them, re-dispatch.
    // -----------------------------------------------------------------------

    private async Task RequestChangesAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        AssemblyReviewDecision decision,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        CancellationToken ct)
    {
        var rejection = AssemblyPlanning.InferRedispatch(
            decision.Feedback, decision.TargetFiles, touchedFilesBySubtask, edges);

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyChangesRequested, new
        {
            workPlanId,
            redispatchSubtaskIds = rejection.SubtaskIds,
            inferredFiles = rejection.InferredFiles,
            fellBackToAll = rejection.FellBackToAll,
            feedback = decision.Feedback,
        });

        // Reset the selected subtasks to pending (leave others' results intact); clear stage and move
        // the plan back to dispatching so the dispatch engine re-runs the affected frontier.
        await ResetSubtasksToPendingAsync(rejection.SubtaskIds, ct).ConfigureAwait(false);
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Dispatching, null, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.Dispatching, edges, ct)
            .ConfigureAwait(false);

        // Re-dispatch. CoordinatorDispatchService is resolved lazily (both singletons) to avoid a
        // constructor DI cycle (dispatch -> assembly -> dispatch). When the re-dispatched children
        // finish, FinalizeDispatchAsync returns the plan to awaiting_assembly and triggers a fresh
        // assembly pass (the DB CAS guards exactly-once again).
        var dispatch = _serviceProvider.GetRequiredService<ICoordinatorDispatch>();
        dispatch.StartDispatch(context);

        _logger.LogInformation(
            "Collective assembly: changes requested for run {RunId}; re-dispatching subtasks [{Ids}] (fallbackAll={Fallback})",
            context.CoordinatorRunId, string.Join(",", rejection.SubtaskIds), rejection.FellBackToAll);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task BlockAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string reason,
        object payload,
        CancellationToken ct)
    {
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.AssemblyBlocked, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyBlocked, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, $"assembly_blocked: {reason}", ct).ConfigureAwait(false);
        _streamStore.Complete(context.CoordinatorRunId);
        _logger.LogWarning("Collective assembly blocked for run {RunId}: {Reason}", context.CoordinatorRunId, reason);
    }

    /// <summary>
    /// Terminalizes the assembly background task on an UNEXPECTED fault: marks the work plan failed,
    /// emits <see cref="EventTypes.CoordinatorAssemblyFailed"/> with a human-readable reason, and
    /// records the same reason on the coordinator run so the UI never shows a bare "Failed" with no
    /// explanation. The inner emit/store work is itself guarded so a secondary fault cannot prevent
    /// the run from reaching a terminal status.
    /// </summary>
    private async Task FailUnexpectedAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        Exception ex,
        CancellationToken ct)
    {
        var reason = $"assembly_error: {ex.Message}";
        _logger.LogError(ex, "Collective assembly: unexpected error for run {RunId}", context.CoordinatorRunId);
        try
        {
            await _assemblyStore.SetStatusAndStageAsync(
                workPlanId, WorkPlanStatus.AssemblyFailed, null, ct).ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyFailed, new
            {
                workPlanId,
                reason,
                phase = "assembly",
            });
            await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
            await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
                .ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner,
                "Collective assembly: failed to record terminal state for run {RunId}", context.CoordinatorRunId);
        }
        await TerminalizeCoordinatorRunAsync(context.CoordinatorRunId, RunStatus.Failed, reason, ct)
            .ConfigureAwait(false);
        _streamStore.Complete(context.CoordinatorRunId);
    }

    /// <summary>
    /// Records a terminal status + human-readable result on the COORDINATOR run so the project runs
    /// list and run detail surface why assembly ended (instead of leaving the run InProgress, which a
    /// later restart would sweep to a bare "Failed"). A no-op when the run row is absent or already
    /// terminal (the CAS guard in <see cref="SqliteRunStore.TrySetTerminalStatusAsync"/>).
    /// </summary>
    private async Task TerminalizeCoordinatorRunAsync(
        string coordinatorRunId, RunStatus status, string result, CancellationToken ct)
    {
        if (RunId.TryParse(coordinatorRunId, out var id))
            await _runStore.TrySetTerminalStatusAsync(id, status, DateTimeOffset.UtcNow, result, ct)
                .ConfigureAwait(false);
    }

    private void Emit(string coordinatorRunId, string eventType, object payload) =>
        _streamStore.Get(coordinatorRunId)?.RecordNext(eventType, payload);

    private async Task EmitGraphAsync(string coordinatorRunId, int workPlanId, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is null) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var deps = (await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();
        var stage = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => w.AssemblyStage)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        entry.RecordNext(EventTypes.CoordinatorGraph,
            CoordinatorGraphDescriptor.Build(coordinatorRunId, subtasks, deps, stage));
    }

    private async Task EmitTopologyAsync(
        string coordinatorRunId, int workPlanId, string status,
        IReadOnlyCollection<(int, int)> edges, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is null) return;

        var subtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        entry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildSnapshot(
            coordinatorRunId, workPlanId, status, subtasks, edges, 0));
    }

    private async Task ResetSubtasksToPendingAsync(IReadOnlyCollection<int> subtaskIds, CancellationToken ct)
    {
        if (subtaskIds.Count == 0) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Subtasks
            .Where(s => subtaskIds.Contains(s.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SubtaskStatus.Pending)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    private async Task<(int WorkPlanId, List<Subtask> Subtasks, List<(int, int)> Edges)?> LoadPlanAsync(
        string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var workPlan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (workPlan is null) return null;

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlan.Id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var edges = (await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();

        return (workPlan.Id, subtasks, edges);
    }

    private async Task<List<Subtask>> ReloadSubtasksAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
