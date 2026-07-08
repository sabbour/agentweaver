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
using Agentweaver.Api.Workflows;
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

    /// <summary>Ensures the coordinator's final Scribe activity exists and runs for an already-terminal run.</summary>
    void EnsureFinalScribe(Run coordinatorRun);

    /// <summary>
    /// True when a collective-assembly loop is currently active IN THIS PROCESS for the run (the
    /// in-memory guard is populated). Lets the reconciler tell a legitimately in-flight assembly (e.g.
    /// awaiting the open human-review gate) from a genuinely orphaned one, so it never re-arms an
    /// already-owned run every sweep (the "already active; skipping" infinite loop).
    /// </summary>
    bool IsAssemblyActive(string coordinatorRunId);

    /// <summary>
    /// Escape hatch (reconciler-driven): terminalizes a run parked in <c>in_review</c> past the
    /// operator review timeout with no action, so it can never stay stuck forever. Fire-and-forget and
    /// idempotent (a no-op once the run is already terminal or no longer in review); reuses the same
    /// terminal path as the in-process review timeout.
    /// </summary>
    void AbandonStaleReview(CoordinatorDispatchContext context);

    /// <summary>
    /// Hard stop (reconciler-driven): terminalizes a run whose collective assembly could not be
    /// recovered after repeated re-arm attempts (e.g. a persistent git integration failure that fails
    /// every re-arm). Fire-and-forget and idempotent — marks the plan <c>assembly_failed</c> and the
    /// coordinator run failed with <paramref name="reason"/>, then closes the stream. Prevents the
    /// reconciler from re-arming a doomed assembly forever.
    /// </summary>
    void FailAssembly(CoordinatorDispatchContext context, string reason);
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
    private const string AssemblyScribeSubtaskId = "assembly-scribe";
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly CoordinatorAssemblyStore _assemblyStore;
    private readonly AssemblyReviewGate _reviewGate;
    private readonly ICollectiveAssemblyPipeline _pipeline;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IProjectStore? _projectStore;
    private readonly WorkflowRegistry? _workflowRegistry;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IKubernetesEnvironment? _k8sEnv;
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;
    private readonly CoordinatorSteeringWaitRegistry _steeringWaits;
    private readonly CoordinatorSteeringQueue _steeringQueue;
    private readonly ILogger<CoordinatorAssemblyService> _logger;
    private readonly CancellationToken _appStopping;
    private readonly TimeSpan _reviewTimeout;
    private readonly TimeSpan _steeringWaitTimeout;
    private readonly TimeSpan _assemblyLeaseStaleTtl;

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
        CoordinatorSteeringWaitRegistry? steeringWaits = null,
        IPodNameRegistry? podRegistry = null,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null,
        IKubernetesEnvironment? k8sEnv = null,
        IProjectStore? projectStore = null,
        WorkflowRegistry? workflowRegistry = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _assemblyStore = assemblyStore;
        _reviewGate = reviewGate;
        _pipeline = pipeline;
        _scopeFactory = scopeFactory;
        _serviceProvider = serviceProvider;
        _projectStore = projectStore;
        _workflowRegistry = workflowRegistry;
        _podRegistry = podRegistry;
        _k8sEnv = k8sEnv;
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
        _steeringWaits = steeringWaits ?? new CoordinatorSteeringWaitRegistry();
        _steeringQueue = new CoordinatorSteeringQueue(scopeFactory);
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
        var reviewTimeoutMinutes = configuration?.GetValue("Coordinator:AssemblyReviewTimeoutMinutes", 60.0) ?? 60.0;
        _reviewTimeout = TimeSpan.FromMinutes(Math.Max(1.0, reviewTimeoutMinutes));
        var steeringWaitSeconds = configuration?.GetValue<double?>("Coordinator:AssemblyBlockedSteeringTimeoutSeconds");
        var steeringWaitMinutes = configuration?.GetValue("Coordinator:AssemblyBlockedSteeringTimeoutMinutes", 10.0) ?? 10.0;
        _steeringWaitTimeout = steeringWaitSeconds is { } seconds
            ? TimeSpan.FromSeconds(Math.Max(0.1, seconds))
            : TimeSpan.FromMinutes(Math.Max(0.1, steeringWaitMinutes));
        // How long an `assembling` claim is considered fresh (owner alive). A second replica only
        // reclaims a stuck assembly after the owner has been silent this long — must comfortably exceed
        // a normal integration-branch build so a live merge is never stolen mid-flight (default 120 s).
        var assemblyLeaseSecs = configuration?.GetValue("Coordinator:AssemblyLeaseStaleTtlSeconds", 120) ?? 120;
        _assemblyLeaseStaleTtl = TimeSpan.FromSeconds(Math.Max(10, assemblyLeaseSecs));
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
            _logger.LogDebug(
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

    public void EnsureFinalScribe(Run coordinatorRun)
    {
        if (coordinatorRun.ParentRunId is not null
            || !string.Equals(coordinatorRun.AgentName, "Coordinator", StringComparison.Ordinal))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureFinalScribeAsync(coordinatorRun, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Coordinator final scribe recovery failed for run {RunId}",
                    coordinatorRun.Id);
            }
        }, _appStopping);
    }

    /// <inheritdoc />
    public bool IsAssemblyActive(string coordinatorRunId) =>
        !string.IsNullOrEmpty(coordinatorRunId) && _active.ContainsKey(coordinatorRunId);

    /// <inheritdoc />
    public void AbandonStaleReview(CoordinatorDispatchContext context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await AbandonStaleReviewAsync(context, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Collective assembly: failed to abandon stale in_review run {RunId}",
                    context.CoordinatorRunId);
            }
        }, _appStopping);
    }

    /// <summary>
    /// Terminalizes a run that has been parked in <c>in_review</c> past the operator review timeout.
    /// Verifies the plan is still <see cref="WorkPlanStatus.InReview"/> (idempotent — a no-op if the
    /// reviewer acted or another path already resolved it), then routes through the same terminal
    /// machinery as the in-process <see cref="ReviewTimeoutAsync"/> so the run reaches a clean terminal
    /// state (plan failed, coordinator run terminalized, scribe + stream completed, pod released).
    /// </summary>
    internal async Task AbandonStaleReviewAsync(CoordinatorDispatchContext context, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null)
            return;

        var (workPlanId, planStatus, _, edges) = plan.Value;
        if (planStatus != WorkPlanStatus.InReview)
            return; // reviewer acted or another path resolved it — nothing to abandon.

        _logger.LogWarning(
            "Collective assembly: run {RunId} parked in in_review with no operator action past the review timeout; abandoning",
            context.CoordinatorRunId);
        await AbandonReviewTerminalAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void FailAssembly(CoordinatorDispatchContext context, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await FailAssemblyAsync(context, reason, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Collective assembly: failed to terminalize exhausted assembly for run {RunId}",
                    context.CoordinatorRunId);
            }
        }, _appStopping);
    }

    /// <summary>
    /// Terminalizes a run whose assembly could not be recovered (reconciler re-arm cap reached). Skips
    /// if the plan already reached a terminal/parked state (idempotent). Marks the plan failed, emits
    /// the assembly-failed event + graph/topology, terminalizes the coordinator run, runs the scribe,
    /// and completes the stream.
    /// </summary>
    internal async Task FailAssemblyAsync(CoordinatorDispatchContext context, string reason, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null)
            return;

        var (workPlanId, planStatus, _, edges) = plan.Value;
        if (planStatus is WorkPlanStatus.Complete
            or WorkPlanStatus.AssemblyFailed
            or WorkPlanStatus.AssemblyDeclined)
            return; // already terminal — nothing to do.

        _logger.LogError(
            "Collective assembly: run {RunId} could not be recovered ({Reason}); terminalizing as failed",
            context.CoordinatorRunId, reason);

        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, reason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyFailed, new
        {
            workPlanId,
            reason,
            phase = "reconciler_rearm",
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, reason, ct).ConfigureAwait(false);
        await PreserveOrClearReviewGateAsync(context, workPlanId, reason, clearIfNoOpenGate: true, ct)
            .ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Failed.ToApiString(),
            mergeResult: reason,
            ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
    }

    /// <summary>
    /// When a coordinator run terminates in a failure state, an OPEN human-review gate must OUTLIVE the
    /// run: the human should always be able to view the assembled changes and complete their review even
    /// if the backend faulted (git-lock race, crash, pod eviction, reconciler re-arm exhaustion). If a
    /// review gate is still open (no decision submitted) it is preserved — marked <c>coordinator_failed</c>
    /// via <see cref="CoordinatorAssemblyReviewPersistence.MarkCoordinatorFailedAsync"/> and surfaced with
    /// <see cref="EventTypes.CoordinatorAssemblyReviewPreserved"/> (emitted LAST so the UI keeps the review
    /// visible instead of kicking the operator out). Only when there is NO open gate (already decided, or
    /// never armed) is the record cleared, and only if <paramref name="clearIfNoOpenGate"/> is set.
    /// </summary>
    private async Task PreserveOrClearReviewGateAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        string reason,
        bool clearIfNoOpenGate,
        CancellationToken ct)
    {
        var preserved = await CoordinatorAssemblyReviewPersistence.MarkCoordinatorFailedAsync(
            _scopeFactory, context.CoordinatorRunId, reason, ct).ConfigureAwait(false);
        if (preserved)
        {
            var record = await CoordinatorAssemblyReviewPersistence.GetAsync(
                _scopeFactory, context.CoordinatorRunId, ct).ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewPreserved, new
            {
                workPlanId,
                integrationBranch = record?.IntegrationBranch,
                treeHash = record?.AggregateTreeHash,
                reason,
            });
            _logger.LogWarning(
                "Collective assembly: run {RunId} failed while the review gate was open ({Reason}); "
                + "preserving the gate so the operator can still view the changes",
                context.CoordinatorRunId, reason);
            return;
        }

        if (clearIfNoOpenGate)
            await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Common terminal path for an abandoned/timed-out collective review: mark the plan failed, emit
    /// the timeout event + graph/topology, terminalize the coordinator run, run the scribe, and
    /// complete the stream. Shared by <see cref="ReviewTimeoutAsync"/> (in-process 60 min) and
    /// <see cref="AbandonStaleReviewAsync"/> (reconciler backstop, hours).
    /// </summary>
    private async Task AbandonReviewTerminalAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        const string timeoutReason = "review_timeout_abandoned";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, timeoutReason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, "run.review_timeout", new
        {
            workPlanId,
            timeoutSeconds = (int)_reviewTimeout.TotalSeconds,
            reason = timeoutReason,
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, timeoutReason, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Failed.ToApiString(),
            mergeResult: timeoutReason,
            ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
    }
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

        var (workPlanId, planStatus, subtasks, edges) = plan.Value;

        try
        {
            if (planStatus == WorkPlanStatus.AssemblyBlocked)
            {
                var blockedReason = await ResolveBlockedAssemblyReasonAsync(
                    context.CoordinatorRunId, fallback: null, ct).ConfigureAwait(false);

                if (CanRecoverBlockedAssemblyOnEligibility(blockedReason)
                    && await TryRecoverBlockedAssemblyIfEligibleAsync(
                        context, workPlanId, subtasks, edges, "assembly_blocked_eligible_recovered", ct)
                    .ConfigureAwait(false))
                    return;

                await WaitForBlockedAssemblySteeringAsync(
                    context, workPlanId, blockedReason, edges, ct).ConfigureAwait(false);
                return;
            }

            if (planStatus == WorkPlanStatus.InReview)
            {
                await ResumeInReviewAsync(context, workPlanId, subtasks, edges, ct).ConfigureAwait(false);
                return;
            }

            if (planStatus == WorkPlanStatus.Assembling)
            {
                // Cross-pod idempotency guard for the git integration merge. An `assembling` plan is
                // normally owned by a LIVE assembly loop; reclaim it here ONLY if the claim is stale
                // (owner likely dead). If it is fresh, another replica is actively building the
                // integration branch right now — bail so two pods never race the ref-lock files.
                var staleBefore = DateTimeOffset.UtcNow - _assemblyLeaseStaleTtl;
                if (!await _assemblyStore.TryReclaimStaleAssemblyAsync(workPlanId, staleBefore, ct).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Collective assembly: run {RunId} is already being assembled by a live owner (fresh claim); skipping to avoid a concurrent git merge",
                        context.CoordinatorRunId);
                    return;
                }
            }

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
        var ineligible = AssemblyPlanning.TerminalIneligibleSubtasks(statusById);
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
                    return new { id = s.Id, title = s.Title, status = s.Status, agent = s.AssignedAgent, recoveryGuidance = s.RecoveryGuidance };
                })
                .ToList();

            // Include the blocking subtask IDs in the reason so the coordinator run FailureReason
            // names WHICH subtasks blocked assembly (e.g. "assembly_blocked: ineligible_subtasks [47,48,49]").
            var ineligibleIdSummary = $"ineligible_subtasks [{string.Join(",", ineligible)}]";
            await BlockAsync(context, workPlanId, edges, ineligibleIdSummary, new
            {
                workPlanId,
                reason = "ineligible_subtasks",
                ineligibleSubtaskIds = ineligible,
                ineligibleSubtasks,
            }, ct).ConfigureAwait(false);
            return;
        }

        if (!AssemblyPlanning.AllEligible(statusById))
        {
            await AwaitMoreSubtasksAsync(context, workPlanId, edges, statusById, ct).ConfigureAwait(false);
            return;
        }

        var assemblyInputs = await BuildAssemblyInputsAsync(subtasks, edges, ct).ConfigureAwait(false);
        var branchesInOrder = assemblyInputs.BranchesInOrder;
        var touchedFilesBySubtask = assemblyInputs.TouchedFilesBySubtask;
        var includedSubtaskIds = assemblyInputs.IncludedSubtaskIds;

        // D1 — build the COMBINED integration branch.
        var integrationRequest = new CollectiveIntegrationRequest(
            context.RepositoryPath, context.OriginatingBranch, integrationBranch, branchesInOrder);
        IntegrationBranchResult integration;
        try
        {
            integration = await BuildIntegrationBranchWithRetryAsync(
                context, integrationRequest, ct).ConfigureAwait(false);
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

        foreach (var (branch, files) in integration.AutoResolutions)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorIntegrationConflictAutoResolved, new
            {
                workPlanId,
                conflictingBranch = branch,
                conflictingFiles = files,
                strategy = "accept_child",
            });
        }

        var aggregateDiff = integration.Diff ?? string.Empty;
        var aggregateTreeHash = integration.TreeHash ?? string.Empty;
        var assemblyGates = await ResolveAssemblyGatesAsync(workPlanId, ct).ConfigureAwait(false);

        foreach (var gate in assemblyGates)
        {
            if (gate.GateKind == "build-test")
            {
                await _assemblyStore.SetStageAsync(workPlanId, gate.StageId, ct).ConfigureAwait(false);
                await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
                {
                    workPlanId,
                    integrationBranch,
                    treeHash = aggregateTreeHash,
                    gateId = gate.Id,
                    gateKind = gate.GateKind,
                    hasChanges = integration.HasChanges,
                });

                var buildTest = await _pipeline.RunBuildTestAsync(
                    new CollectiveBuildTestRequest(
                        context.CoordinatorRunId,
                        context.RepositoryPath,
                        integrationBranch,
                        aggregateTreeHash,
                        aggregateDiff,
                        context.SubmittingUser,
                        gate.GraphNodeId,
                        gate.Label,
                        gate.AgentId),
                    ct).ConfigureAwait(false);

                if (!await ApplyAuthoredGateDecisionAsync(
                        context,
                        workPlanId,
                        edges,
                        touchedFilesBySubtask,
                        new AssemblyReviewDecision(
                            Approved: buildTest.Approved,
                            RequestChanges: buildTest.RequestChanges,
                            Feedback: buildTest.Feedback,
                            TargetFiles: null,
                            Reviewer: "build-test"),
                        ct).ConfigureAwait(false))
                    return;

                continue;
            }

            if (gate.GateKind == "rai")
            {
                await _assemblyStore.SetStageAsync(workPlanId, gate.StageId, ct).ConfigureAwait(false);
                await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyRaiStarted, new { workPlanId, integrationBranch, gateId = gate.Id });

                var rai = await _pipeline.RunRaiAsync(
                    new CollectiveRaiRequest(context.CoordinatorRunId, context.RepositoryPath, aggregateDiff, context.SubmittingUser), ct)
                    .ConfigureAwait(false);

                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyRaiCompleted, new
                {
                    workPlanId,
                    gateId = gate.Id,
                    raiSafetyFlagged = rai.SafetyFlagged,
                });

                if (rai.SafetyFlagged)
                {
                    await RaiBlockAsync(context, workPlanId, edges, integrationBranch, ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (gate.GateKind == "rubberduck")
            {
                await _assemblyStore.SetStageAsync(workPlanId, gate.StageId, ct).ConfigureAwait(false);
                await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
                {
                    workPlanId,
                    integrationBranch,
                    treeHash = aggregateTreeHash,
                    gateId = gate.Id,
                    gateKind = gate.GateKind,
                    hasChanges = integration.HasChanges,
                });

                var rubberduck = await _pipeline.RunRubberduckAsync(
                    new CollectiveRubberduckRequest(
                        context.CoordinatorRunId,
                        context.RepositoryPath,
                        aggregateDiff,
                        context.SubmittingUser,
                        gate.GraphNodeId,
                        gate.Label),
                    ct).ConfigureAwait(false);

                if (rubberduck.RequestChanges)
                {
                    await RequestChangesAsync(
                        context,
                        workPlanId,
                        edges,
                        new AssemblyReviewDecision(
                            Approved: false,
                            RequestChanges: true,
                            Feedback: rubberduck.Feedback,
                            TargetFiles: null,
                            Reviewer: "rubberduck"),
                        touchedFilesBySubtask,
                        ct).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (gate.GateKind == "human-review")
            {
                // ── ONE human review gate (D5) ───────────────────────────────────────────────────
                await _assemblyStore.SetStatusAndStageAsync(
                    workPlanId, WorkPlanStatus.InReview, gate.StageId, ct).ConfigureAwait(false);
                await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
                {
                    workPlanId,
                    integrationBranch,
                    treeHash = aggregateTreeHash,
                    includedSubtaskIds,
                    gateId = gate.Id,
                    gateKind = gate.GateKind,
                    hasChanges = integration.HasChanges,
                });
                await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
                    _scopeFactory,
                    context.CoordinatorRunId,
                    context.SubmittingUser,
                    integrationBranch,
                    aggregateTreeHash,
                    ct).ConfigureAwait(false);

                var decision = await AwaitReviewDecisionAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
                if (decision is null)
                    return;

                if (!await ApplyAuthoredGateDecisionAsync(
                        context,
                        workPlanId,
                        edges,
                        touchedFilesBySubtask,
                        decision,
                        ct).ConfigureAwait(false))
                    return;
            }
        }

        await CompleteAfterApprovalAsync(
            context, workPlanId, edges, integrationBranch, aggregateTreeHash, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Post-approval: ONE merge -> ONE scribe -> complete.
    // -----------------------------------------------------------------------

    private async Task<CoordinatorAssemblyInputs> BuildAssemblyInputsAsync(
        IReadOnlyCollection<Subtask> subtasks,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
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

        return new CoordinatorAssemblyInputs(branchesInOrder, touchedFilesBySubtask, includedSubtaskIds);
    }

    private async Task ResumeInReviewAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        List<Subtask> subtasks,
        List<(int, int)> edges,
        CancellationToken ct)
    {
        var persisted = await CoordinatorAssemblyReviewPersistence.GetAsync(
            _scopeFactory, context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (persisted is null
            || string.IsNullOrEmpty(persisted.IntegrationBranch)
            || string.IsNullOrEmpty(persisted.AggregateTreeHash))
        {
            await _assemblyStore.SetStatusAndStageAsync(
                workPlanId, WorkPlanStatus.AwaitingAssembly, null, ct).ConfigureAwait(false);
            await RunAssemblyCoreAsync(context, workPlanId, subtasks, edges, ct).ConfigureAwait(false);
            return;
        }

        var inputs = await BuildAssemblyInputsAsync(subtasks, edges, ct).ConfigureAwait(false);
        var decision = string.IsNullOrEmpty(persisted.DecisionJson)
            ? await AwaitReviewDecisionAsync(context, workPlanId, edges, ct).ConfigureAwait(false)
            : JsonSerializer.Deserialize<AssemblyReviewDecision>(persisted.DecisionJson, JsonDefaults.Options);

        if (decision is null)
            return;

        await ApplyReviewDecisionAsync(
            context,
            workPlanId,
            edges,
            persisted.IntegrationBranch,
            persisted.AggregateTreeHash,
            inputs.TouchedFilesBySubtask,
            decision,
            ct).ConfigureAwait(false);
    }

    private async Task<AssemblyReviewDecision?> AwaitReviewDecisionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        await MarkCoordinatorAwaitingReviewAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        var decisionTask = _reviewGate.ArmAsync(context.CoordinatorRunId, context.SubmittingUser, ct);
        _ = PollDeferredAssemblyReviewDecisionAsync(context, ct);
        try
        {
            // The human-review gate waits INDEFINITELY for the operator — an open gate is never
            // auto-failed on a wall-clock timeout. The wait ends only when a decision arrives (here or
            // via another replica, picked up by the deferred poller) or the app is stopping (ct). The
            // durable review record + the reconciler recover the gate across restarts, so parking here
            // indefinitely is safe and honors "let it wait for the human".
            var decision = await decisionTask.ConfigureAwait(false);
            await MarkCoordinatorInProgressAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
            return decision;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Collective assembly: review wait cancelled for run {RunId}; leaving in_review",
                context.CoordinatorRunId);
            return null;
        }
    }

    private async Task ApplyReviewDecisionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string integrationBranch,
        string aggregateTreeHash,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        AssemblyReviewDecision decision,
        CancellationToken ct)
    {
        if (decision.Approved)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewApproved, new
            {
                workPlanId,
                reviewer = decision.Reviewer,
            });
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

        const string declineReason = "assembly_declined";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyDeclined, declineReason, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
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
        await RunCoordinatorScribeAsync(context, workPlanId, terminalStatus: RunStatus.Declined.ToApiString(), mergeResult: declineReason, ct)
            .ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogInformation("Collective assembly: run {RunId} declined", context.CoordinatorRunId);
    }

    private async Task<bool> ApplyAuthoredGateDecisionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        AssemblyReviewDecision decision,
        CancellationToken ct)
    {
        if (decision.Approved)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewApproved, new
            {
                workPlanId,
                reviewer = decision.Reviewer,
            });
            await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
                .ConfigureAwait(false);
            await _assemblyStore.SetStatusAndStageAsync(
                workPlanId, WorkPlanStatus.Assembling, null, ct).ConfigureAwait(false);
            return true;
        }

        if (decision.RequestChanges)
        {
            await RequestChangesAsync(
                context, workPlanId, edges, decision, touchedFilesBySubtask, ct).ConfigureAwait(false);
            return false;
        }

        const string declineReason = "assembly_declined";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyDeclined, declineReason, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
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
        await RunCoordinatorScribeAsync(context, workPlanId, terminalStatus: RunStatus.Declined.ToApiString(), mergeResult: declineReason, ct)
            .ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogInformation("Collective assembly: run {RunId} declined", context.CoordinatorRunId);
        return false;
    }

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
                var row = await CoordinatorAssemblyReviewPersistence.GetAsync(
                    _scopeFactory, context.CoordinatorRunId, ct).ConfigureAwait(false);
                if (row is null || string.IsNullOrEmpty(row.DecisionJson))
                    continue;

                decision = JsonSerializer.Deserialize<AssemblyReviewDecision>(row.DecisionJson, JsonDefaults.Options);
                if (decision is null)
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

    private sealed record CoordinatorAssemblyInputs(
        List<string> BranchesInOrder,
        Dictionary<int, IReadOnlySet<string>> TouchedFilesBySubtask,
        List<int> IncludedSubtaskIds);

    private async Task<IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode>> ResolveAssemblyGatesAsync(
        int workPlanId,
        CancellationToken ct)
    {
        if (_projectStore is null || _workflowRegistry is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new { w.ProjectId, w.WorkflowId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (plan is null || !ProjectId.TryParse(plan.ProjectId, out var projectId))
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var project = await _projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var workflow = !string.IsNullOrWhiteSpace(plan.WorkflowId)
            ? _workflowRegistry.Get(project, plan.WorkflowId!)?.Definition
            : _workflowRegistry.ResolveDefault(project).Definition;
        workflow ??= _workflowRegistry.ResolveDefault(project).Definition;
        if (workflow is null)
            return CoordinatorGraphDescriptor.DefaultAssemblyGates;

        var gates = workflow.Nodes
            .Where(n => n.Type == WorkflowNodeType.Check || n.Type == WorkflowNodeType.BuildTest)
            .Select(n => (Node: n, GateKind: n.Type == WorkflowNodeType.BuildTest ? "build-test" : NodeClassifier.NormalizeGateKind(n)))
            .Where(x => x.GateKind is "build-test" or "rai" or "rubberduck" or "human-review")
            .Select(x => new CoordinatorGraphDescriptor.AssemblyGateNode(
                x.Node.Id,
                string.IsNullOrWhiteSpace(x.Node.Label) ? x.Node.Id : x.Node.Label,
                x.GateKind!,
                x.Node.Agent))
            .ToList();

        return gates;
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
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
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
            var terminalReason = $"assembly_merge_failed: {mergeReason}";
            await _assemblyStore.SetTerminalStatusAsync(
                workPlanId, WorkPlanStatus.AssemblyFailed, terminalReason, ct).ConfigureAwait(false);
            await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
            await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
                .ConfigureAwait(false);
            await TerminalizeCoordinatorRunAsync(
                context.CoordinatorRunId, RunStatus.MergeFailed, terminalReason, ct)
                .ConfigureAwait(false);
            await RunCoordinatorScribeAsync(
                context,
                workPlanId,
                terminalStatus: RunStatus.MergeFailed.ToApiString(),
                mergeResult: mergeReason,
                ct).ConfigureAwait(false);
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

        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Completed.ToApiString(),
            mergeResult: merge.CommitHash,
            ct).ConfigureAwait(false);

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

    private async Task RunCoordinatorScribeAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        string terminalStatus,
        string? mergeResult,
        CancellationToken ct)
    {
        await _assemblyStore.SetStageAsync(workPlanId, AssemblyStage.Scribe, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeStarted, new { workPlanId });

        var coordinatorRun = await TryGetCoordinatorRunAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (coordinatorRun is null)
            return;

        var (scribeRun, shouldExecute) = await EnsureScribeActivityAsync(
            coordinatorRun,
            terminalStatus,
            mergeResult,
            ct).ConfigureAwait(false);
        if (!shouldExecute || scribeRun is null)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeCompleted, new { workPlanId });
            return;
        }

        var scribeSucceeded = true;
        try
        {
            await _pipeline.RunScribeAsync(new CollectiveScribeRequest(
                context.CoordinatorRunId,
                context.ProjectId?.Value.ToString(),
                AgentName: "coordinator",
                SubmittingUser: coordinatorRun.SubmittingUser,
                context.RepositoryPath,
                ModelSource.GitHubCopilot.ToString(),
                ModelId: coordinatorRun.ModelId,
                RunStartedAt: coordinatorRun.StartedAt,
                TerminalStatus: terminalStatus,
                MergeResult: mergeResult), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scribeSucceeded = false;
            _logger.LogWarning(ex, "Collective assembly: scribe pass failed for run {RunId} (non-fatal)",
                context.CoordinatorRunId);
            Emit(context.CoordinatorRunId, "run.scribe_failed", new
            {
                workPlanId,
                reason = ex.Message,
            });
            await _runStore.TrySetTerminalStatusAsync(
                scribeRun.Id, RunStatus.Failed, DateTimeOffset.UtcNow, ex.Message, ct).ConfigureAwait(false);
        }

        if (scribeSucceeded)
        {
            await _runStore.TrySetTerminalStatusAsync(
                scribeRun.Id, RunStatus.Completed, DateTimeOffset.UtcNow, terminalStatus, ct).ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyScribeCompleted, new { workPlanId });
        }
    }

    private async Task EnsureFinalScribeAsync(Run coordinatorRun, CancellationToken ct)
    {
        var context = new CoordinatorDispatchContext(
            coordinatorRun.Id.ToString(),
            coordinatorRun.RepositoryPath,
            coordinatorRun.OriginatingBranch,
            coordinatorRun.SubmittingUser,
            coordinatorRun.ProjectId);

        var (scribeRun, shouldExecute) = await EnsureScribeActivityAsync(
            coordinatorRun,
            coordinatorRun.Status.ToApiString(),
            coordinatorRun.Result,
            ct).ConfigureAwait(false);
        if (!shouldExecute || scribeRun is null)
            return;

        try
        {
            await _pipeline.RunScribeAsync(new CollectiveScribeRequest(
                context.CoordinatorRunId,
                context.ProjectId?.Value.ToString(),
                AgentName: "coordinator",
                SubmittingUser: coordinatorRun.SubmittingUser,
                context.RepositoryPath,
                coordinatorRun.ModelSource.ToString(),
                coordinatorRun.ModelId,
                RunStartedAt: coordinatorRun.StartedAt,
                TerminalStatus: coordinatorRun.Status.ToApiString(),
                MergeResult: coordinatorRun.Result), ct).ConfigureAwait(false);

            await _runStore.TrySetTerminalStatusAsync(
                scribeRun.Id, RunStatus.Completed, DateTimeOffset.UtcNow, coordinatorRun.Result, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coordinator final scribe failed for run {RunId} (non-fatal)", coordinatorRun.Id);
            await _runStore.TrySetTerminalStatusAsync(
                scribeRun.Id, RunStatus.Failed, DateTimeOffset.UtcNow, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task<(Run? Run, bool ShouldExecute)> EnsureScribeActivityAsync(
        Run coordinatorRun,
        string terminalStatus,
        string? mergeResult,
        CancellationToken ct)
    {
        var existingChildren = await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString(), ct).ConfigureAwait(false);
        var existingCompleted = existingChildren.FirstOrDefault(r =>
            string.Equals(r.SubtaskId, AssemblyScribeSubtaskId, StringComparison.Ordinal)
            && string.Equals(r.AgentName, "Scribe", StringComparison.Ordinal)
            && r.Status == RunStatus.Completed);
        if (existingCompleted is not null)
            return (existingCompleted, false);

        var scribeRun = new Run
        {
            Id = RunId.New(),
            RepositoryPath = coordinatorRun.RepositoryPath,
            OriginatingBranch = coordinatorRun.OriginatingBranch,
            ModelSource = coordinatorRun.ModelSource,
            Task = $"Collective assembly scribe for {coordinatorRun.Id} ({terminalStatus})",
            SubmittingUser = coordinatorRun.SubmittingUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = coordinatorRun.ProjectId,
            ModelId = coordinatorRun.ModelId,
            AgentName = "Scribe",
            ParentRunId = coordinatorRun.Id.ToString(),
            SubtaskId = AssemblyScribeSubtaskId,
            Result = mergeResult,
        };

        await _runStore.InsertAsync(scribeRun, ct).ConfigureAwait(false);
        return (scribeRun, true);
    }

    private async Task<Run?> TryGetCoordinatorRunAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(coordinatorRunId, out var parsedRunId))
            return null;

        return await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
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
            redispatchedSubtaskIds = rejection.SubtaskIds,
            inferredFiles = rejection.InferredFiles,
            fellBackToAll = rejection.FellBackToAll,
            feedback = decision.Feedback,
        });

        // Reset the selected subtasks to pending (leave others' results intact); clear stage and move
        // the plan back to dispatching so the dispatch engine re-runs the affected frontier.
        await ResetSubtasksToPendingAsync(rejection.SubtaskIds, decision.Feedback ?? string.Empty, ct).ConfigureAwait(false);
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Dispatching, null, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
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

    private async Task<IntegrationBranchResult> BuildIntegrationBranchWithRetryAsync(
        CoordinatorDispatchContext context,
        CollectiveIntegrationRequest request,
        CancellationToken ct)
    {
        const int MaxAttempts = 3;
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return _pipeline.BuildIntegrationBranch(request);
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                var delayMs = (int)delay.TotalMilliseconds;
                _logger.LogWarning(
                    "Assembly git operation failed (attempt {Attempt}/3), retrying after {DelayMs}ms: {Message}",
                    attempt, delayMs, ex.Message);
                _pipeline.PrepareIntegrationBranchRetry(request);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
            catch (Exception ex)
            {
                var wrapped = new InvalidOperationException(
                    $"Assembly integration branch build failed after {attempt} attempts for run {context.CoordinatorRunId}: {ex.Message}",
                    ex);
                _logger.LogError(wrapped,
                    "Collective assembly: integration branch build failed after {AttemptCount} attempts for run {RunId}",
                    attempt, context.CoordinatorRunId);
                throw wrapped;
            }
        }

        throw new InvalidOperationException(
            $"Assembly integration branch build failed after {MaxAttempts} attempts for run {context.CoordinatorRunId}.");
    }

    private async Task BlockAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string reason,
        object payload,
        CancellationToken ct)
    {
        var statusReason = $"assembly_blocked: {reason}";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyBlocked, statusReason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyBlocked, edges, ct)
            .ConfigureAwait(false);
        _logger.LogWarning(
            "Collective assembly blocked for run {RunId}: {Reason}. Waiting for steering input.",
            context.CoordinatorRunId, reason);

        await WaitForBlockedAssemblySteeringAsync(context, workPlanId, reason, edges, ct).ConfigureAwait(false);
    }

    private async Task AwaitMoreSubtasksAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyDictionary<int, string> statusById,
        CancellationToken ct)
    {
        var notReady = AssemblyPlanning.IneligibleSubtasks(statusById);
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Dispatching, null, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorRecovered, new
        {
            reason = "assembly_subtasks_not_ready",
            workPlanId,
            notReadySubtaskIds = notReady,
        });
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.Dispatching, edges, ct)
            .ConfigureAwait(false);

        var dispatch = _serviceProvider.GetRequiredService<ICoordinatorDispatch>();
        dispatch.StartDispatch(context);
        _logger.LogInformation(
            "Collective assembly: run {RunId} saw non-terminal subtasks [{Ids}]; returning to dispatch instead of latching assembly_blocked.",
            context.CoordinatorRunId, string.Join(",", notReady));
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
        const string statusReason = "rai_blocked";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.RaiBlocked, statusReason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, "run.rai_blocked", payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.RaiBlocked, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, statusReason, ct).ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Failed.ToApiString(),
            mergeResult: statusReason,
            ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
        _logger.LogWarning("Collective assembly RAI-blocked run {RunId}", context.CoordinatorRunId);
    }

    private async Task NeedsResolutionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string reason,
        object payload,
        CancellationToken ct)
    {
        var statusReason = $"needs_resolution: {reason}";
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.NeedsResolution, statusReason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.MergeConflicted, payload);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.NeedsResolution, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.MergeFailed, statusReason, ct).ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.MergeFailed.ToApiString(),
            mergeResult: reason,
            ct).ConfigureAwait(false);
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
            await _assemblyStore.SetTerminalStatusAsync(
                workPlanId, WorkPlanStatus.AssemblyFailed, reason, ct).ConfigureAwait(false);
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
        // An unexpected fault must not close an open review gate — preserve it so the human can still
        // view the changes. Do NOT clear when absent/decided (this path never cleared historically).
        await PreserveOrClearReviewGateAsync(context, workPlanId, reason, clearIfNoOpenGate: false, ct)
            .ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Failed.ToApiString(),
            mergeResult: reason,
            ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
    }
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

    private async Task MarkCoordinatorAwaitingReviewAsync(string coordinatorRunId, CancellationToken ct)
    {
        await _runStore.UpdateStatusAsync(
            RunId.Parse(coordinatorRunId), RunStatus.AwaitingReview, endedAt: null, ct).ConfigureAwait(false);
        _streamStore.Get(coordinatorRunId)?.MarkAwaitingReview();
    }

    private async Task MarkCoordinatorInProgressAsync(string coordinatorRunId, CancellationToken ct)
    {
        await _runStore.UpdateStatusAsync(
            RunId.Parse(coordinatorRunId), RunStatus.InProgress, endedAt: null, ct).ConfigureAwait(false);
        _streamStore.Get(coordinatorRunId)?.ClearAwaitingReview();
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
        var state = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new
            {
                w.Status,
                w.AssemblyStage,
                w.AssemblyTerminalStage,
                w.AssemblyStatusReason,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var gates = await ResolveAssemblyGatesAsync(workPlanId, ct).ConfigureAwait(false);
        entry.RecordNext(EventTypes.CoordinatorGraph,
            CoordinatorGraphDescriptor.Build(
                coordinatorRunId,
                subtasks,
                deps,
                state?.AssemblyStage,
                state?.Status,
                state?.AssemblyTerminalStage,
                state?.AssemblyStatusReason,
                assemblyGates: gates));
    }

    private async Task EmitTopologyAsync(
        string coordinatorRunId, int workPlanId, string status,
        IReadOnlyCollection<(int, int)> edges, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is null) return;

        var subtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        var state = await _assemblyStore.GetAsync(workPlanId, ct).ConfigureAwait(false);
        entry.RecordNext(EventTypes.CoordinatorTopology, seq => CoordinatorTopology.BuildSnapshot(
            coordinatorRunId,
            workPlanId,
            status,
            subtasks,
            edges,
            seq,
            _podRegistry,
            _k8sEnv?.PodName,
            state?.AssemblyStage,
            state?.AssemblyTerminalStage,
            state?.AssemblyStatusReason));
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
                .SetProperty(s => s.ChildRunId, (string?)null)
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

    private async Task<(int WorkPlanId, string Status, List<Subtask> Subtasks, List<(int, int)> Edges)?> LoadPlanAsync(
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

        return (workPlan.Id, workPlan.Status, subtasks, edges);
    }

    private enum BlockedAssemblyOutcome
    {
        Terminalized,
        TimedOut,
        DispatchResumed,
        EligibilityRecovered,
    }

    private async Task WaitForBlockedAssemblySteeringAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        string? reason,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        reason = await ResolveBlockedAssemblyReasonAsync(
            context.CoordinatorRunId, reason, ct).ConfigureAwait(false);

        var outcome = await WaitForBlockedAssemblyOutcomeAsync(
            context.CoordinatorRunId, workPlanId, reason, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case BlockedAssemblyOutcome.EligibilityRecovered:
                var recoveredSubtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
                await TryRecoverBlockedAssemblyIfEligibleAsync(
                    context, workPlanId, recoveredSubtasks, edges.ToList(), "assembly_blocked_eligible_recovered", ct)
                    .ConfigureAwait(false);
                return;

            case BlockedAssemblyOutcome.DispatchResumed:
                _logger.LogInformation(
                    "Collective assembly wait exited for run {RunId}: steering resumed dispatch",
                    context.CoordinatorRunId);
                return;

            case BlockedAssemblyOutcome.TimedOut:
                await FailBlockedAssemblyAsync(
                    context,
                    workPlanId,
                    edges,
                    FormatAssemblyBlockedReason(reason ?? "awaiting_steering_timeout"),
                    ct).ConfigureAwait(false);
                return;

            default:
                await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
                return;
        }
    }

    private async Task<BlockedAssemblyOutcome> WaitForBlockedAssemblyOutcomeAsync(
        string coordinatorRunId,
        int workPlanId,
        string? reason,
        CancellationToken ct)
    {
        var waitUntil = DateTimeOffset.UtcNow + _steeringWaitTimeout;
        var waitVersion = _steeringWaits.GetVersion(coordinatorRunId);
        var recoverOnEligibility = CanRecoverBlockedAssemblyOnEligibility(reason);

        while (!ct.IsCancellationRequested)
        {
            var runStatus = await GetCoordinatorRunStatusAsync(coordinatorRunId, ct).ConfigureAwait(false);
            if (runStatus is RunStatus.Completed or RunStatus.Failed or RunStatus.Merged or RunStatus.Declined or RunStatus.MergeFailed)
                return BlockedAssemblyOutcome.Terminalized;

            var planStatus = await GetWorkPlanStatusAsync(workPlanId, ct).ConfigureAwait(false);
            if (planStatus != WorkPlanStatus.AssemblyBlocked)
                return BlockedAssemblyOutcome.DispatchResumed;

            if (recoverOnEligibility && await AreSubtasksAssemblyEligibleAsync(workPlanId, ct).ConfigureAwait(false))
                return BlockedAssemblyOutcome.EligibilityRecovered;

            var send = await _steeringQueue.TryTakeAssemblySendAsync(coordinatorRunId, ct).ConfigureAwait(false);
            if (send is not null)
            {
                await MarkSteeringAppliedAsync(send.DirectiveId, ct).ConfigureAwait(false);
                EmitSteering(coordinatorRunId, send, SteeringStatus.Applied);
                Emit(coordinatorRunId, EventTypes.CoordinatorRecovered, new
                {
                    reason = "assembly_blocked_send_acknowledged",
                    workPlanId,
                    directiveId = send.DirectiveId,
                });
                continue;
            }

            var remaining = waitUntil - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return BlockedAssemblyOutcome.TimedOut;

            waitVersion = await _steeringWaits.WaitForSignalAsync(
                coordinatorRunId,
                waitVersion,
                remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),
                ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException(ct);
    }

    private static bool CanRecoverBlockedAssemblyOnEligibility(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && reason.Contains("ineligible_subtasks", StringComparison.Ordinal);

    private static string FormatAssemblyBlockedReason(string reason) =>
        reason.StartsWith("assembly_blocked:", StringComparison.Ordinal)
            ? reason
            : $"assembly_blocked: {reason}";

    private async Task<string?> ResolveBlockedAssemblyReasonAsync(
        string coordinatorRunId,
        string? fallback,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var persistedReason = await db.WorkPlans.AsNoTracking()
                .Where(w => w.CoordinatorRunId == coordinatorRunId)
                .Select(w => w.AssemblyStatusReason)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(persistedReason))
                return persistedReason;

            var payload = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == coordinatorRunId && e.EventType == EventTypes.CoordinatorAssemblyBlocked)
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.PayloadJson)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Collective assembly: failed to resolve assembly_blocked reason for run {RunId}",
                coordinatorRunId);
            return null;
        }
    }

    private async Task FailBlockedAssemblyAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string reason,
        CancellationToken ct)
    {
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, reason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyFailed, new
        {
            workPlanId,
            reason,
            phase = "assembly_blocked",
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyFailed, edges, ct)
            .ConfigureAwait(false);
        await TerminalizeCoordinatorRunAsync(
            context.CoordinatorRunId, RunStatus.Failed, reason, ct).ConfigureAwait(false);
        await PreserveOrClearReviewGateAsync(context, workPlanId, reason, clearIfNoOpenGate: true, ct)
            .ConfigureAwait(false);
        await RunCoordinatorScribeAsync(
            context,
            workPlanId,
            terminalStatus: RunStatus.Failed.ToApiString(),
            mergeResult: reason,
            ct).ConfigureAwait(false);
        await PersistAndCompleteStreamAsync(context.CoordinatorRunId).ConfigureAwait(false);
    }

    private async Task<bool> TryRecoverBlockedAssemblyIfEligibleAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        List<Subtask> subtasks,
        List<(int, int)> edges,
        string reason,
        CancellationToken ct)
    {
        var statusById = subtasks.ToDictionary(s => s.Id, s => s.Status);
        if (!AssemblyPlanning.AllEligible(statusById))
            return false;

        if (!await _assemblyStore.TryResetBlockedAssemblyAsync(workPlanId, ct).ConfigureAwait(false))
            return false;

        await MarkCoordinatorInProgressAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorRecovered, new
        {
            reason,
            workPlanId,
        });
        _logger.LogInformation(
            "Collective assembly: run {RunId} cleared stale assembly_blocked state because all subtasks are now eligible.",
            context.CoordinatorRunId);

        var retrySubtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        await RunAssemblyCoreAsync(context, workPlanId, retrySubtasks, edges, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> AreSubtasksAssemblyEligibleAsync(int workPlanId, CancellationToken ct)
    {
        var subtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        return AssemblyPlanning.AllEligible(subtasks.ToDictionary(s => s.Id, s => s.Status));
    }

    private async Task<string?> GetWorkPlanStatusAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => w.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkSteeringAppliedAsync(int directiveId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.SteeringDirectives.FirstOrDefaultAsync(d => d.Id == directiveId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = SteeringStatus.Applied;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void EmitSteering(string coordinatorRunId, QueuedSteering directive, string status)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        entry?.RecordNext(EventTypes.CoordinatorSteering, CoordinatorSteeringEvent.Payload(
            directive.DirectiveId, directive.Kind, directive.TargetChildRunId, status, directive.Instruction));
    }

    private async Task<RunStatus?> GetCoordinatorRunStatusAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(coordinatorRunId, out var runId))
            return null;

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        return run?.Status;
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
