using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Git;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
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
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly CoordinatorAssemblyStore _assemblyStore;
    private readonly AssemblyReviewGate _reviewGate;
    private readonly ICollectiveAssemblyPipeline _pipeline;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;
    private readonly ILogger<CoordinatorAssemblyService> _logger;
    private readonly CancellationToken _appStopping;
    private readonly TimeSpan _reviewTimeout;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new();

    public CoordinatorAssemblyService(
        IRunStore runStore,
        RunStreamStore streamStore,
        CoordinatorAssemblyStore assemblyStore,
        AssemblyReviewGate reviewGate,
        ICollectiveAssemblyPipeline pipeline,
        IServiceScopeFactory scopeFactory,
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorAssemblyService> logger,
        IConfiguration? configuration = null,
        IPodNameRegistry? podRegistry = null,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _assemblyStore = assemblyStore;
        _reviewGate = reviewGate;
        _pipeline = pipeline;
        _scopeFactory = scopeFactory;
        _serviceProvider = serviceProvider;
        _podRegistry = podRegistry;
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
        var reviewTimeoutMinutes = configuration?.GetValue("Coordinator:AssemblyReviewTimeoutMinutes", 60.0) ?? 60.0;
        _reviewTimeout = TimeSpan.FromMinutes(Math.Max(1.0, reviewTimeoutMinutes));
    }

    /// <summary>The integration branch name (D1) derived from the coordinator run id.</summary>
    public static string IntegrationBranchName(string coordinatorRunId) =>
        $"agentweaver/integration/{coordinatorRunId}";

    /// <summary>
    /// Returns true when two subtasks are likely to conflict in the shared orchestration worktree
    /// and must therefore run serially rather than in parallel.
    ///
    /// <para>Conflict rules (conservative-by-default):</para>
    /// <list type="bullet">
    /// <item>If either subtask declares no file-path tokens in its <see cref="Subtask.Scope"/>,
    ///   the scope is undeclared and they are assumed to conflict (safe default).</item>
    /// <item>If both declare file-path tokens, they conflict when any token from one subtask
    ///   suffix-matches or filename-matches a token from the other (same logic as
    ///   <see cref="AssemblyPlanning.FilesMatch"/> in D6 rejection routing).</item>
    /// </list>
    ///
    /// Called by the dispatch loop to decide parallel vs serial scheduling before dispatching a
    /// ready frontier subtask alongside one that is already in-flight.
    /// </summary>
    internal static bool DoSubtasksConflict(Subtask subtask1, Subtask subtask2)
    {
        // NOTE: IsolationStrategy ("shared" vs "worktree") has NO runtime enforcement — all child
        // runs share a single worktree (see RunOrchestrator.StartChildRunAsync). A subtask labeled
        // "shared" can therefore still write files and clobber a sibling. We deliberately do NOT
        // short-circuit on isolation here; every pair flows through token-based filename matching so
        // mislabeled writers are still scheduled serially when their declared outputs overlap.
        var files1 = AssemblyPlanning.ExtractFileTokens(subtask1.Scope);
        var files2 = AssemblyPlanning.ExtractFileTokens(subtask2.Scope);

        // Either subtask has no declared paths → conservatively treat as conflicting.
        if (files1.Count == 0 || files2.Count == 0)
            return true;

        // Check for file-path overlap using the same matching rules as D6 rejection routing.
        foreach (var f1 in files1)
            foreach (var f2 in files2)
                if (FilesMatchPublic(f1, f2))
                    return true;

        return false;
    }

    // Mirrors AssemblyPlanning.FilesMatch (private static there) for use in DoSubtasksConflict.
    private static bool FilesMatchPublic(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase)) return true;
        if (b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)) return true;
        // Bare filename token (no separator) matches the other path's filename.
        if (!b.Contains('/') && string.Equals(FileNameOf(a), b, StringComparison.OrdinalIgnoreCase)) return true;
        if (!a.Contains('/') && string.Equals(FileNameOf(b), a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FileNameOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

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
            // Enrich the block with the offending subtasks (id + title + status + agent) so the UI can
            // name WHICH subtasks blocked assembly and WHY, instead of showing only the opaque code.
            // ineligibleSubtaskIds is retained for back-compat. camelCase member names are preserved
            // verbatim by the event serializer, matching the existing coordinator payload convention.
            var ineligibleSubtasks = ineligible
                .OrderBy(id => id)
                .Select(id =>
                {
                    var s = subtasks.First(x => x.Id == id);
                    return new { id = s.Id, title = s.Title, status = s.Status, agent = s.AssignedAgent };
                })
                .ToList();

            await BlockAsync(context, workPlanId, edges, "ineligible_subtasks", new
            {
                workPlanId,
                reason = "ineligible_subtasks",
                ineligibleSubtaskIds = ineligible,
                ineligibleSubtasks,
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
            await NeedsResolutionAsync(context, workPlanId, edges, "integration_conflict", new
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

        if (rai.SafetyFlagged)
        {
            await RaiBlockAsync(context, workPlanId, edges, integrationBranch, ct).ConfigureAwait(false);
            return;
        }

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
        _ = PollDeferredAssemblyReviewDecisionAsync(context, ct);
        AssemblyReviewDecision decision;
        try
        {
            var completed = await Task.WhenAny(decisionTask, Task.Delay(_reviewTimeout)).ConfigureAwait(false);
            if (completed != decisionTask)
            {
                await ReviewTimeoutAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
                return;
            }

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
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogInformation("Collective assembly: run {RunId} declined", context.CoordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // Post-approval: ONE merge -> ONE scribe -> complete.
    // -----------------------------------------------------------------------

    private async Task PollDeferredAssemblyReviewDecisionAsync(CoordinatorDispatchContext context, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _reviewGate.IsArmed(context.CoordinatorRunId))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            AssemblyReviewDecision? decision;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var row = await db.DeferredDecisions
                    .FirstOrDefaultAsync(d => d.RunId == context.CoordinatorRunId, ct)
                    .ConfigureAwait(false);
                if (row is null)
                    continue;

                decision = JsonSerializer.Deserialize<AssemblyReviewDecision>(row.DecisionJson, JsonDefaults.Options);
                var deleted = await db.DeferredDecisions
                    .Where(d => d.RunId == context.CoordinatorRunId)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
                if (deleted == 0 || decision is null)
                    continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Collective assembly: error polling deferred review decision for run {RunId}",
                    context.CoordinatorRunId);
                continue;
            }

            var result = _reviewGate.TrySubmit(context.CoordinatorRunId, context.SubmittingUser, decision);
            _logger.LogInformation(
                "Collective assembly: deferred review decision for run {RunId} applied with result {Result}",
                context.CoordinatorRunId, result);
            return;
        }
    }

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
            if (merge.Outcome == CollectiveMergeOutcome.Conflict || (merge.ConflictingFiles?.Count ?? 0) > 0)
            {
                await NeedsResolutionAsync(context, workPlanId, edges, mergeReason, new
                {
                    workPlanId,
                    reason = mergeReason,
                    conflictingFiles = merge.ConflictingFiles,
                    integrationBranch,
                }, ct).ConfigureAwait(false);
                return;
            }

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
            await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
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

        var scribeSucceeded = true;
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
            scribeSucceeded = false;
            _logger.LogWarning(ex, "Collective assembly: scribe pass failed for run {RunId} (non-fatal)",
                context.CoordinatorRunId);
            Emit(context.CoordinatorRunId, "run.scribe_failed", new
            {
                workPlanId,
                reason = ex.Message,
            });
        }

        if (scribeSucceeded)
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeCompleted, new { workPlanId });

        // ── Coordinator decision promotion ───────────────────────────────────────────────────────
        // The per-run Scribe auto-merges only learning/pattern/update entries; architectural and
        // scope entries are deliberately left for the Coordinator. Promote the still-pending ones
        // here so they become active decisions (visible in the UI and injected into agent context).
        // Best-effort and idempotent: a failure must not fail the already-merged assembly.
        await PromoteCoordinatorDecisionsAsync(context, ct).ConfigureAwait(false);

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

        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogInformation("Collective assembly complete for run {RunId}", context.CoordinatorRunId);
    }

    /// <summary>
    /// Deterministic backstop for the Coordinator's autonomous decision review: promotes every
    /// still-pending architectural/scope inbox entry for the run's project into an active decision,
    /// using the same mapping as the <c>/merge</c> endpoint. Best-effort and non-blocking — mirrors
    /// <see cref="PostRunScribeService"/>: any failure is logged and the run completes regardless.
    /// </summary>
    private async Task PromoteCoordinatorDecisionsAsync(CoordinatorDispatchContext context, CancellationToken ct)
    {
        var projectId = context.ProjectId?.Value.ToString();
        if (string.IsNullOrEmpty(projectId))
            return;

        try
        {
            if (!RunId.TryParse(context.CoordinatorRunId, out var parsedRunId))
                return;
            var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            if (run is null)
                return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var pending = (await db.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.Status == "pending"
                         && e.AgentName == "coordinator")
                .ToListAsync(ct).ConfigureAwait(false))
                .Where(e => e.CreatedAt >= run.StartedAt
                         && DecisionPromotion.CoordinatorReviewTypes.Contains(e.Type))
                .ToList();

            var now = DateTimeOffset.UtcNow;
            foreach (var entry in pending)
                await DecisionPromotion.PromoteEntry(db, entry, now, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var promoted = pending.Count;
            if (promoted > 0)
                _logger.LogInformation(
                    "Coordinator promoted {Count} run-scoped architectural/scope decision(s) for run {RunId}",
                    promoted, context.CoordinatorRunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coordinator decision promotion failed for run {RunId} (non-fatal)", context.CoordinatorRunId);
        }
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
        await ResetSubtasksToPendingAsync(rejection.SubtaskIds, decision.Feedback ?? string.Empty, ct).ConfigureAwait(false);
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
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogWarning("Collective assembly blocked for run {RunId}: {Reason}", context.CoordinatorRunId, reason);
    }

    private async Task RaiBlockAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string integrationBranch,
        CancellationToken ct)
    {
        var payload = new
        {
            workPlanId,
            reason = "rai_blocked",
            integrationBranch,
            requiresHumanOverride = true,
        };
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.RaiBlocked, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, "run.rai_blocked", payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.RaiBlocked, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, "rai_blocked", ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogWarning("Collective assembly RAI-blocked run {RunId}", context.CoordinatorRunId);
    }

    private async Task ReviewTimeoutAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, "run.review_timeout", new
        {
            workPlanId,
            timeoutSeconds = (int)_reviewTimeout.TotalSeconds,
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, "review_timeout", ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogWarning("Collective assembly review timed out for run {RunId}", context.CoordinatorRunId);
    }

    private async Task NeedsResolutionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string reason,
        object payload,
        CancellationToken ct)
    {
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.NeedsResolution, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.MergeConflicted, payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.NeedsResolution, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.MergeFailed, $"needs_resolution: {reason}", ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogWarning("Collective assembly needs resolution for run {RunId}: {Reason}",
            context.CoordinatorRunId, reason);
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
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
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

        // CRITICAL (orphan cleanup): when assembly blocks/fails (e.g. ineligible_subtasks, rai_blocked,
        // review_timeout) the coordinator run terminates but its AgentHost pod (2 CPU / 4 Gi) would
        // otherwise keep running and eventually exhaust the namespace CPU quota. Release it best-effort.
        await ReleaseAgentHostPodSafeAsync(coordinatorRunId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the AgentHost pod for <paramref name="runId"/> when running pod-per-run. Best-effort:
    /// logs and swallows exceptions so a release failure never disrupts terminalization. No-op when
    /// not in pod-per-run mode or no lifecycle is wired (in-api / non-Kubernetes).
    /// </summary>
    private async Task ReleaseAgentHostPodSafeAsync(string runId, CancellationToken ct)
    {
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun || string.IsNullOrEmpty(runId))
            return;

        try
        {
            await _podLifecycle.ReleaseAgentHostPodAsync(runId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "CoordinatorAssemblyService: AgentHost pod released for terminalized coordinator run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CoordinatorAssemblyService: failed to release AgentHost pod for run {RunId} (best-effort)",
                runId);
        }
    }

    private void Emit(string coordinatorRunId, string eventType, object payload) =>
        _streamStore.Get(coordinatorRunId)?.RecordNext(eventType, StampTimestamp(payload));

    /// <summary>
    /// Persists the coordinator run's in-memory assembly events to the RunEvents table, then marks
    /// the stream complete. Assembly events (including <c>coordinator.assembly_blocked</c>) otherwise
    /// live only in the evictable in-memory stream; once it is gone a page reload replays nothing, so
    /// the blocked/failed detail is lost. Best-effort: a persistence fault must not stop the stream
    /// from completing. Mirrors <see cref="RunWorkflowFactory.PersistRunEventsAsync"/>, inlined here
    /// to avoid a constructor dependency on the workflow factory.
    /// </summary>
    private async Task PersistAndCompleteStreamAsync(string coordinatorRunId)
    {
        try
        {
            var entry = _streamStore.Get(coordinatorRunId);
            var events = entry?.GetSnapshotSince(0).Events;
            if (events is { Count: > 0 })
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

                var existingSeqs = db.RunEvents
                    .Where(e => e.RunId == coordinatorRunId)
                    .Select(e => e.Sequence)
                    .ToHashSet();

                var toInsert = events
                    .Where(e => !existingSeqs.Contains(e.Sequence))
                    .Select(e => new RunEventRecord
                    {
                        RunId = coordinatorRunId,
                        Sequence = e.Sequence,
                        EventType = e.Type,
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(e.Payload),
                        CreatedAt = DateTime.UtcNow,
                    })
                    .ToList();

                if (toInsert.Count > 0)
                {
                    db.RunEvents.AddRange(toInsert);
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Collective assembly: failed to persist run events for {RunId}", coordinatorRunId);
        }
        finally
        {
            _streamStore.Complete(coordinatorRunId);
        }
    }

    // Adds a server-side wall-clock `timestamp_utc` (ISO-8601 "O") to every assembly event so the
    // frontend can derive live count-up timers for each stage (RAI, Review, Merge, Scribe) the same
    // way it does for subtask.* events. The payload members are already camelCase identifiers, so
    // serializing to a JsonObject preserves the exact keys the UI reads; the stamp survives SSE
    // replay/restart because it is persisted in the event payload (not the client receive time).
    private static System.Text.Json.Nodes.JsonObject StampTimestamp(object payload)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(payload) as System.Text.Json.Nodes.JsonObject
            ?? new System.Text.Json.Nodes.JsonObject();
        if (!node.ContainsKey("timestamp_utc"))
            node["timestamp_utc"] = DateTimeOffset.UtcNow.ToString("O");
        return node;
    }

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

    private async Task ResetSubtasksToPendingAsync(IReadOnlyCollection<int> subtaskIds, string feedback, CancellationToken ct)
    {
        if (subtaskIds.Count == 0) return;
        var guidance = BuildAssemblyFeedbackGuidance(feedback);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Subtasks
            .Where(s => subtaskIds.Contains(s.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SubtaskStatus.Pending)
                .SetProperty(s => s.RecoveryGuidance, guidance)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the guidance text written into a re-dispatched subtask's <c>RecoveryGuidance</c> when
    /// the collective assembly reviewer requested changes. Mirrors the pattern used by
    /// <see cref="CoordinatorSteeringService"/> for steering-driven recovery, adapted for the
    /// assembly feedback path. <see cref="CoordinatorDispatchService.ComposeChildTask"/> reads this
    /// field when composing the child's re-dispatch prompt so the child receives the reviewer's
    /// exact feedback and does not repeat the same output verbatim.
    /// </summary>
    private static string BuildAssemblyFeedbackGuidance(string feedback) =>
        $"Recovery guidance from the assembly reviewer: {feedback}\n\n" +
        "Context: The collective assembly reviewer requested changes to your output. " +
        "Re-do this work against the latest repository state and address the feedback above.";

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
