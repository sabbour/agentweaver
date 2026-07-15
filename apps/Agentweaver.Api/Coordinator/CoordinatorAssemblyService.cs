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
using Agentweaver.Api.Sandbox.Preview;
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
    internal const string AssemblyScribeSubtaskId = "assembly-scribe";
    internal const int DefaultFinalScribeMaxConcurrency = 2;
    internal const int DefaultFinalScribeMaxAttempts = 3;
    private const string FinalScribeMaxConcurrencyConfigurationKey = "Coordinator:FinalScribeMaxConcurrency";
    private const string FinalScribeMaxAttemptsConfigurationKey = "Coordinator:FinalScribeMaxAttempts";
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
    private readonly Preview.PreviewStep? _previewStep;
    private readonly WorktreeManager? _worktreeManager;
    private readonly IntegrationBuildLock? _integrationBuildLock;
    private readonly TimeSpan _integrationBuildLockTimeout;
    private readonly ILogger<CoordinatorAssemblyService> _logger;
    private readonly CancellationToken _appStopping;
    private readonly string _myPodId;
    private readonly TimeSpan _leaseHeartbeatInterval;
    private readonly TimeSpan _reviewTimeout;
    private readonly TimeSpan _steeringWaitTimeout;
    private readonly TimeSpan _assemblyLeaseStaleTtl;
    private readonly SemaphoreSlim _finalScribeConcurrency;
    private readonly int _finalScribeMaxAttempts;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _finalScribeAdmissions = new();

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
        WorkflowRegistry? workflowRegistry = null,
        Preview.PreviewStep? previewStep = null,
        WorktreeManager? worktreeManager = null,
        IntegrationBuildLock? integrationBuildLock = null)
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
        _previewStep = previewStep;
        _worktreeManager = worktreeManager;
        _integrationBuildLock = integrationBuildLock;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
        // #239 root-cause: renew the per-run assembly lease on a timer for the WHOLE assembly lifecycle
        // (Assembling/AssemblySteering routinely run >120 s), which the Dispatching-only heartbeat in
        // CoordinatorDispatchService never covers. Reuse the SAME config key + pod-id resolution as the
        // dispatch heartbeat (no new config key) so both loops keep the lease fresh identically.
        var heartbeatSecs = configuration?.GetValue("Coordinator:PodLeaseHeartbeatSeconds", 30) ?? 30;
        _leaseHeartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, heartbeatSecs));
        _myPodId = configuration?.GetValue<string>("App:PodId")
                   ?? Environment.GetEnvironmentVariable("HOSTNAME")
                   ?? Environment.MachineName;
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
        var lockTimeoutSecs = configuration?.GetValue("Coordinator:IntegrationBuildLockAcquireTimeoutSeconds", 120) ?? 120;
        _integrationBuildLockTimeout = TimeSpan.FromSeconds(Math.Max(1, lockTimeoutSecs));
        var finalScribeMaxConcurrency = configuration?.GetValue(
            FinalScribeMaxConcurrencyConfigurationKey,
            DefaultFinalScribeMaxConcurrency) ?? DefaultFinalScribeMaxConcurrency;
        finalScribeMaxConcurrency = Math.Max(1, finalScribeMaxConcurrency);
        _finalScribeConcurrency = new SemaphoreSlim(
            finalScribeMaxConcurrency,
            finalScribeMaxConcurrency);
        _finalScribeMaxAttempts = GetFinalScribeMaxAttempts(configuration);
    }

    /// <summary>The integration branch name (D1) derived from the coordinator run id.</summary>
    public static string IntegrationBranchName(string coordinatorRunId) =>
        $"agentweaver/integration/{coordinatorRunId}";

    /// <summary>
    /// spec-006 §3.2 / focus item 3: decides whether the deterministic preview step runs after
    /// build-test. The step is ALWAYS the behavior (no feature flag) — it runs whenever it is wired
    /// and the build-test verdict is APPROVED or REQUEST_CHANGES. A DECLINED verdict is
    /// <c>CollectiveGateDecision(Approved:false, RequestChanges:false)</c> — that run is about to
    /// terminate as <c>assembly_declined</c>, so provisioning a live preview process + Gateway
    /// HTTPRoute for a soon-torn-down assembly would be wasted work; the step is skipped. (Preview
    /// infra unavailability is a further behavioral self-skip inside PreviewStep itself, emitting
    /// <c>preview_skipped(preview_infra_unavailable)</c>.)
    /// </summary>
    internal static bool ShouldRunDeterministicPreviewStep(
        bool hasStep, CollectiveGateDecision buildTest)
    {
        if (!hasStep)
            return false;

        // Run for APPROVED or REQUEST_CHANGES; skip the terminating DECLINED verdict.
        return buildTest.Approved || buildTest.RequestChanges;
    }

    internal static async Task RunPreviewStepDefensivelyAsync(
        Func<Task> runPreview,
        string runId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await runPreview().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PreviewStep self-contains preview failures; any leaked non-caller cancellation or other
            // error must still never block human review.
            logger.LogWarning(ex,
                "Deterministic preview step threw for coordinator run {RunId}; proceeding with review.",
                runId);
        }
    }

    /// <summary>
    /// Returns true when two subtasks are likely to conflict in the shared orchestration worktree
    /// and must therefore run serially rather than in parallel.
    ///
    /// <para>Conflict rules (tri-state, #261):</para>
    /// <list type="bullet">
    /// <item>If either subtask's <see cref="Subtask.DeclaredOutputPathsJson"/> is malformed/missing/
    ///   wrong-shape (<see cref="CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid"/>),
    ///   the pair is conservatively assumed to conflict (safe default — nothing reliable was
    ///   declared).</item>
    /// <item>If either subtask genuinely declared NO outputs
    ///   (<see cref="CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty"/>, a
    ///   literal <c>[]</c>), the pair does NOT conflict — that subtask writes nothing, so there is
    ///   nothing for the other side's outputs to collide with.</item>
    /// <item>If both declare file-path tokens, they conflict when any token from one subtask
    ///   suffix-matches or filename-matches a token from the other (see
    ///   <see cref="CoordinatorOrchestratorExecutor.DeclaredOutputPathsMatch"/>).</item>
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
        // short-circuit on isolation here; every pair flows through structured output matching so
        // mislabeled writers are still scheduled serially when their declared outputs overlap.
        var parsed1 = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(subtask1.DeclaredOutputPathsJson);
        var parsed2 = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(subtask2.DeclaredOutputPathsJson);

        // Either side's declaration is untrustworthy (malformed/missing/wrong-shape) → conservatively
        // conflict, since we cannot rely on what it did or didn't declare (#261).
        if (parsed1.State == CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid
            || parsed2.State == CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid)
            return true;

        // A genuinely empty declaration (`[]`) means that subtask writes no files, so it cannot
        // collide with anything the other subtask writes — even if the other side declares paths
        // (#261: previously this collapsed into the same "conflict with everything" bucket as Invalid).
        if (parsed1.State == CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty
            || parsed2.State == CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty)
            return false;

        // Both sides declared concrete paths — check for file-path overlap using the shared matcher
        // (also used by D6 rejection routing and the persisted dependency-edge builder).
        foreach (var f1 in parsed1.Paths)
            foreach (var f2 in parsed2.Paths)
                if (CoordinatorOrchestratorExecutor.DeclaredOutputPathsMatch(f1, f2))
                    return true;

        return false;
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
            // #239 root-cause: a dedicated per-run assembly lease heartbeat renews WorkPlans.UpdatedAt
            // on a timer for the WHOLE assembly lifecycle (Assembling/AssemblySteering can routinely
            // run >120 s with nothing else renewing the lease), so a healthy owner's lease never goes
            // stale and a peer's CoordinatorReconciler never reclaims it mid-assembly. Linked to
            // app-stop; cancelled + awaited in the finally so it never outlives the assembly loop.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
            Task? heartbeat = null;
            try
            {
                var workPlanId = await ResolveWorkPlanIdAsync(context.CoordinatorRunId, _appStopping)
                    .ConfigureAwait(false);
                if (workPlanId is { } planId)
                    heartbeat = RunAssemblyLeaseHeartbeatAsync(
                        planId, context.CoordinatorRunId, heartbeatCts.Token);

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
                heartbeatCts.Cancel();
                if (heartbeat is not null)
                {
                    try
                    {
                        await heartbeat.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Heartbeat cancelled at hand-off/shutdown — a clean stop.
                    }
                }
                _active.TryRemove(context.CoordinatorRunId, out _);
            }
        }, _appStopping);
    }

    /// <summary>
    /// Resolves the work plan id for a coordinator run via its OWN DI scope + context (never a caller's
    /// context). Used by <see cref="StartAssembly"/> to key the per-run assembly lease heartbeat.
    /// </summary>
    private async Task<int?> ResolveWorkPlanIdAsync(string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(w => w.CoordinatorRunId == coordinatorRunId)
            .Select(w => (int?)w.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    internal enum AssemblyLeaseTick { Renewed, Idle, PeerOwned }

    /// <summary>
    /// One per-run ASSEMBLY lease heartbeat tick (#239 root-cause). Renews <c>WorkPlans.UpdatedAt</c>
    /// with an OWNERSHIP-and-status keyed UPDATE
    /// (<c>WHERE Id=@planId AND CoordinatorPodId=@myPodId AND Status IN (assembling, assembly_steering)</c>)
    /// so a healthy owner's lease stays fresh through the long assembly phases and a peer reconciler
    /// never reclaims it. Uses its OWN DI scope + <see cref="MemoryDbContext"/> per tick (NEVER the
    /// assembly loop's context, which is not thread-safe).
    ///
    /// <para>If a row is updated → <see cref="AssemblyLeaseTick.Renewed"/>. Otherwise re-read the owner:
    /// a PEER (non-null and != this pod) → <see cref="AssemblyLeaseTick.PeerOwned"/> (stop heartbeating).
    /// Still this pod, the row is gone, or the status is transiently outside the assembling set (e.g. a
    /// multi-phase lifecycle briefly in <c>in_review</c>) → <see cref="AssemblyLeaseTick.Idle"/>: skip
    /// this tick but KEEP ticking, because the plan may re-enter <c>assembling</c> later in the run.</para>
    ///
    /// <para>The set is EXACTLY {assembling, assembly_steering}. <c>in_review</c> is deliberately
    /// excluded so this heartbeat never masks <c>TryAbandonStaleReviewAsync</c>'s 24 h idle backstop
    /// (in_review is already cross-pod-protected by the durable pending-gate check);
    /// awaiting_assembly/assembly_blocked are excluded because their reclaim is not staleTtl-gated.</para>
    /// </summary>
    internal async Task<AssemblyLeaseTick> AssemblyHeartbeatTickAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Equality-only WHERE + SetProperty(UpdatedAt): a single ExecuteUpdateAsync translates on BOTH
        // SQLite and Postgres (no DateTimeOffset comparison in the predicate), so no IsSqlite() raw-SQL
        // branch is needed (mirrors CoordinatorDispatchService.HeartbeatTickAsync).
        var renewed = await db.WorkPlans
            .Where(w => w.Id == workPlanId
                     && w.CoordinatorPodId == _myPodId
                     && (w.Status == WorkPlanStatus.Assembling
                      || w.Status == WorkPlanStatus.AssemblySteering))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        if (renewed > 0)
            return AssemblyLeaseTick.Renewed;

        var owner = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => w.CoordinatorPodId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (owner is not null && !string.Equals(owner, _myPodId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Assembly lease for plan {PlanId} is owned by peer pod {Owner} (was {MyPod}); stopping assembly heartbeat",
                workPlanId, owner, _myPodId);
            return AssemblyLeaseTick.PeerOwned;
        }

        // Still this pod (status transiently outside the assembling set) or the row is gone — skip this
        // tick but keep ticking; the multi-phase lifecycle may re-enter assembling later.
        return AssemblyLeaseTick.Idle;
    }

    /// <summary>
    /// Renews the per-run assembly lease every <see cref="_leaseHeartbeatInterval"/> for the life of an
    /// assembly loop (#239 root-cause). Uses its OWN DI scope + <see cref="MemoryDbContext"/> per tick.
    /// Stops on <paramref name="stopToken"/> (normal hand-off / shutdown) or when a tick reports a PEER
    /// now owns the row. A transient per-tick error is NON-fatal: it is logged and the loop keeps
    /// heartbeating on the next interval (a single blip must never stop renewals and let the lease go
    /// stale). Mirrors <see cref="CoordinatorDispatchService.RunLeaseHeartbeatAsync"/>.
    /// </summary>
    internal async Task RunAssemblyLeaseHeartbeatAsync(
        int workPlanId, string coordinatorRunId, CancellationToken stopToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_leaseHeartbeatInterval);
            while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false))
            {
                try
                {
                    var tick = await AssemblyHeartbeatTickAsync(workPlanId, stopToken).ConfigureAwait(false);
                    if (tick == AssemblyLeaseTick.PeerOwned)
                        return; // A peer owns the row — stop renewing (this owner is being superseded).
                }
                catch (OperationCanceledException)
                {
                    throw; // Cancellation (app-stop / assembly hand-off) is a clean stop, not a blip.
                }
                catch (Exception ex)
                {
                    // Transient per-tick DB/SMB blip: log and keep heartbeating on the next interval. A
                    // single failed tick must NOT stop renewals — that would defeat the lease/fencing net.
                    _logger.LogWarning(ex,
                        "Assembly lease heartbeat tick for run {RunId} (plan {PlanId}) failed transiently; continuing",
                        coordinatorRunId, workPlanId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopToken cancelled: normal teardown at assembly hand-off or app shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Assembly lease heartbeat for run {RunId} (plan {PlanId}) stopped on an unexpected error",
                coordinatorRunId, workPlanId);
        }
    }

    public void EnsureFinalScribe(Run coordinatorRun)
    {
        if (coordinatorRun.ParentRunId is not null
            || !string.Equals(coordinatorRun.AgentName, "Coordinator", StringComparison.Ordinal))
            return;

        var coordinatorRunId = coordinatorRun.Id.ToString();
        if (!_finalScribeAdmissions.TryAdd(coordinatorRunId, 0))
        {
            _logger.LogDebug(
                "Coordinator final scribe already admitted for run {RunId}; skipping",
                coordinatorRun.Id);
            return;
        }

        _ = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await _finalScribeConcurrency.WaitAsync(_appStopping).ConfigureAwait(false);
                entered = true;
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
            finally
            {
                if (entered)
                    _finalScribeConcurrency.Release();
                _finalScribeAdmissions.TryRemove(coordinatorRunId, out _);
            }
        });
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

            if (planStatus == WorkPlanStatus.AssemblySteering)
            {
                // UNIFIED AUTONOMOUS STEERING (rev8 §3b/§3c, RD#3/CR#1) — the restart-router routes an
                // AssemblySteering plan here (ReArmAssembly). The AwaitingAssembly-scoped CAS in
                // TryStartAssemblyAsync would otherwise DEAD-END on this status, permanently wedging the
                // run mid-steering. Reclaim the stale decision lease back to awaiting_assembly ONLY if it
                // is stale (owner likely dead); if it is fresh, a live decider on another replica owns it
                // — bail. The single reclaim WINNER also returns this run's stale `relayed` directives to
                // `queued` in the SAME step (claim-durability §3c), then falls through to
                // RunAssemblyCoreAsync whose TryStartAssemblyAsync re-establishes the claim and
                // DriveOutstandingSteeringExecutionAsync re-drives the outstanding directive.
                var staleBefore = DateTimeOffset.UtcNow - _assemblyLeaseStaleTtl;
                if (!await _assemblyStore.TryReclaimStaleAssemblySteeringAsync(workPlanId, staleBefore, ct)
                        .ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Collective assembly: run {RunId} steering decision is owned by a live decider (fresh lease); skipping",
                        context.CoordinatorRunId);
                    return;
                }
                var steering = _serviceProvider.GetRequiredService<CoordinatorSteeringService>();
                await steering.ReclaimStaleRelayedDirectivesAsync(
                    context.CoordinatorRunId, staleBefore, ct).ConfigureAwait(false);
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

        // UNIFIED AUTONOMOUS STEERING (rev8 §3d, recovery): before doing any assembly work, drive any
        // outstanding decided/executing steering directive to completion. On the normal path after an
        // in-place (A) resume this advances the directive to `applied` once the durable effect marker is
        // confirmed; on crash recovery it re-drives the in-place resume exactly once (and returns true,
        // aborting this pass because the plan is now dispatching again).
        if (await DriveOutstandingSteeringExecutionAsync(context, workPlanId, edges, ct).ConfigureAwait(false))
            return;

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

        var assemblyInputs = await BuildAssemblyInputsAsync(context, subtasks, edges, ct).ConfigureAwait(false);
        var branchesInOrder = assemblyInputs.BranchesInOrder;
        var touchedFilesBySubtask = assemblyInputs.TouchedFilesBySubtask;
        var includedSubtaskIds = assemblyInputs.IncludedSubtaskIds;

        // D1 — build the COMBINED integration branch.
        var integrationRequest = new CollectiveIntegrationRequest(
            context.RepositoryPath, context.OriginatingBranch, integrationBranch, branchesInOrder);
        IntegrationBranchResult integration;
        try
        {
            // Cross-process serialization (issue #218): take the per-project integration-build lock (repo
            // granularity, NOT per-run) around the final integration build so it never races a dependency-base
            // rebuild or a peer assembly on the shared /workspace/{projectId}/.git repo. If a peer holds it past
            // the timeout, proceed anyway — BuildIntegrationBranchWithRetryAsync retries on any residual
            // LockedFileException as the backstop, so the mandatory final build is never skipped.
            var lockKey = IntegrationBuildLock.ResolveProjectKey(context.ProjectId?.ToString(), context.RepositoryPath);
            await using var projectLock = _integrationBuildLock is null
                ? null
                : await _integrationBuildLock.TryAcquireAsync(lockKey, _integrationBuildLockTimeout, ct).ConfigureAwait(false);
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

        // #236: the assembly-gate RAI + rubber-duck reviewers must be able to READ the assembled
        // integration files host-side (raw bytes, line endings, integration state) — not just the
        // aggregate diff text. Provision ONE detached reviewer worktree at the integration branch,
        // reusing the deterministic Build/Test worktree name so (a) Build/Test destructively recreates
        // it fresh when it runs — no reviewer-write bleed into Build/Test — and (b) the existing
        // CleanupBuildTestResourcesAsync / TerminalizeCoordinatorRunAsync teardown removes it (no extra
        // cleanup wiring). Skipped for empty-diff assemblies: the reviewers early-return approved
        // without touching a worktree, matching the HasChanges guard here.
        var reviewerWorktreePath = string.Empty;
        if (integration.HasChanges)
        {
            reviewerWorktreePath = _pipeline.PrepareReviewerWorktree(
                context.CoordinatorRunId, context.RepositoryPath, integrationBranch);
        }

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

                CollectiveGateDecision buildTest;
                try
                {
                    await EnsurePreviewApplicabilityRecordedAsync(
                        context.CoordinatorRunId,
                        workPlanId,
                        aggregateTreeHash,
                        aggregateDiff,
                        ct).ConfigureAwait(false);

                    buildTest = await _pipeline.RunBuildTestAsync(
                        new CollectiveBuildTestRequest(
                            context.CoordinatorRunId,
                            context.ProjectId?.Value.ToString(),
                            context.RepositoryPath,
                            integrationBranch,
                            aggregateTreeHash,
                            aggregateDiff,
                            context.SubmittingUser,
                            gate.GraphNodeId,
                            gate.Label,
                            gate.AgentId),
                        ct).ConfigureAwait(false);
                }
                catch (CollectiveBuildTestInfrastructureException ex)
                {
                    await ParkBuildTestInfrastructureFailureAsync(
                        context, workPlanId, edges, ex, ct).ConfigureAwait(false);
                    return;
                }

                // spec-006 §3.2: deterministic, platform-owned preview step. Runs AFTER build-test
                // for an APPROVED or REQUEST_CHANGES verdict and BEFORE the authored gate decision /
                // human review, on the SAME retained coordinator pod + detached worktree. It is the
                // SINGLE emitter of the terminal preview outcome; a preview failure NEVER blocks review.
                // Skipped on a DECLINED verdict (see ShouldRunDeterministicPreviewStep).
                if (ShouldRunDeterministicPreviewStep(_previewStep is not null, buildTest))
                {
                    await RunPreviewStepDefensivelyAsync(
                        () => _previewStep!.RunAsync(
                            new Preview.PreviewStepRequest(
                                RunId: context.CoordinatorRunId,
                                WorkPlanId: workPlanId,
                                TreeHash: aggregateTreeHash,
                                WorktreePath: _pipeline.GetBuildTestWorktreePath(context.CoordinatorRunId),
                                SubmittingUser: context.SubmittingUser,
                                ExecutionWorkspacePath: _podRegistry?.TryGetEffectiveWorkingDirectory(
                                    context.CoordinatorRunId)),
                            ct),
                        context.CoordinatorRunId,
                        _logger,
                        ct).ConfigureAwait(false);
                }

                var previewOutcome = await EnsureFinalPreviewOutcomeBeforeApprovalAsync(
                    context.CoordinatorRunId,
                    workPlanId,
                    aggregateTreeHash,
                    ct).ConfigureAwait(false);

                var requestChanges = buildTest.RequestChanges;
                var approved = buildTest.Approved;
                if (previewOutcome.Kind == PreviewOutcomeKind.Failed
                    && buildTest.RequestChanges
                    && IsPreviewOnlyFeedback(buildTest.Feedback))
                {
                    requestChanges = false;
                    approved = true;
                }

                if (!await ApplyAuthoredGateDecisionAsync(
                        context,
                        workPlanId,
                        edges,
                        touchedFilesBySubtask,
                        new AssemblyReviewDecision(
                            Approved: approved,
                            RequestChanges: requestChanges,
                            Feedback: buildTest.Feedback,
                            TargetFiles: buildTest.TargetFiles,
                            Reviewer: "build-test"),
                        SteeringSource.BuildTest,
                        aggregateTreeHash,
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
                    new CollectiveRaiRequest(context.CoordinatorRunId, context.RepositoryPath, aggregateDiff, context.SubmittingUser, reviewerWorktreePath), ct)
                    .ConfigureAwait(false);

                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyRaiCompleted, new
                {
                    workPlanId,
                    gateId = gate.Id,
                    raiSafetyFlagged = rai.SafetyFlagged,
                    raiRevisionRequested = rai.RevisionRequested,
                    feedback = rai.Feedback,
                });

                if (rai.SafetyFlagged)
                {
                    await ParkRaiRedAtHumanReviewAsync(
                        context, workPlanId, edges, aggregateTreeHash, touchedFilesBySubtask,
                        rai.Feedback, ct).ConfigureAwait(false);
                    return;
                }

                if (rai.RevisionRequested)
                {
                    if (await RouteAssemblyGateThroughSteeringAsync(
                            context, workPlanId, edges, SteeringSource.Rai, rai.Feedback,
                            targetFiles: null, touchedFilesBySubtask, aggregateTreeHash, ct)
                        .ConfigureAwait(false))
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
                        gate.Label,
                        WorktreePath: reviewerWorktreePath),
                    ct).ConfigureAwait(false);

                if (rubberduck.RequestChanges)
                {
                    // UNIFIED AUTONOMOUS STEERING (rev8): the gate NEVER calls RequestChangesAsync
                    // directly. It normalizes the feedback into a steering signal and routes it to the
                    // coordinator, which consciously decides the direction. If the decision terminalized
                    // or re-dispatched the plan the loop returns; an advisory decision continues.
                    if (await RouteAssemblyGateThroughSteeringAsync(
                            context, workPlanId, edges, SteeringSource.Rubberduck,
                            rubberduck.Feedback, rubberduck.TargetFiles, touchedFilesBySubtask, aggregateTreeHash, ct)
                        .ConfigureAwait(false))
                        return;
                    continue;
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
                        SteeringSource.HumanReview,
                        aggregateTreeHash,
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
        CoordinatorDispatchContext context,
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
            // run.Diff is kept ONLY for UI / touched-file extraction — never as inclusion authority.
            touchedFilesBySubtask[id] = AssemblyPlanning.ExtractTouchedFiles(run.Diff);

            // BLOCKING #1 (issue #197): gate FINAL-assembly branch inclusion on branch VALIDITY (exists +
            // tip tree == recorded handoff TreeHash), NOT on run.Diff. GetDiff can swallow an error and
            // leave run.Diff empty even after a real commit, which previously dropped a committed child
            // from the collective assembly. The committed worktree branch is the authoritative artifact.
            if (_worktreeManager is null)
            {
                // No git access (e.g. unit context) — preserve legacy behavior: include when a branch and
                // a display diff are both present.
                if (!string.IsNullOrEmpty(run.WorktreeBranch) && !string.IsNullOrEmpty(run.Diff))
                {
                    branchesInOrder.Add(run.WorktreeBranch);
                    includedSubtaskIds.Add(id);
                }
                continue;
            }

            var decision = DependencyBranchInclusion.Evaluate(
                _worktreeManager, context.RepositoryPath, run.WorktreeBranch, run.TreeHash);
            switch (decision)
            {
                case BranchInclusionOutcome.Include:
                    branchesInOrder.Add(run.WorktreeBranch!);
                    includedSubtaskIds.Add(id);
                    break;
                case BranchInclusionOutcome.ExcludeMissingBranch:
                    _logger.LogError(
                        "Coordinator assembly: subtask {SubtaskId} (child run {ChildRunId}) excluded from FINAL collective " +
                        "assembly for run {RunId} because its worktree branch is missing (WorktreeBranch={WorktreeBranch}, " +
                        "TreeHash={TreeHash}) — committed child work may be omitted (issue #197).",
                        id, childRunId, context.CoordinatorRunId, run.WorktreeBranch ?? "<null>", run.TreeHash ?? "<null>");
                    break;
                case BranchInclusionOutcome.ExcludeTreeMismatch:
                    _logger.LogError(
                        "Coordinator assembly: subtask {SubtaskId} (child run {ChildRunId}) excluded from FINAL collective " +
                        "assembly for run {RunId} because its branch tip tree does not match the recorded handoff contract " +
                        "(WorktreeBranch={WorktreeBranch}, expected TreeHash={TreeHash}) — stale/diverged branch (issue #197).",
                        id, childRunId, context.CoordinatorRunId, run.WorktreeBranch ?? "<null>", run.TreeHash ?? "<null>");
                    break;
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

        var inputs = await BuildAssemblyInputsAsync(context, subtasks, edges, ct).ConfigureAwait(false);
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
            // UNIFIED AUTONOMOUS STEERING (rev8 §3a, RD#4): the deferred human-review request-changes
            // path ALSO routes through the coordinator's steering decider — it never calls
            // RequestChangesAsync directly. The coordinator consciously chooses A/B/C/D.
            await RouteAssemblyGateThroughSteeringAsync(
                context, workPlanId, edges, SteeringSource.HumanReview, decision.Feedback,
                decision.TargetFiles, touchedFilesBySubtask, aggregateTreeHash, ct).ConfigureAwait(false);
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
        string steeringSource,
        string aggregateTreeHash,
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
            // UNIFIED AUTONOMOUS STEERING (rev8 §3a, RD#4): ALL correction feedback — including
            // build-test and human-review request-changes — normalizes into a SteeringSignal and routes
            // to the coordinator, which CONSCIOUSLY chooses the direction. The source NEVER forces a
            // reset+dispatch. RouteAssembly returns true when the gate loop should stop (terminalized or
            // re-dispatched/in-place-steered); false = advisory, continue the loop.
            var stop = await RouteAssemblyGateThroughSteeringAsync(
                context, workPlanId, edges, steeringSource, decision.Feedback,
                decision.TargetFiles, touchedFilesBySubtask, aggregateTreeHash, ct).ConfigureAwait(false);
            return !stop;
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

        var gates = ResolveAssemblyGates(workflow);

        return gates;
    }

    internal static IReadOnlyList<CoordinatorGraphDescriptor.AssemblyGateNode> ResolveAssemblyGates(
        WorkflowDefinition workflow)
    {
        var traversalOrder = ComputeWorkflowTraversalOrder(workflow);

        var gates = workflow.Nodes
            .Select((node, index) => (Node: node, DeclarationIndex: index))
            .Where(n => n.Node.Type == WorkflowNodeType.Check || n.Node.Type == WorkflowNodeType.BuildTest)
            .Select(n => (
                n.Node,
                n.DeclarationIndex,
                TraversalIndex: traversalOrder.GetValueOrDefault(n.Node.Id, int.MaxValue),
                GateKind: n.Node.Type == WorkflowNodeType.BuildTest ? "build-test" : NodeClassifier.NormalizeGateKind(n.Node)))
            .Where(x => x.GateKind is "build-test" or "rai" or "rubberduck" or "human-review")
            .OrderBy(x => x.TraversalIndex)
            .ThenBy(x => x.DeclarationIndex)
            .Select(x => new CoordinatorGraphDescriptor.AssemblyGateNode(
                CoordinatorGraphDescriptor.CanonicalStageId(x.GateKind!, x.Node.Id),
                string.IsNullOrWhiteSpace(x.Node.Label) ? x.Node.Id : x.Node.Label,
                x.GateKind!,
                x.Node.Agent))
            // Dedupe by the canonical StageId: an authored workflow with two gates that canonicalize
            // to the same stage (e.g. two human-review nodes both -> "review", or two rai nodes)
            // would otherwise emit duplicate assembly StageId / GraphNodeId, breaking the coordinator
            // graph (non-unique node ids) and AssemblyStage.Ordinal projection. The collective
            // assembly supplies each platform gate exactly once, so keeping the first occurrence per
            // canonical stage is the correct contract. (Fallback build-test/rubberduck gates keep
            // their distinct node-id-derived StageId and are unaffected.)
            .GroupBy(g => g.StageId)
            .Select(grp => grp.First())
            .ToList();

        return gates;
    }

    private static Dictionary<string, int> ComputeWorkflowTraversalOrder(WorkflowDefinition workflow)
    {
        var nodeIds = workflow.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!nodeIds.Contains(workflow.Start))
            return order;

        var nodesById = workflow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outgoing = workflow.Edges
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var queue = new Queue<string>();
        order[workflow.Start] = 0;
        queue.Enqueue(workflow.Start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in outgoing.GetValueOrDefault(current, []))
            {
                var next = edge.To;
                var target = nodesById.GetValueOrDefault(next);
                if (target?.Type == WorkflowNodeType.Terminal)
                    continue;

                if (!IsApprovalPathEdge(edge))
                    continue;

                if (!nodeIds.Contains(next) || order.ContainsKey(next))
                    continue;

                order[next] = order.Count;
                queue.Enqueue(next);
            }
        }

        return order;
    }

    private static bool IsApprovalPathEdge(WorkflowEdge? edge)
    {
        if (edge is null || string.IsNullOrWhiteSpace(edge.When))
            return true;

        var when = edge.When.Trim();
        return string.Equals(when, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(when, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(when, "review", StringComparison.OrdinalIgnoreCase);
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

        if (!ShouldAttemptFinalScribe(existingChildren, _finalScribeMaxAttempts))
            return (null, false);

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

    internal static int GetFinalScribeMaxAttempts(IConfiguration? configuration) =>
        Math.Max(1, configuration?.GetValue(
            FinalScribeMaxAttemptsConfigurationKey,
            DefaultFinalScribeMaxAttempts) ?? DefaultFinalScribeMaxAttempts);

    internal static bool ShouldAttemptFinalScribe(
        IEnumerable<Run> existingChildren,
        int maxAttempts)
    {
        var scribeAttempts = existingChildren.Where(r =>
            string.Equals(r.SubtaskId, AssemblyScribeSubtaskId, StringComparison.Ordinal)
            && string.Equals(r.AgentName, "Scribe", StringComparison.Ordinal));

        var failedAttempts = 0;
        foreach (var attempt in scribeAttempts)
        {
            if (attempt.Status is RunStatus.Completed or RunStatus.InProgress)
                return false;
            if (attempt.Status == RunStatus.Failed)
                failedAttempts++;
        }

        return failedAttempts < Math.Max(1, maxAttempts);
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
        // UNIFIED AUTONOMOUS STEERING (rev8, §9) + #223: the fragile prose-parsing InferRedispatch
        // heuristic is DELETED. This method is the deterministic direction-B (conscious fresh dispatch)
        // executor. The IMPLICATED set is the reviewer-named subtasks (via the SINGLE shared scoping
        // helper) — never the raw touched-files keys, never a set inferred from prose. The REDISPATCH
        // set additionally sweeps the implicated subtasks' transitive dependents: they did nothing wrong
        // (no author lockout — this fresh-dispatch path does not lock out anyway), but they built against
        // the now-revised contract and must rebuild. #223 fix: a prose/PRD/UX subtask that committed only
        // unnamed files is no longer swept in — only reviewer-implicated subtasks + their dependents.
        var implicatedIds = AssemblyPlanning.ScopeImplicatedSubtasks(
            touchedFilesBySubtask, decision.TargetFiles, out var usedFallback, out var fallbackReason);
        var dependentIds = AssemblyPlanning.TransitiveDependents(implicatedIds, edges);
        var targetIds = implicatedIds.Concat(dependentIds).Distinct().OrderBy(x => x).ToList();

        if (usedFallback)
            EmitImplicatedScopeFallback(
                context.CoordinatorRunId, workPlanId, source: decision.Reviewer, reviewer: decision.Reviewer,
                reason: fallbackReason, namedFiles: decision.TargetFiles, touchedFilesBySubtask);

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyChangesRequested, new
        {
            workPlanId,
            redispatchSubtaskIds = targetIds,
            redispatchedSubtaskIds = targetIds,
            implicatedSubtaskIds = implicatedIds,
            dependentSubtaskIds = dependentIds,
            feedback = decision.Feedback,
        });

        if (IsAutomatedAssemblyGateReviewer(decision.Reviewer))
        {
            _logger.LogInformation(
                "Collective assembly: retaining Build/Test pod and detached worktree for automated gate request-changes on run {RunId} (reviewer={Reviewer})",
                context.CoordinatorRunId, decision.Reviewer);
        }
        else
        {
            await CleanupAssemblyBuildTestResourcesAsync(
                context.CoordinatorRunId, context.RepositoryPath, ct).ConfigureAwait(false);
        }

        // Reset the IMPLICATED subtasks to pending (leave others' results intact) and additionally sweep
        // their transitive dependents that already built against the now-revised contract. Clear stage
        // and move the plan back to dispatching so the dispatch engine re-runs the affected frontier.
        await ResetSubtasksToPendingAsync(
            context.CoordinatorRunId, implicatedIds, decision.Feedback ?? string.Empty, ct).ConfigureAwait(false);
        await RedispatchDependentsAsync(
            context.CoordinatorRunId, workPlanId, dependentIds, decision.Feedback ?? string.Empty, ct)
            .ConfigureAwait(false);
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
            "Collective assembly: changes requested for run {RunId}; re-dispatching subtasks [{Ids}]",
            context.CoordinatorRunId, string.Join(",", targetIds));
    }

    /// <summary>
    /// #223 — resets an implicated set's TRANSITIVE DEPENDENTS to pending so they rebuild against the
    /// revised contract, WITHOUT locking out their authors (a dependent did nothing wrong; locking it
    /// re-creates the roster-exhaustion deadlock). Only dependents that already reached a satisfying
    /// state (<see cref="SubtaskStatus.AssembleReady"/>/<see cref="SubtaskStatus.Completed"/>) are reset:
    /// a still-pending/running dependent needs no reset and must never be clobbered (keeps a crash
    /// re-drive idempotent). Returns the ids actually reset.
    /// </summary>
    private async Task<IReadOnlyList<int>> RedispatchDependentsAsync(
        string coordinatorRunId, int workPlanId, IReadOnlyCollection<int> dependentSubtaskIds,
        string feedback, CancellationToken ct)
    {
        if (dependentSubtaskIds.Count == 0) return [];

        List<int> toRebuild;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            toRebuild = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == workPlanId
                    && dependentSubtaskIds.Contains(s.Id)
                    && (s.Status == SubtaskStatus.AssembleReady || s.Status == SubtaskStatus.Completed))
                .Select(s => s.Id)
                .OrderBy(id => id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        if (toRebuild.Count > 0)
        {
            await ResetSubtasksToPendingAsync(coordinatorRunId, toRebuild, feedback, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Collective assembly: #223 re-dispatching non-implicated dependents [{Ids}] for run {RunId} (rebuild against revised contract; authors NOT locked out)",
                string.Join(",", toRebuild), coordinatorRunId);
        }
        return toRebuild;
    }

    /// <summary>
    /// #223 telemetry — surfaces that the implicated-subtask scoping reverted to the broad
    /// all-contributors set (either no structured <c>targetFiles</c> field was present, or the field
    /// reverse-mapped to nothing). Emitting this makes a silent reversion to broad reset/lockout
    /// observable, carrying the raw reviewer-named files vs the touched-file universe.
    /// </summary>
    private void EmitImplicatedScopeFallback(
        string coordinatorRunId, int workPlanId, string? source, string? reviewer, string reason,
        IReadOnlyList<string>? namedFiles,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask)
    {
        Emit(coordinatorRunId, EventTypes.CoordinatorAssemblyImplicatedScopeFallback, new
        {
            workPlanId,
            source,
            reviewer,
            reason,
            namedFiles = namedFiles ?? [],
            touchedFiles = touchedFilesBySubtask.Values.SelectMany(v => v).Distinct().OrderBy(f => f).ToList(),
            contributorIds = touchedFilesBySubtask.Keys.OrderBy(x => x).ToList(),
        });
        _logger.LogInformation(
            "Collective assembly: #223 implicated-scope fell back to all contributors for run {RunId} (reason={Reason}, source={Source}, namedFiles=[{Named}])",
            coordinatorRunId, reason, source, string.Join(",", namedFiles ?? []));
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3a/§9) — the coordinator-owned routing for an assembly-gate
    /// request-changes signal. This is THE unconditional behavior: gates never call
    /// <see cref="RequestChangesAsync"/> directly (the old auto-reset+redispatch reflex). It:
    /// (1) stamps the <see cref="WorkPlanStatus.AssemblySteering"/> decision-in-progress lease, (2)
    /// normalizes the feedback into a <see cref="SteeringSignal"/> and submits it to the coordinator via
    /// <see cref="CoordinatorSteeringService.SubmitSteeringAsync"/> (persist/queue/surface), (3) claims
    /// it and invokes the <see cref="CoordinatorSteeringDecider"/> SYNCHRONOUSLY INLINE (the assembly
    /// loop is the single writer here), then (4) executes the chosen direction: A → a TRUE in-place,
    /// context-preserving resume of the target child (<see cref="ExecuteInPlaceSteerAsync"/>); B → a
    /// conscious fresh reset+redispatch (<see cref="RequestChangesAsync"/>); C → escalate to terminal
    /// (<c>steering_budget_exhausted</c>) breaking any loop; D → advisory no-op. The
    /// <c>coordinator.steering_decision</c> action is emitted first and matches the actual effect.
    /// Returns true when the gate loop should stop (a decision terminalized or re-dispatched the plan).
    /// </summary>
    private async Task<bool> RouteAssemblyGateThroughSteeringAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string source,
        string? feedback,
        IReadOnlyList<string>? targetFiles,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        string aggregateTreeHash,
        CancellationToken ct)
    {
        var steering = _serviceProvider.GetRequiredService<CoordinatorSteeringService>();
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();

        // #223 — TWO DISTINCT SETS. The IMPLICATED set (lockoutSet) is the reviewer-named subtasks only
        // (via the SINGLE shared scoping helper): only these authors produced a rejected artifact, so
        // ONLY these are the steering target scope (persisted on the directive) and ONLY these are
        // eligible for author lockout on the DispatchFresh rotation. The REDISPATCH set additionally
        // sweeps their transitive dependents — they built against the now-revised contract and must
        // rebuild, but WITHOUT author lockout (locking them re-creates the roster-exhaustion deadlock).
        // The live path NO LONGER uses the raw touchedFilesBySubtask.Keys as the implicated set (#223).
        var implicatedIds = AssemblyPlanning.ScopeImplicatedSubtasks(
            touchedFilesBySubtask, targetFiles, out var usedFallback, out var fallbackReason);
        var dependentIds = AssemblyPlanning.TransitiveDependents(implicatedIds, edges);
        var redispatchIds = implicatedIds.Concat(dependentIds).Distinct().OrderBy(x => x).ToList();
        var targetIds = implicatedIds.OrderBy(x => x).ToArray();

        if (usedFallback)
            EmitImplicatedScopeFallback(
                context.CoordinatorRunId, workPlanId, source, reviewer: $"gate:{source}",
                reason: fallbackReason, namedFiles: targetFiles, touchedFilesBySubtask);

        // UNIFIED AUTONOMOUS STEERING (Fix-B, change #2/#4): a HUMAN request-changes is a SUPERVISED,
        // deliberate action, so it ALWAYS grants a FRESH convergence mandate — it UNCONDITIONALLY resets
        // the autonomous steering budget so the coordinator's decider can converge again under human
        // guidance. There is NO cap: capping human round-trips would silently stop honoring explicit
        // human requests, a dead-end. The persisted round-trip counter is retained purely as a
        // telemetry/observability signal. Autonomous sources (rubberduck/rai/build-test/agent) NEVER
        // reset their own budget — that reset-gating (DefaultMaxPlanSteeringIterations) is exactly what
        // bounds the UNSUPERVISED loop the budget exists to stop.
        if (source == SteeringSource.HumanReview)
        {
            var roundTrips = await _assemblyStore
                .IncrementHumanReviewRoundTripAsync(workPlanId, ct).ConfigureAwait(false);
            // #223 budget hygiene: reset the FULL redispatch closure (implicated ∪ dependents) so no
            // subtask about to be re-dispatched carries a stale RecoveryAttempts count from a prior,
            // more-broadly-scoped round.
            await decider.ResetSteeringBudgetAsync(workPlanId, redispatchIds, ct).ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteering, new
            {
                workPlanId,
                source,
                humanReviewRoundTrip = roundTrips,
                note = "human request-changes: autonomous steering budget reset for a fresh convergence pass",
            });
        }

        // Decision-in-progress lease (§3b): a cross-pod claim can't race the inline decision, and a
        // restart re-enters the same boundary (recovery routes AssemblySteering → ReArmAssembly). Stamp
        // AssemblyStartedAt as the lease heartbeat so the reclaim path can tell fresh from stale.
        await _assemblyStore.SetAssemblySteeringAsync(workPlanId, ct).ConfigureAwait(false);

        // The persisted directive scope is the IMPLICATED set (crash-recovery re-derives dependents from
        // it + the plan edges), and carries the reviewer's structured file hint so the scope survives a
        // restart.
        var signal = SteeringSignal.Create(
            context.CoordinatorRunId, source, SteeringTargetScope.ForSubtasks(targetIds),
            feedback ?? string.Empty, SteeringSeverity.RequestChanges, SteeringKind.Redirect,
            createdBy: $"gate:{source}", treeHash: aggregateTreeHash, targetFiles: targetFiles);
        var view = await steering.SubmitSteeringAsync(signal, ct).ConfigureAwait(false);

        // Claim the queued directive for the inline decision (single-writer boundary → direct CAS).
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var relayedAt = DateTimeOffset.UtcNow;
            await db.SteeringDirectives
                .Where(d => d.Id == view.Id && d.Status == SteeringStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, SteeringStatus.Relayed)
                    .SetProperty(d => d.RelayedAt, relayedAt), ct)
                .ConfigureAwait(false);
        }

        var decision = await decider.DecideAsync(
            view.Id, autopilotOn: false,
            resumabilityProbe: new PodPerRunResumabilityProbe(_sandboxRuntime.IsPodPerRun && _sandboxRuntime.ReleasePodOnSuspend),
            ct: ct).ConfigureAwait(false);
        var direction = decision?.Direction ?? SteeringDirection.Proceed;
        var attempt = decision?.Attempt ?? 0;

        // DECIDER-OWNED ROUTING (Fix-B, run 19cec519) — the coordinator's decider (CoordinatorSteeringDecider
        // / SteeringPolicy) is the SINGLE authority on how iterative build-test/reviewer feedback is applied.
        // There is NO post-decision override that force-rotates every RequestChanges to a different agent
        // (that OVERRODE the decider's in_place_steer choice on EVERY assembly gate, discarding context).
        // We route PURELY by decision.Direction:
        //   • InPlaceSteer  → context-preserving resume of the SAME author (ExecuteInPlaceSteerAsync).
        //   • DispatchFresh → the target session is unresumable → CONSCIOUS lockout rotation to a DIFFERENT
        //     eligible agent, target-author only, with FULL accumulated context (ExecuteLockoutRotationAsync).
        //   • Proceed       → budget exhausted / blocking → escalate to human review (EscalateToHumanReviewAsync).
        //   • Advisory      → surface, no reset (restore assembling + mark applied).
        if (direction == SteeringDirection.InPlaceSteer)
        {
            // A — TRUE in-place resume (rev8 §3d, Ahmed's headline #3): the target subtask's child run
            // resumes its SAME session/worktree with the feedback as a revision turn — context PRESERVED,
            // NO fresh pod, NO reset-to-pending. The decorated checkpoint manager writes the durable
            // attempt-specific effect marker on the resumed workflow's first superstep. The directive is
            // left `executing`; DriveOutstandingSteeringExecutionAsync (next assembly pass / recovery)
            // probes the effect marker and advances to `applied`. The steering_decision event's action
            // is in_place — matching the actual effect (no lie to the UI).
            await decider.MarkDirectiveExecutingAsync(view.Id, ct).ConfigureAwait(false);
            await ExecuteInPlaceSteerAsync(
                context, workPlanId, edges, decision!.SubtaskIds, view.Id, attempt, feedback ?? string.Empty, ct)
                .ConfigureAwait(false);
            return true;
        }

        if (direction == SteeringDirection.DispatchFresh)
        {
            // B — CONSCIOUS lockout rotation: the decider judged the target session UNRESUMABLE, so the
            // rejected author is LOCKED OUT and the revision rotates to a DIFFERENT eligible agent
            // (target-author only) dispatched with FULL accumulated context (Req-1). #233: a
            // single-eligible-agent deadlock WITH context DEGRADES to a same-author fresh re-dispatch
            // (bounded by the recovery budget); only a no-context deadlock escalates to human review —
            // never a blind rotation, never terminal. The steering_decision event's action is
            // dispatch_fresh (matching the real effect).
            // The directive is left `executing` first (change #1: a crash before the rotation/handoff
            // completes re-drives the rotation idempotently via DriveOutstandingSteeringExecutionAsync,
            // never a silent applied); ExecuteLockoutRotationAsync settles it `applied` after the effect.
            await decider.MarkDirectiveExecutingAsync(view.Id, ct).ConfigureAwait(false);
            // #223: lockout targets = the IMPLICATED set only (decision.SubtaskIds, from the persisted
            // directive scope). The transitive dependents are re-derived from that same set + the plan
            // edges so the live path and the crash-recovery re-drive compute an identical redispatch
            // closure. Dependents are re-dispatched WITHOUT lockout.
            var freshDependents = AssemblyPlanning.TransitiveDependents(decision!.SubtaskIds, edges);
            return await ExecuteLockoutRotationAsync(
                context, workPlanId, edges, decision.SubtaskIds, freshDependents, view.Id, attempt,
                feedback ?? string.Empty, aggregateTreeHash, touchedFilesBySubtask, ct).ConfigureAwait(false);
        }

        if (direction == SteeringDirection.Proceed)
        {
            // C — UNIFIED AUTONOMOUS STEERING (Fix-B): the autonomous steering budget is exhausted. DO
            // NOT latch terminal AssemblyBlocked (that wedged the run with no way for a human to
            // intervene — Ahmed: "we should be resilient to change requests and follow through"). Instead
            // ESCALATE to the human-review gate: open awaiting_review so a human can approve / decline /
            // steer, carrying the accumulated gate feedback. Escalation is modeled as a RECOVERABLE
            // executable effect: mark the directive `executing` FIRST (change #1: a crash before the
            // review is durably open re-drives the escalation, never silently marks it applied), then
            // park the plan at review, settle the directive AFTER the review is durably OPEN (change #2:
            // never block the directive on the human decision), and live-await the human choice.
            await decider.MarkDirectiveExecutingAsync(view.Id, ct).ConfigureAwait(false);
            await EscalateToHumanReviewAsync(
                context, workPlanId, edges, view.Id,
                decision?.Rationale ?? "steering_budget_exhausted",
                aggregateTreeHash, touchedFilesBySubtask, ct).ConfigureAwait(false);
            return true;
        }

        // D — advisory: surfaced (steering_decision emitted), no reset; restore the assembly stage and
        // let the gate loop continue.
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Assembling, null, ct).ConfigureAwait(false);
        await decider.MarkDirectiveAppliedAsync(view.Id, ct).ConfigureAwait(false);
        return false;
    }

    private IAssemblyAuthorRotationSelector RotationSelector =>
        _serviceProvider.GetService<IAssemblyAuthorRotationSelector>() ?? SquadAuthorRotationSelector.Instance;

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Req-2, Strict Lockout — squad.agent.md §"Reviewer Rejection Lockout
    /// Semantics"). A CONTEXT-COMPLETE reviewer rejection locks the current author out of the artifact
    /// and rotates the revision to a DIFFERENT eligible agent, dispatched CONSCIOUSLY and VISIBLY with
    /// FULL context (Req-1 accumulated feedback + prior-work pointer). Gated on Req-1 (change #3): if the
    /// context bundle carries nothing, we do NOT rotate blind — that would just reproduce the amnesia
    /// loop with a new agent; we escalate to human review instead (the <c>lockout_no_context</c> case).
    /// #233 — if a target subtask has NO domain-eligible agent outside the locked-out set BUT there IS
    /// context to carry (a single-eligible-agent domain — the norm for a one-agent blueprint domain), we
    /// no longer dead-end to a human on the first rejection: we DEGRADE the strict cross-agent lockout to
    /// a SAME-AUTHOR fresh re-dispatch with full context (<see cref="ConsciousDispatchFreshFallbackAsync"/>)
    /// — the same author revises against the accumulated feedback + prior worktree, WITHOUT any lockout
    /// mutation. That same-author loop is BOUNDED upstream by the per-subtask recovery budget
    /// (<see cref="CoordinatorSteeringService.MaxRecoveryAttempts"/>): once it is exhausted the decider
    /// flips to Proceed and this gate escalates to human review — never a rotation to an unrelated agent,
    /// never a terminal wedge, never an infinite loop.
    /// Returns true (the gate loop stops: the rotation re-dispatched the plan, the degrade re-dispatched
    /// the plan, or a no-context deadlock escalated to human review).
    /// <para>#223 — <paramref name="targetSubtaskIds"/> is the IMPLICATED (reviewer-named) set: ONLY
    /// these authors are locked out. <paramref name="dependentSubtaskIds"/> is their transitive
    /// dependent closure: those are re-dispatched to rebuild against the revised contract WITHOUT any
    /// author lockout (locking a blameless dependent re-creates the roster-exhaustion deadlock #223
    /// fixes). The two sets are NEVER collapsed.</para>
    /// </summary>
    private async Task<bool> ExecuteLockoutRotationAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyList<int> targetSubtaskIds,
        IReadOnlyList<int> dependentSubtaskIds,
        int directiveId,
        int attempt,
        string feedback,
        string aggregateTreeHash,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        CancellationToken ct)
    {
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();

        // Req-2 (change #3) — GATE rotation on Req-1 context. The rotated pod is re-dispatched via
        // RequestChangesAsync → ResetSubtasksToPendingAsync, which threads the accumulated feedback +
        // prior-child pointer. If there is NOTHING to carry (no latest feedback AND no accumulated
        // rejection history) rotation would be a blind re-dispatch — escalate rather than rotate.
        var priorRounds = await BuildPriorReviewRoundsAsync(context.CoordinatorRunId, targetSubtaskIds, ct)
            .ConfigureAwait(false);
        var hasContext = !string.IsNullOrWhiteSpace(feedback) || priorRounds.Count > 0;

        // Load the rejection targets' current authors + domain context.
        List<Subtask> targets;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            targets = await db.Subtasks
                .Where(s => s.WorkPlanId == workPlanId && targetSubtaskIds.Contains(s.Id))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        // #238 — a non-empty run model PIN (the coordinator run's explicit request.ModelId OR the
        // project's GitHub Copilot default, both captured on Run.ModelId at start) must win for EVERY
        // subtask, including a lockout ROTATION. The rotation selector only knows the rotated author's
        // role DEFAULT model (CoordinatorAuthorRotation.cs: role?.DefaultModel), so without this the
        // first reviewer rejection would OVERWRITE the pinned model back to a role default — both at the
        // TryRotateSubtaskAuthorAsync persist and the handoff child's ModelId. Mirror SelectModel
        // precedence: run pin wins; else keep the rotation's role-default choice.
        var runModelPin = (await TryGetCoordinatorRunAsync(context.CoordinatorRunId, ct).ConfigureAwait(false))?.ModelId;
        RotationChoice PinModel(RotationChoice choice) =>
            string.IsNullOrWhiteSpace(runModelPin) ? choice : choice with { SelectedModelId = runModelPin! };

        // Plan the rotation for EACH target BEFORE mutating anything: pick a different eligible author.
        // IDEMPOTENT RE-DRIVE (change #1): a target this directive/attempt ALREADY rotated (the durable
        // (LastResetDirectiveId, LastResetAttempt) stamp written atomically with the rotation) is NOT
        // re-selected — re-selecting off its now-rotated author would DOUBLE-ROTATE it to a third agent.
        // It is carried straight to the handoff under its CURRENT (already-rotated) author so a crash
        // between the rotation and the handoff still completes the handoff (never a silent drop).
        var planned = new List<(Subtask Subtask, RotationChoice Choice)>();
        var alreadyRotated = new List<(Subtask Subtask, RotationChoice Choice)>();
        var deadlockRoster = new List<string>();
        var deadlocked = !hasContext;
        foreach (var subtask in targets)
        {
            if (subtask.LastResetDirectiveId == directiveId && subtask.LastResetAttempt == attempt)
            {
                // Already rotated by THIS directive/attempt — carry under the current rotated author.
                alreadyRotated.Add((subtask, PinModel(
                    new RotationChoice(subtask.AssignedAgent, subtask.SelectedModelId, subtask.AgentCharter))));
                continue;
            }
            var lockedOut = new HashSet<string>(
                await _assemblyStore.GetLockedOutAgentsAsync(subtask.Id, ct).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase);
            var choice = hasContext
                ? RotationSelector.SelectRotationAuthor(
                    context.RepositoryPath,
                    new RotationSubtaskContext(subtask.Id, subtask.AssignedAgent, subtask.Title, subtask.Scope, subtask.Phase),
                    lockedOut)
                : null;
            if (choice is null)
            {
                deadlocked = true;
                // Record the full locked-out roster (existing lockouts + the author being rejected now).
                var roster = new List<string>(lockedOut) { subtask.AssignedAgent };
                deadlockRoster.AddRange(roster);
                continue;
            }
            planned.Add((subtask, PinModel(choice)));
        }

        // A pure re-drive where EVERY target is already rotated by this directive/attempt has nothing to
        // re-select — it is NOT a deadlock (the rotation already succeeded); fall through to the handoff.
        // GUARD (change #5): only suppress the deadlock escalation when there is NO genuine per-target
        // deadlock recorded. A multi-target directive can crash after rotating+stamping target A but
        // before target B, then genuinely deadlock on target B (all eligible authors locked out) on
        // re-drive. In that case planned is empty and alreadyRotated has target A, but deadlockRoster is
        // non-empty — we MUST let the `if (deadlocked)` branch escalate to human review rather than force
        // deadlocked=false, which would silently drop target B (never in planned nor alreadyRotated).
        if (planned.Count == 0 && alreadyRotated.Count > 0 && deadlockRoster.Count == 0)
            deadlocked = false;

        if (deadlocked)
        {
            var roster = deadlockRoster.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // #233 — SPLIT the two DISTINCT deadlock causes. `deadlocked` becomes true for exactly one of
            // two reasons, cleanly discriminated by hasContext:
            //   (a) hasContext == false → NOTHING to carry (this is the INITIAL value of `deadlocked`),
            //       OR
            //   (b) hasContext == true BUT ≥1 target's domain has a SINGLE eligible agent, so
            //       SelectRotationAuthor returned null and its author was recorded in deadlockRoster.
            //
            // (b) SINGLE-ELIGIBLE-AGENT WITH CONTEXT (#233 live incident, staging run 825ea158): the strict
            //     cross-agent lockout has NO other eligible agent to rotate to (the norm for a blueprint
            //     whose domain has one eligible agent), but we DO have accumulated feedback + a prior
            //     worktree to carry. Dead-ending an otherwise-recoverable revision to a human on the FIRST
            //     rejection is exactly the systemic wedge #233 reports. DEGRADE the strict lockout to a
            //     SAME-AUTHOR fresh re-dispatch with FULL context via ConsciousDispatchFreshFallbackAsync
            //     → RequestChangesAsync → ResetSubtasksToPendingAsync: it KEEPS AssignedAgent (same
            //     author — the only eligible one), does NOT mutate LockedOutAgents (locking the sole
            //     author would re-create the same roster-exhaustion deadlock), writes RecoveryGuidance
            //     from the accumulated feedback + prior worktree branch, and re-dispatches dependents
            //     WITHOUT lockout. It covers the FULL targetSubtaskIds set (implicated ∪ dependents), so
            //     NO target is silently dropped — including a MIXED directive (some targets rotatable in
            //     `planned`, some deadlocked single-eligible): all its targets uniformly degrade to a
            //     same-author fresh re-dispatch (the rotatable ones simply keep their current author).
            //     This same-author loop is BOUNDED UPSTREAM: ResetSubtasksToPendingAsync does NOT reset
            //     Subtask.RecoveryAttempts, so once the per-subtask recovery budget
            //     (CoordinatorSteeringService.MaxRecoveryAttempts) is exhausted the decider's policy flips
            //     to Proceed and THIS gate escalates to human review — it can never loop forever or wedge.
            if (hasContext)
            {
                _logger.LogWarning(
                    "Steering(lockout): directive {DirectiveId} single-eligible-agent deadlock for run {RunId} " +
                    "(roster locked out: [{Roster}]) — degrading strict lockout to a same-author fresh re-dispatch " +
                    "with full context (#233)",
                    directiveId, context.CoordinatorRunId, string.Join(",", roster));
                await ConsciousDispatchFreshFallbackAsync(
                    context, workPlanId, edges, targetSubtaskIds, directiveId, feedback, ct,
                    rationale: "single_eligible_agent: degrading strict lockout to same-author fresh re-dispatch with full context")
                    .ConfigureAwait(false);
                return true;
            }

            // (a) NO-CONTEXT deadlock → KEEP escalating to human review (protocol step 7). Degrading a
            //     no-context deadlock to a fresh re-dispatch would carry NOTHING and reproduce the exact
            //     amnesia loop that the #220/#223 context-carry prevents. NEVER degrade this case.
            await decider.OverrideDecidedActionAsync(directiveId, SteeringDirection.Proceed, ct)
                .ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
            {
                workPlanId,
                directiveId,
                decision = SteeringDirection.Proceed,
                disposition = "rejection",
                rationale = "lockout_no_context: nothing to carry — escalating to human review",
                lockedOutRoster = roster,
                targetSubtaskIds,
            });
            _logger.LogWarning(
                "Steering(lockout): directive {DirectiveId} DEADLOCK for run {RunId} (roster locked out: [{Roster}]) — escalating to human review",
                directiveId, context.CoordinatorRunId, string.Join(",", roster));
            await EscalateToHumanReviewAsync(
                context, workPlanId, edges, directiveId,
                "lockout_no_context",
                aggregateTreeHash, touchedFilesBySubtask, ct).ConfigureAwait(false);
            return true;
        }

        // Rotate every target atomically (change #4): append the rejected author to the durable
        // locked-out set + persist the new author/model/charter in one guarded CAS per subtask.
        var rotated = new List<(Subtask Subtask, RotationChoice Choice)>();
        foreach (var (subtask, choice) in planned)
        {
            var result = await _assemblyStore.TryRotateSubtaskAuthorAsync(
                subtask.Id, subtask.AssignedAgent, choice.AgentName, choice.SelectedModelId, choice.AgentCharter,
                ct, directiveId, attempt)
                .ConfigureAwait(false);

            // Defensive: only the replica that WON the guarded CAS re-dispatches this target's handoff.
            // The directive-level single-writer lease already serializes replicas, so this is belt-and-
            // suspenders — a CAS loser must never launch a handoff child + repoint the subtask.
            if (result.Won)
                rotated.Add((subtask, choice));

            // Visible, conscious rotation event (never a glitch): who was locked out, who now owns it.
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
            {
                workPlanId,
                directiveId,
                decision = SteeringDirection.DispatchFresh,
                disposition = "rejection",
                rationale = result.Won
                    ? "strict_lockout: reviewer rejection — author locked out, rotating revision to a different eligible agent with full context"
                    : "strict_lockout: rotation already applied by a concurrent replica (no-op)",
                subtaskId = subtask.Id,
                rotatedFrom = subtask.AssignedAgent,
                rotatedTo = choice.AgentName,
                lockedOutRoster = result.LockedOutRoster,
                attempt,
            });
            _logger.LogInformation(
                "Steering(lockout): directive {DirectiveId} rotated subtask {SubtaskId} from {From} to {To} for run {RunId} (won={Won}, lockedOut=[{Locked}])",
                directiveId, subtask.Id, subtask.AssignedAgent, choice.AgentName, context.CoordinatorRunId,
                result.Won, string.Join(",", result.LockedOutRoster));
        }

        // The persisted decision matches the real effect (a conscious fresh dispatch to the new author).
        await decider.OverrideDecidedActionAsync(directiveId, SteeringDirection.DispatchFresh, ct)
            .ConfigureAwait(false);

        // Re-dispatch via the CONTEXT-CARRYING handoff: instead of the plain fresh dispatch (which
        // provisions a brand-new worktree branched from the integration branch and DISCARDS the
        // locked-out author's uncommitted/staged worktree work), hand off to the ROTATED (different,
        // non-locked-out) agent via RunOrchestrator.StartChildRevisionHandoffAsync. That mints a NEW
        // SDK session (lockout-correct — the new agent does NOT inherit the locked-out author's
        // conversation) while REUSING the prior child's worktree/branch and injecting the accumulated
        // review feedback. The rotated author was already persisted on the subtask above (change #4).
        // Freshly-rotated targets AND any already-rotated targets carried in from a crash re-drive are
        // handed off together (the handoff is itself idempotent — see DispatchLockoutHandoffAsync).
        var handoffTargets = rotated.Concat(alreadyRotated).ToList();
        await DispatchLockoutHandoffAsync(
            context, workPlanId, edges, handoffTargets, dependentSubtaskIds, feedback, ct)
            .ConfigureAwait(false);

        await decider.MarkDirectiveAppliedAsync(directiveId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-A(3a) Path-2 wiring) — re-dispatches each lockout-rotated
    /// subtask to its NEW (non-locked-out) author via the CONTEXT-CARRYING handoff
    /// <see cref="RunOrchestrator.StartChildRevisionHandoffAsync"/> rather than a plain fresh dispatch.
    /// For each rotated target it: (1) resolves the locked-out author's PRIOR child run (the durable
    /// worktree/branch source captured before any reset — <see cref="Subtask.ChildRunId"/> still points
    /// at it here), (2) builds the STABLE <see cref="AccumulatedReviewFeedback"/> bundle
    /// (target+rejection-scoped prior rounds + <c>PriorWorktreeBranch</c>), (3) allocates a fresh child
    /// run (NEW <see cref="RunId"/> ⇒ new deterministic session ⇒ lockout-correct) carrying the ROTATED
    /// author/model/charter and the prior child's base task — WITHOUT the rendered guidance (the handoff
    /// appends <see cref="AccumulatedReviewFeedback.RenderedGuidance"/> itself; never double-append) —
    /// and does NOT pre-insert the row (the handoff calls <c>InsertAsync</c>, mirroring
    /// <c>StartChildRunAsync</c>), (4) launches the handoff (which reuses the prior worktree when safe,
    /// else visibly branches a clean worktree from the prior branch), and (5) points the subtask at the
    /// new child (<see cref="SubtaskStatus.Running"/>, prior pointer retained) so the re-armed dispatch
    /// loop RE-OBSERVES it (never re-dispatches a duplicate). A rotated target that has NO resolvable
    /// prior child (nothing to reuse) falls back to the plain fresh dispatch (reset-to-pending; the
    /// dispatch engine composes a fresh child that still carries the accumulated guidance via
    /// <c>RecoveryGuidance</c>). Worktree safety + the <c>coordinator.child_revision_handoff</c> strategy
    /// event are OWNED by the handoff method; this method does not handle poisoned-tree fallback.
    /// </summary>
    private async Task DispatchLockoutHandoffAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyList<(Subtask Subtask, RotationChoice Choice)> planned,
        IReadOnlyList<int> dependentSubtaskIds,
        string feedback,
        CancellationToken ct)
    {
        var targetIds = planned.Select(p => p.Subtask.Id).OrderBy(x => x).ToList();

        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyChangesRequested, new
        {
            workPlanId,
            redispatchSubtaskIds = targetIds,
            redispatchedSubtaskIds = targetIds,
            dependentSubtaskIds,
            feedback,
        });

        // The reviewer here is the coordinator's automated assembly gate — RETAIN the Build/Test pod +
        // detached worktree across the rotation (mirrors RequestChangesAsync's automated-gate branch)
        // so the prior worktree the handoff reuses is not torn down underneath it.
        _logger.LogInformation(
            "Collective assembly: retaining Build/Test pod and detached worktree for lockout rotation handoff on run {RunId}",
            context.CoordinatorRunId);

        var freshFallbackIds = new List<int>();
        // Resolve the handoff seam LAZILY — only when at least one target actually has a reusable prior
        // child. Targets that fall through to the plain fresh dispatch never need it.
        IChildRevisionHandoff? handoff = null;
        foreach (var (subtask, choice) in planned)
        {
            var priorChildRunId = subtask.ChildRunId;
            Run? priorChild = null;
            if (!string.IsNullOrWhiteSpace(priorChildRunId) && RunId.TryParse(priorChildRunId, out var priorRunId))
                priorChild = await _runStore.GetAsync(priorRunId, ct).ConfigureAwait(false);

            if (priorChild is null)
            {
                // No prior child run to hand off from (nothing to reuse) → plain fresh dispatch for this
                // target: reset-to-pending threads the accumulated guidance via RecoveryGuidance and the
                // dispatch engine composes a fresh child under the ROTATED author (already persisted).
                freshFallbackIds.Add(subtask.Id);
                continue;
            }

            // IDEMPOTENT RE-DRIVE (change #1): if the subtask's CURRENT child already belongs to the
            // ROTATED (target) author, this handoff already ran for this rotation — SKIP minting another
            // child (a re-drive after a crash between the handoff launch and MarkDirectiveApplied must not
            // double-dispatch). On the FIRST pass the child still belongs to the locked-out author, so
            // this never short-circuits a genuine handoff.
            if (string.Equals(priorChild.AgentName, choice.AgentName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Steering(lockout): subtask {SubtaskId} already handed off to {Agent} (child {Child}) — re-drive no-op for run {RunId}",
                    subtask.Id, choice.AgentName, priorChildRunId, context.CoordinatorRunId);
                continue;
            }

            // STABLE contract bundle: target+rejection-scoped prior rounds + PriorWorktreeBranch (prior
            // child branch, falling back to the integration branch so the handoff precondition always
            // holds) + deterministic RenderedGuidance. Built BEFORE we repoint the subtask's ChildRunId.
            var bundle = await BuildAccumulatedReviewFeedbackAsync(
                context.CoordinatorRunId, subtask.Id, feedback, priorChildRunId, ct).ConfigureAwait(false);

            // Allocate the NEW agent's child run like a fresh child (new RunId ⇒ new deterministic
            // session ⇒ lockout-correct) but DO NOT pre-insert — the handoff inserts it itself. Carry
            // the ROTATED author/model/charter; keep the prior child's repo/branch/project/user so the
            // new agent works the SAME subtask. The base Task is the GUIDANCE-FREE canonical subtask
            // text derived from the Subtask definition — NOT priorChild.Task (which, on the 2nd+
            // rotation, is itself a prior handoff child whose Task already embeds an earlier round's
            // rendered guidance; chaining it would double-carry that guidance and compound each
            // rotation). The handoff appends bundle.RenderedGuidance (ALL accumulated rounds) exactly
            // once, so this base MUST carry no guidance — preserving the "never double-append" invariant.
            var newAgentRun = priorChild with
            {
                Id = RunId.New(),
                ParentRunId = context.CoordinatorRunId,
                SubtaskId = subtask.Id.ToString(),
                AgentName = choice.AgentName,
                ModelId = choice.SelectedModelId,
                AgentCharter = choice.AgentCharter,
                Task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(subtask),
                Status = RunStatus.InProgress,
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = null,
                Result = null,
                WorktreePath = null,
                WorktreeBranch = null,
            };

            handoff ??= _serviceProvider.GetRequiredService<IChildRevisionHandoff>();
            await handoff.StartChildRevisionHandoffAsync(newAgentRun, priorChild, bundle, ct)
                .ConfigureAwait(false);

            // Point the subtask at the NEW child, retain the prior pointer, and mark Running so the
            // re-armed dispatch loop RE-OBSERVES this child (does not re-dispatch a duplicate).
            await SetSubtaskHandoffRunningAsync(
                subtask.Id, newAgentRun.Id.ToString(), priorChildRunId!, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Steering(lockout): subtask {SubtaskId} handed off to {Agent} via StartChildRevisionHandoffAsync " +
                "(newChild={NewChild}, priorChild={PriorChild}, priorBranch={Branch}) for run {RunId}",
                subtask.Id, choice.AgentName, newAgentRun.Id, priorChildRunId, bundle.PriorWorktreeBranch,
                context.CoordinatorRunId);
        }

        // Targets with no reusable prior child → plain fresh dispatch (dispatch engine relaunches).
        if (freshFallbackIds.Count > 0)
            await ResetSubtasksToPendingAsync(context.CoordinatorRunId, freshFallbackIds, feedback, ct)
                .ConfigureAwait(false);

        // #223: transitive dependents of the implicated subtasks are re-dispatched to rebuild against the
        // revised contract — but WITHOUT any author lockout (they authored nothing rejected). This runs
        // only on the re-dispatch path (never on escalation to human review), so a blameless dependent is
        // reset iff the plan is actually going back out for another round.
        await RedispatchDependentsAsync(context.CoordinatorRunId, workPlanId, dependentSubtaskIds, feedback, ct)
            .ConfigureAwait(false);

        // Return the plan to dispatching and re-arm the loop, whose recovery-aware re-arm re-observes the
        // Running handoff child(ren) and re-arms assembly when they complete (Fix-A's failure→terminal
        // edge governs terminal emission for the trimmed child pipeline the handoff launches).
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Dispatching, null, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.Dispatching, edges, ct)
            .ConfigureAwait(false);

        var dispatch = _serviceProvider.GetRequiredService<ICoordinatorDispatch>();
        dispatch.StartDispatch(context);

        _logger.LogInformation(
            "Collective assembly: lockout rotation re-dispatched for run {RunId}; handed off [{Handoff}] via revision handoff, fresh-fallback [{Fresh}]",
            context.CoordinatorRunId,
            string.Join(",", targetIds.Except(freshFallbackIds)),
            string.Join(",", freshFallbackIds));
    }

    /// <summary>
    /// Points a lockout-rotated subtask at its NEW handoff child run: sets
    /// <see cref="SubtaskStatus.Running"/>, repoints <c>ChildRunId</c> to the new run, and RETAINS the
    /// prior child pointer in <c>PriorChildRunId</c> (durable provenance / worktree source). Unlike the
    /// in-place resume, this does NOT write <c>RecoveryGuidance</c> — the handoff injects the accumulated
    /// guidance into the new agent's task prompt directly (avoids double-carrying it).
    /// </summary>
    private async Task SetSubtaskHandoffRunningAsync(
        int subtaskId, string newChildRunId, string priorChildRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Subtasks
            .Where(s => s.Id == subtaskId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SubtaskStatus.Running)
                .SetProperty(s => s.ChildRunId, newChildRunId)
                .SetProperty(s => s.PriorChildRunId, priorChildRunId)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B) — ESCALATES an exhausted steering budget to the human-review
    /// gate instead of latching terminal AssemblyBlocked. Durably PARKS the plan at review (idempotent
    /// effect) and — for the replica that won the escalation — settles the directive as soon as the
    /// review is durably OPEN (change #2: the directive is NEVER blocked on the human decision) and then
    /// live-awaits the human choice via the SAME gate machinery a normal human review uses
    /// (approve → complete/merge, request-changes → route back through steering, decline → terminal).
    /// A replica that LOST the park CAS simply returns — the winning replica (or the reconciler's
    /// <c>ResumeInReviewAsync</c> after a crash) owns the live await. Because the park is durable and the
    /// directive is settled only after it, a crash BEFORE the review opens leaves the directive
    /// <c>executing</c> → recovery re-drives the escalation (never a silent drop).
    /// </summary>
    private async Task EscalateToHumanReviewAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        int directiveId,
        string reason,
        string aggregateTreeHash,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        CancellationToken ct)
    {
        var won = await ParkAtHumanReviewAsync(
            context, workPlanId, directiveId, reason, aggregateTreeHash, ct).ConfigureAwait(false);
        if (!won)
            return;

        // The park is durably open and the directive is settled; live-await the human decision on THIS
        // replica (a crash here is recovered by ResumeInReviewAsync, which re-reads the durable review
        // record and applies the eventual decision). Not blocking the directive means autopilot-with-no-
        // human simply parks at awaiting_review showing the preview — never auto-approve/decline (§5).
        var decision = await AwaitReviewDecisionAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
        if (decision is null)
            return;

        await ApplyReviewDecisionAsync(
            context, workPlanId, edges, IntegrationBranchName(context.CoordinatorRunId),
            aggregateTreeHash, touchedFilesBySubtask, decision, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B) — the DURABLE, IDEMPOTENT escalation effect. GUARDED-CAS
    /// transitions the plan AssemblySteering/Assembling → InReview/stage "review" (change #3: a second
    /// replica that finds the plan already InReview NO-OPs, preventing double-escalation from clobbering
    /// an open review record). The winner: writes the durable review request, emits the review-requested
    /// event carrying the exhaustion reason + accumulated gate feedback (so the escalation is VISIBLE —
    /// never a glitch), marks the coordinator run awaiting_review, and settles the directive `applied`
    /// AFTER the review is durably OPEN (change #2). Returns true iff this replica won the escalation.
    /// </summary>
    private async Task<bool> ParkAtHumanReviewAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int directiveId,
        string reason,
        string aggregateTreeHash,
        CancellationToken ct)
    {
        var won = await _assemblyStore.TryEscalateToInReviewAsync(workPlanId, ct).ConfigureAwait(false);
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();
        var integrationBranch = IntegrationBranchName(context.CoordinatorRunId);

        if (!won)
        {
            // Lost the CAS: the plan is already InReview — either a concurrent replica escalated it, OR a
            // PRIOR escalation of THIS directive crashed after the InReview transition but before the
            // durable review request was written (change #1 crash window). Verify a durable review record
            // exists; if it is MISSING, complete the escalation here (write it) so the human gate is never
            // left open-in-status-only with no card. If a record already exists we do NOT touch it (a
            // concurrent replica owns it and may already hold a submitted human decision — change #3).
            var existing = await CoordinatorAssemblyReviewPersistence
                .GetAsync(_scopeFactory, context.CoordinatorRunId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
                    _scopeFactory, context.CoordinatorRunId, context.SubmittingUser,
                    integrationBranch, aggregateTreeHash, ct).ConfigureAwait(false);
                await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
                {
                    workPlanId,
                    integrationBranch,
                    treeHash = aggregateTreeHash,
                    gateKind = "human-review",
                    escalated = true,
                    reason,
                    recovered = true,
                });
                await MarkCoordinatorAwaitingReviewAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
            }
            await decider.MarkDirectiveAppliedAsync(directiveId, ct).ConfigureAwait(false);
            return false;
        }

        var accumulatedFeedback = await BuildAccumulatedGateFeedbackAsync(context.CoordinatorRunId, ct)
            .ConfigureAwait(false);

        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory,
            context.CoordinatorRunId,
            context.SubmittingUser,
            integrationBranch,
            aggregateTreeHash,
            ct).ConfigureAwait(false);

        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
        {
            workPlanId,
            integrationBranch,
            treeHash = aggregateTreeHash,
            gateKind = "human-review",
            escalated = true,
            reason,
            accumulatedFeedback,
        });

        await MarkCoordinatorAwaitingReviewAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);

        // Settle the directive ONLY now that the review is durably OPEN (change #2). A crash BEFORE this
        // point leaves the directive `executing` → DriveOutstandingSteeringExecutionAsync re-drives the
        // escalation on recovery (change #1) rather than silently marking it applied.
        await decider.MarkDirectiveAppliedAsync(directiveId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, §7) — collects the accumulated gate feedback for the escalated
    /// review card, bounded and structured by gate source + round. Reads the run's steering directives
    /// (each carries its source + instruction/feedback) so the human sees WHY the autonomous loop could
    /// not converge before they approve / decline / steer.
    /// </summary>
    private async Task<IReadOnlyList<object>> BuildAccumulatedGateFeedbackAsync(
        string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.SteeringDirectives.AsNoTracking()
            .Where(d => d.CoordinatorRunId == coordinatorRunId && d.Instruction != null)
            .OrderBy(d => d.Id)
            .Select(d => new { d.Source, d.Instruction })
            .Take(32)
            .ToListAsync(ct).ConfigureAwait(false);
        var round = 0;
        return rows
            .Select(r => (object)new
            {
                round = ++round,
                gate = r.Source,
                feedback = Truncate(r.Instruction, 2000),
            })
            .ToList();
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, change #1) — true iff the human-review escalation is durably
    /// OPEN: the plan is <see cref="WorkPlanStatus.InReview"/> at the canonical <see cref="AssemblyStage.Review"/>
    /// stage AND a durable review request record exists. Used by recovery to decide whether a crashed
    /// Proceed→escalation directive can be settled (both true) or must be RE-DRIVEN (either false).
    /// </summary>
    private async Task<bool> IsEscalationDurablyOpenAsync(
        string coordinatorRunId, int workPlanId, CancellationToken ct)
    {
        bool inReview;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            inReview = await db.WorkPlans.AsNoTracking()
                .AnyAsync(w => w.Id == workPlanId
                    && w.Status == WorkPlanStatus.InReview
                    && w.AssemblyStage == AssemblyStage.Review, ct)
                .ConfigureAwait(false);
        }
        if (!inReview)
            return false;
        var record = await CoordinatorAssemblyReviewPersistence
            .GetAsync(_scopeFactory, coordinatorRunId, ct).ConfigureAwait(false);
        return record is not null
            && !string.IsNullOrEmpty(record.IntegrationBranch)
            && !string.IsNullOrEmpty(record.AggregateTreeHash);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3d, Decision A) — executes a TRUE in-place, context-preserving
    /// steer of the target subtask(s). For each target it: (1) probes the durable
    /// <c>SteeringRevisionExecution</c> effect marker — if already confirmed, skips (recovery advances
    /// the directive), (2) inserts the Phase-1 <c>initiated</c> marker under the UNIQUE
    /// <c>(directiveId, attempt)</c> key BEFORE launch, (3) resumes the child run's SAME session/worktree
    /// via <see cref="RunOrchestrator.StartRevisionAsync"/> (isChild:true) with the feedback as a
    /// revision turn — NO reset-to-pending, NO fresh pod, context PRESERVED — threading
    /// <c>(directiveId, attempt)</c> so the per-launch checkpoint decorator confirms the effect marker on
    /// the resumed workflow's first superstep, (4) sets the subtask <see cref="SubtaskStatus.Running"/>
    /// keeping its <c>ChildRunId</c>. Then it returns the plan to <see cref="WorkPlanStatus.Dispatching"/>
    /// and re-arms dispatch, whose recovery-aware re-arm re-observes the Running child and re-arms
    /// assembly when it completes. The directive is left <c>executing</c>; the next assembly pass
    /// (<see cref="DriveOutstandingSteeringExecutionAsync"/>) probes the effect and advances to
    /// <c>applied</c> — so we NEVER mark applied before a durable effect is observed.
    /// </summary>
    private async Task ExecuteInPlaceSteerAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyList<int> targetSubtaskIds,
        int directiveId,
        int attempt,
        string feedback,
        CancellationToken ct)
    {
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();
        // Req-1 (change #6, in-place): the caller REMOVES the child stream before the revision restart
        // (_streamStore.Remove below), so the resumed agent can NOT rely on replayed stream history for
        // prior-round feedback. Thread the ACCUMULATED, target+rejection-scoped feedback EXPLICITLY into
        // the revision task so an in-place resume also sees every prior requirement, not just the latest.
        var priorRounds = await BuildPriorReviewRoundsAsync(context.CoordinatorRunId, targetSubtaskIds, ct)
            .ConfigureAwait(false);
        // In-place resume PRESERVES the child session — pass no prior worktree branch (the agent
        // continues where it left off; there is no "build on prior work" fresh-pod pointer to inject).
        var guidance = ReviewFeedbackRenderer.RenderForRevisionPrompt(
            feedback, priorRounds, priorWorktreeBranch: null);

        List<Subtask> targets;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            targets = await db.Subtasks
                .Where(s => s.WorkPlanId == workPlanId && targetSubtaskIds.Contains(s.Id))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        // Identify the resumable target children (same session/worktree preserved). Direction A can
        // target MULTIPLE subtasks; each is its own child run and gets its OWN per-child effect marker
        // (RD-B) keyed (directiveId, attempt, childRunId).
        var resumable = new List<(Subtask Subtask, RunId RunId, Run Run)>();
        foreach (var subtask in targets)
        {
            if (string.IsNullOrWhiteSpace(subtask.ChildRunId)
                || !RunId.TryParse(subtask.ChildRunId, out var childRunId))
                continue;
            var childRun = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false);
            if (childRun is null)
                continue;
            resumable.Add((subtask, childRunId, childRun));
        }

        if (resumable.Count == 0)
        {
            // FIX (RD#6): Decision A cannot resume any child (runs GC'd / never had a child). Decision A
            // must NOT silently degrade to B — that reintroduces the "fresh dispatch felt like a glitch"
            // bug. Make a CONSCIOUS dispatch_fresh decision (emit coordinator.steering_decision with
            // action=dispatch_fresh, durably record DecidedAction=dispatch_fresh so the persisted decision
            // matches the real effect), THEN reset+dispatch.
            await ConsciousDispatchFreshFallbackAsync(
                context, workPlanId, edges, targetSubtaskIds, directiveId, feedback, ct).ConfigureAwait(false);
            return;
        }

        // PER-CHILD idempotency (RD-B): probe each target child's durable effect marker. A child whose
        // effect is already confirmed (ran ≥1 superstep) is SKIPPED — never re-injected — while the
        // remaining unconfirmed children are (re)launched. This is what lets a partial multi-target
        // crash (first child confirmed, pod died before the second launched) resume ONLY the missing
        // children instead of marking the whole directive applied.
        var pending = new List<(Subtask Subtask, RunId RunId, Run Run)>();
        foreach (var r in resumable)
        {
            var childProbe = await decider
                .ProbeRevisionEffectAsync(directiveId, attempt, r.Subtask.ChildRunId!, ct)
                .ConfigureAwait(false);
            if (childProbe != RevisionRecoveryAction.Advance)
                pending.Add(r);
        }

        if (pending.Count == 0)
        {
            // Every targeted child already durably confirmed its effect — nothing to (re)launch.
            // DriveOutstandingSteeringExecutionAsync advances the directive to applied once it sees ALL
            // children confirmed. Just return the plan to dispatching so assembly re-arms on completion.
            await ReturnPlanToDispatchingAfterSteerAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
            return;
        }

        // Bounded per-directive EXECUTION retry (rev8 §6, RD#2): each drive/re-drive that ACTUALLY
        // launches ≥1 child consumes one execution attempt via an atomic guarded CAS. SEPARATE from the
        // decision budget so a revision that finishes/errors before EVER writing a checkpoint (never
        // confirms its effect marker) still TERMINATES — recovery re-drives are capped. On exhaustion the
        // directive is parked needs_attention (visible) and the plan escalates to a terminal; NEVER an
        // infinite loop.
        // FIX (Nit 1): the increment is here — AFTER we know there is at least one child to launch — so a
        // pure no-op re-drive (all children already confirmed) never burns an execution attempt.
        if (!await decider.TryIncrementExecutionAttemptAsync(directiveId, ct).ConfigureAwait(false))
        {
            await EscalateSteeringExecutionExhaustedAsync(
                context, workPlanId, edges, directiveId, ct).ConfigureAwait(false);
            return;
        }

        var launched = 0;
        // Resolve the orchestrator LAZILY — only once we know at least one child will actually be
        // (re)launched. The unresumable (→ conscious dispatch_fresh) and all-confirmed early-returns
        // above never launch a revision, so they must not depend on RunOrchestrator being resolvable.
        var orchestrator = _serviceProvider.GetRequiredService<RunOrchestrator>();
        foreach (var (subtask, childRunId, childRun) in pending)
        {
            // PER-CHILD launch claim (RD-A recovery relaunch): insert the Phase-1 `initiated` marker for
            // THIS child if absent, OR relaunch against an existing `initiated` marker (a transient crash
            // BEFORE the first checkpoint — the old code no-oped here and wedged the run). Only an already
            // CONFIRMED child returns Skip. The AssemblySteering/reclaim lease held across this whole
            // assembly pass serializes launchers, so exactly one pod is here — relaunch is safe.
            var launchDecision = await decider
                .ClaimRevisionLaunchAsync(subtask.ChildRunId!, directiveId, attempt, ct).ConfigureAwait(false);
            if (launchDecision == RevisionLaunchDecision.Skip)
                continue;

            // Resume the SAME session + worktree: drop the completed child stream, flip the run back to
            // InProgress (same runId — never restarted), and inject the feedback as a revision turn.
            // (directiveId, attempt) thread through so the decorated checkpoint manager confirms the
            // per-child effect marker on the resumed workflow's first superstep.
            _streamStore.Remove(subtask.ChildRunId!);
            await _runStore.UpdateStatusAsync(childRunId, RunStatus.InProgress, null, ct)
                .ConfigureAwait(false);
            await orchestrator.StartRevisionAsync(
                childRun, guidance, ct, isChild: true,
                steeringDirectiveId: directiveId, steeringAttempt: attempt).ConfigureAwait(false);
            await SetSubtaskRunningPreservingContextAsync(
                subtask.Id, subtask.ChildRunId!, directiveId, attempt, guidance, ct).ConfigureAwait(false);
            launched++;
        }

        await ReturnPlanToDispatchingAfterSteerAsync(context, workPlanId, edges, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Steering(A): in-place resume dispatched for run {RunId}; directive {DirectiveId} attempt {Attempt}, launched {Launched}/{Pending} pending child(ren), targets [{Ids}]",
            context.CoordinatorRunId, directiveId, attempt, launched, pending.Count, string.Join(",", targetSubtaskIds));
    }

    /// <summary>
    /// Returns the plan to <see cref="WorkPlanStatus.Dispatching"/> after an in-place steer (or a
    /// confirmed-effect no-op) and re-arms dispatch, whose recovery-aware re-arm re-observes the Running
    /// child and re-arms assembly when it completes. The directive stays <c>executing</c> until the
    /// effect is confirmed by <see cref="DriveOutstandingSteeringExecutionAsync"/>.
    /// </summary>
    private async Task ReturnPlanToDispatchingAfterSteerAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        await _assemblyStore.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.Dispatching, null, ct).ConfigureAwait(false);
        await CoordinatorAssemblyReviewPersistence.ClearAsync(_scopeFactory, context.CoordinatorRunId, ct)
            .ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.Dispatching, edges, ct)
            .ConfigureAwait(false);

        var dispatch = _serviceProvider.GetRequiredService<ICoordinatorDispatch>();
        dispatch.StartDispatch(context);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3c, RD#6) — Decision A chose in-place steer but no resumable
    /// child exists. Rather than silently doing B (which would LIE to the UI), the coordinator makes a
    /// CONSCIOUS <c>dispatch_fresh</c> decision: it emits <c>coordinator.steering_decision</c> with
    /// action=<c>dispatch_fresh</c> and a rationale, durably overrides the persisted
    /// <see cref="SteeringDirective.DecidedAction"/> to <c>dispatch_fresh</c> (persisted decision matches
    /// the real effect), THEN performs the reset+dispatch and settles the directive <c>applied</c>.
    /// </summary>
    private async Task ConsciousDispatchFreshFallbackAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        IReadOnlyList<int> targetSubtaskIds,
        int directiveId,
        string feedback,
        CancellationToken ct,
        string? rationale = null)
    {
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();

        rationale ??= "in_place_unresumable: no resumable child session — conscious fresh dispatch";
        _logger.LogInformation(
            "Steering(A→B): directive {DirectiveId} consciously falls back to dispatch_fresh for run {RunId} ({Rationale})",
            directiveId, context.CoordinatorRunId, rationale);

        // Durably record the real effect BEFORE acting so recovery can never observe a stale in_place
        // decision that no longer matches the effect.
        await decider.OverrideDecidedActionAsync(directiveId, SteeringDirection.DispatchFresh, ct)
            .ConfigureAwait(false);

        // Visible decision event (Ahmed's #8) — action MATCHES the actual effect (dispatch_fresh).
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
        {
            workPlanId,
            directiveId,
            decision = SteeringDirection.DispatchFresh,
            rationale,
            targetSubtaskIds,
        });

        var touched = targetSubtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        await RequestChangesAsync(
            context, workPlanId, edges,
            new AssemblyReviewDecision(
                Approved: false, RequestChanges: true, Feedback: feedback,
                TargetFiles: null, Reviewer: "coordinator"),
            touched, ct).ConfigureAwait(false);

        await decider.MarkDirectiveAppliedAsync(directiveId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §6, RD#2) — the per-directive EXECUTION retry budget is
    /// exhausted (a revision kept finishing/erroring before confirming its effect). Park the directive
    /// in the terminal <c>needs_attention</c> state, emit a visible event, and block the plan (retryable
    /// terminal). NEVER re-drive again.
    /// </summary>
    private async Task EscalateSteeringExecutionExhaustedAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        int directiveId,
        CancellationToken ct)
    {
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();
        await decider.MarkDirectiveNeedsAttentionAsync(directiveId, ct).ConfigureAwait(false);

        const string reason = "steering_execution_exhausted";
        _logger.LogWarning(
            "Steering(A): directive {DirectiveId} exhausted execution retries for run {RunId}; parking needs_attention + blocking plan",
            directiveId, context.CoordinatorRunId);

        await CleanupAssemblyBuildTestResourcesAsync(
            context.CoordinatorRunId, context.RepositoryPath, ct).ConfigureAwait(false);
        await _assemblyStore.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyBlocked, reason, ct).ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
        {
            workPlanId,
            directiveId,
            decision = SteeringDirection.Proceed,
            rationale = reason,
            phase = "execution_exhausted_needs_attention",
        });
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, new
        {
            workPlanId,
            reason,
            retryable = true,
        });
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.AssemblyBlocked, edges, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a subtask <see cref="SubtaskStatus.Running"/> while PRESERVING its <c>ChildRunId</c> (the
    /// in-place steer contract — context is NOT discarded). Records the steering
    /// <c>(directiveId, attempt)</c> that drove the resume on the subtask so a replayed drive is a
    /// no-op at the subtask level (complementing the durable RevisionEffectRecord probe).
    /// </summary>
    private async Task SetSubtaskRunningPreservingContextAsync(
        int subtaskId, string childRunId, int directiveId, int attempt, string guidance, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.Subtasks
            .Where(s => s.Id == subtaskId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, SubtaskStatus.Running)
                .SetProperty(s => s.ChildRunId, childRunId)
                .SetProperty(s => s.RecoveryGuidance, guidance)
                .SetProperty(s => s.LastResetDirectiveId, directiveId)
                .SetProperty(s => s.LastResetAttempt, attempt)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3d, recovery) — drives an outstanding
    /// <c>decided</c>/<c>executing</c> steering directive to completion at the start of an assembly pass
    /// (both the normal re-assembly after an in-place resume AND crash recovery). For a Decision-A
    /// (in-place) directive it probes the durable effect marker: <c>effect_confirmed</c> → advance the
    /// directive to <c>applied</c> (the resumed revision truly ran ≥1 superstep) and continue; marker
    /// absent/unconfirmed → RE-DRIVE the in-place resume exactly once (idempotent under the unique
    /// <c>(directiveId, attempt)</c> key) and return true so the caller aborts this assembly pass (the
    /// plan is now dispatching again). B/C directives left <c>decided</c>/<c>executing</c> by a crash
    /// are advanced to <c>applied</c> (their effect — plan status / reset — is already persisted).
    /// Returns true when it re-drove execution (caller must stop the current assembly pass).
    /// </summary>
    private async Task<bool> DriveOutstandingSteeringExecutionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        SteeringDirective? directive;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            directive = await db.SteeringDirectives.AsNoTracking()
                .Where(d => d.CoordinatorRunId == context.CoordinatorRunId
                    && (d.Status == SteeringStatus.Decided || d.Status == SteeringStatus.Executing))
                .OrderByDescending(d => d.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        if (directive is null)
            return false;

        // Resolve the decider only once an outstanding directive actually exists (keeps the common
        // no-steering assembly pass free of any steering-service dependency).
        var decider = _serviceProvider.GetRequiredService<CoordinatorSteeringDecider>();

        if (directive.DecidedAction != SteeringDirection.InPlaceSteer)
        {
            if (directive.DecidedAction == SteeringDirection.Proceed)
            {
                // C (Fix-B, change #1): a Proceed directive's durable effect is the human-review
                // ESCALATION, which is a MULTI-STEP effect (InReview transition + durable review request +
                // awaiting_review). It is unsafe to blindly mark it applied: a crash mid-escalation could
                // leave the plan not-yet-InReview or InReview-without-a-review-card. Verify the escalation
                // is durably OPEN — plan.Status == InReview AND a durable review request exists — before
                // settling. If not, RE-DRIVE the escalation (idempotent: ParkAtHumanReviewAsync CAS wins
                // only if not yet InReview, else completes a missing review card) so steering is never
                // silently dropped.
                var escalationOpen = await IsEscalationDurablyOpenAsync(
                    context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
                if (escalationOpen)
                {
                    await decider.MarkDirectiveAppliedAsync(directive.Id, ct).ConfigureAwait(false);
                    return false;
                }
                await ParkAtHumanReviewAsync(
                    context, workPlanId, directive.Id,
                    directive.Instruction, directive.TreeHash ?? string.Empty, ct).ConfigureAwait(false);
                // Re-drove the escalation; the review gate now owns the plan — stop this assembly pass.
                return true;
            }

            // B (DispatchFresh, change #1): a DispatchFresh directive at an assembly gate maps to a
            // LOCKOUT ROTATION — a MULTI-STEP effect (rotation-author selection, per-target guarded
            // rotation CAS, context-carrying handoff / fresh fallback, status re-arm, MarkDirectiveApplied).
            // Blindly marking it `applied` on a crash after MarkDirectiveExecutingAsync but before the
            // effect completed would silently DROP the rotation/handoff. Instead RE-DRIVE the rotation
            // (idempotent: TryRotateSubtaskAuthorAsync's guarded CAS + the durable (LastResetDirectiveId,
            // LastResetAttempt) stamp SKIP re-rotating an already-rotated target, and the handoff SKIPS a
            // target whose child already belongs to the rotated author — so a re-drive never double-rotates
            // nor double-dispatches). If the directive carries insufficient context to safely rebuild the
            // rotation (no target subtask ids), ESCALATE to human review rather than silently applying.
            var freshTargetIds = SteeringTargetScope.FromJson(directive.TargetScopeJson)?.SubtaskIds ?? [];
            if (freshTargetIds.Count == 0)
            {
                _logger.LogWarning(
                    "Steering(lockout): dispatch_fresh directive {DirectiveId} has no target subtask ids to re-drive " +
                    "for run {RunId} — escalating to human review instead of silently applying",
                    directive.Id, context.CoordinatorRunId);
                var noTargets = new Dictionary<int, IReadOnlySet<string>>();
                await EscalateToHumanReviewAsync(
                    context, workPlanId, edges, directive.Id,
                    "dispatch_fresh_insufficient_context", directive.TreeHash ?? string.Empty, noTargets, ct)
                    .ConfigureAwait(false);
                return true;
            }

            var freshAttempt = directive.ActionAttempt ?? 0;
            var freshTouched = freshTargetIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
            // #223: re-derive the transitive dependent closure from the SAME persisted implicated scope
            // + plan edges, so the crash-recovery re-drive redispatches (without lockout) exactly the set
            // the live path did.
            var freshDependents = AssemblyPlanning.TransitiveDependents(freshTargetIds, edges);
            _logger.LogInformation(
                "Steering(lockout): re-driving dispatch_fresh directive {DirectiveId} (attempt {Attempt}, targets [{Ids}]) " +
                "for run {RunId} — idempotent rotation re-drive (crash recovery)",
                directive.Id, freshAttempt, string.Join(",", freshTargetIds), context.CoordinatorRunId);
            await ExecuteLockoutRotationAsync(
                context, workPlanId, edges, freshTargetIds, freshDependents, directive.Id, freshAttempt,
                directive.Instruction, directive.TreeHash ?? string.Empty, freshTouched, ct)
                .ConfigureAwait(false);
            // Re-drove the rotation (which settles the directive + re-arms dispatch or escalates) — stop
            // this assembly pass so the re-armed dispatch loop re-observes the rotated/handed-off child.
            return true;
        }

        var attempt = directive.ActionAttempt ?? 0;

        // Resolve the target subtasks of this in-place directive with their AUTHORITATIVE status. The
        // in-place resume can target MULTIPLE subtasks; each is its own child run with its own per-child
        // effect marker (RD-B). The directive is driven by SUBTASK STATUS (not the effect marker alone):
        // any FAILED target → conscious dispatch_fresh; all eligible → applied; otherwise re-drive/wait.
        var targetIds = SteeringTargetScope.FromJson(directive.TargetScopeJson)?.SubtaskIds ?? [];
        List<(int Id, string Status, string? ChildRunId)> targets;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var rows = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == workPlanId && targetIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Status, s.ChildRunId })
                .ToListAsync(ct).ConfigureAwait(false);
            targets = rows.Select(r => (r.Id, r.Status, r.ChildRunId)).ToList();
        }

        // ROOT-CAUSE GUARD (live v0.9.12-rc1 assembly_failed): the durable per-child effect marker only
        // proves the in-place revision durably LAUNCHED and ran ≥1 superstep — it is NOT a proxy for the
        // revised subtask re-reaching a clean assemble_ready terminal. On real infra a resumed child ran a
        // full agent turn (agent.turn.end) but its run ended `watch_stream_completed_without_terminal_event`
        // (the resumed workflow never re-emitted the terminal WorkflowOutputEvent the watcher requires — a
        // Runtime/MAF checkpoint-resume seam), so the child run — and therefore the subtask — was marked
        // FAILED even though the effect marker was confirmed. Advancing to `applied` on the marker alone
        // then let the FAILED subtask fall through to the eligibility gate → assembly_blocked
        // (ineligible_subtasks) → terminal assembly_failed, with NO visible steering action (a "glitch").
        // The authoritative success signal for an in-place steer is the TARGET SUBTASK's status, not the
        // marker. A target that ended FAILED means the revision did not achieve its goal: do NOT mark the
        // directive applied; make a CONSCIOUS, VISIBLE dispatch_fresh decision (reset+dispatch a fresh pod
        // for the failed target only, preserving healthy targets) so the subtask re-enters assembly cleanly
        // — never a silent wedge (Ahmed's rule). This also subsumes the crash-before-first-checkpoint case
        // (effect unconfirmed + subtask FAILED), routing it to a bounded conscious fresh dispatch instead of
        // burning execution attempts on a doomed resume.
        var failedTargets = targets
            .Where(t => t.Status == SubtaskStatus.Failed)
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToList();
        if (failedTargets.Count > 0)
        {
            _logger.LogWarning(
                "Steering(A): directive {DirectiveId} in-place revision left target subtask(s) [{Failed}] FAILED " +
                "(revision child ended without a clean assemble_ready terminal); consciously falling back to " +
                "dispatch_fresh for run {RunId} instead of wedging assembly",
                directive.Id, string.Join(",", failedTargets), context.CoordinatorRunId);

            // Explicit, visible CAUSE event (Ahmed's "never a glitch") — the conscious dispatch_fresh
            // decision itself is emitted inside ConsciousDispatchFreshFallbackAsync.
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
            {
                workPlanId,
                directiveId = directive.Id,
                action = directive.DecidedAction,
                attempt,
                failedSubtaskIds = failedTargets,
                phase = "in_place_revision_failed_terminal",
            });

            await ConsciousDispatchFreshFallbackAsync(
                context, workPlanId, edges, failedTargets, directive.Id, directive.Instruction, ct,
                rationale: "in_place_revision_no_terminal: revised child ended without a clean assemble_ready " +
                    "terminal (marked failed) — conscious fresh dispatch so the subtask re-enters assembly")
                .ConfigureAwait(false);
            return true;
        }

        // Advance the directive to `applied` ONLY when BOTH conditions hold (AND, not OR):
        //   (i)  every target subtask reached a clean, assembly-eligible terminal
        //        (assemble_ready/completed) — the authoritative SUCCESS signal (fixes the live
        //        v0.9.12-rc1 wedge where the marker was confirmed but the child ended FAILED), AND
        //   (ii) every target child's PER-CHILD effect marker (directiveId, attempt, childRunId) is
        //        confirmed — proving the revision actually LAUNCHED and ran ≥1 superstep (RD-B).
        // Neither condition alone is sufficient:
        //   - Status-only is unsafe in the CRASH-BEFORE-LAUNCH window: MarkDirectiveExecutingAsync flips
        //     the directive to `executing` BEFORE ExecuteInPlaceSteerAsync launches the revision and flips
        //     the targets to Running. A crash in that gap leaves the targets holding their PRE-steer
        //     assemble_ready/completed status. Status-only would then read allEligible=true and mark the
        //     directive `applied` WITHOUT any revision ever having run → the steering feedback is silently
        //     DROPPED. Requiring the per-child effect marker closes this window: no launch ⇒ no marker ⇒
        //     not applied ⇒ re-drive ⇒ ExecuteInPlaceSteerAsync (re)launches the unconfirmed child (RD-A
        //     crash-before-first-checkpoint recovery).
        //   - Marker-only is the ORIGINAL live bug: the marker confirms on the first superstep while the
        //     child is still Running; the child then ended without a clean terminal and was marked FAILED
        //     AFTER the directive was already applied — leaving no outstanding directive to catch it, so
        //     assembly wedged silently. The eligibility condition + the failedTargets branch above catch
        //     that. A confirmed-but-still-Running child satisfies neither branch → falls through to the
        //     re-drive/wait below until it reaches a terminal, at which point this method re-evaluates.
        var expectedChildRunIds = targets
            .Select(t => t.ChildRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct()
            .ToList();
        var allEligible = targets.Count > 0
            && targets.All(t => t.Status is SubtaskStatus.AssembleReady or SubtaskStatus.Completed);
        // Every target must have a resumable child run id AND a confirmed per-child effect marker. If any
        // target lacks a ChildRunId it cannot have a marker → not confirmed → re-drive (which routes an
        // unresumable target to a conscious dispatch_fresh).
        var allEffectsConfirmed = allEligible
            && expectedChildRunIds.Count == targets.Count
            && await decider
                .AreAllRevisionEffectsConfirmedAsync(directive.Id, attempt, expectedChildRunIds, ct)
                .ConfigureAwait(false);
        if (allEligible && allEffectsConfirmed)
        {
            await decider.MarkDirectiveAppliedAsync(directive.Id, ct).ConfigureAwait(false);
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
            {
                workPlanId,
                directiveId = directive.Id,
                action = directive.DecidedAction,
                attempt,
                phase = "effect_confirmed_applied",
            });
            return false;
        }

        // No target has FAILED (handled above) and the applied gate did NOT fire — either not all targets
        // are eligible yet (a revision is still in flight) OR (crash-before-launch recovery) a target is
        // eligible-looking but its per-child effect marker is NOT confirmed (the revision never durably
        // launched). Re-drive: ExecuteInPlaceSteerAsync probes each target child's per-child effect marker
        // — a CONFIRMED child is SKIPPED (never re-injected, no duplicate turn), an UNCONFIRMED child is
        // (re)launched (RD-A crash-before-first-checkpoint recovery) — then returns the plan to dispatching
        // so assembly re-arms when the child reaches its terminal, at which point this method re-evaluates
        // the authoritative subtask status AND the marker. A confirmed-but-still-running child is thus
        // safely left to finish; an eligible-but-unconfirmed (crash-before-launch) target is relaunched so
        // the steering feedback is never silently dropped.
        await ExecuteInPlaceSteerAsync(
            context, workPlanId, edges, targetIds, directive.Id, attempt, directive.Instruction, ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task ParkBuildTestInfrastructureFailureAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        CollectiveBuildTestInfrastructureException ex,
        CancellationToken ct)
    {
        var status = ex.Retryable ? WorkPlanStatus.AssemblyBlocked : WorkPlanStatus.AssemblyFailed;
        var reason = $"build_test_infra_{ex.Reason}";
        var inner = InnermostException(ex);
        var detail = BuildInfrastructureFailureDetail(ex);
        await CleanupAssemblyBuildTestResourcesAsync(
            context.CoordinatorRunId, context.RepositoryPath, ct).ConfigureAwait(false);
        await _assemblyStore.SetTerminalStatusAsync(workPlanId, status, reason, ct).ConfigureAwait(false);

        if (ex.Retryable)
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, new
            {
                workPlanId,
                reason,
                detail,
                exceptionMessage = ex.Message,
                innerExceptionMessage = inner?.Message,
                innerExceptionType = inner?.GetType().FullName,
                infrastructureReason = ex.Reason,
                retryable = true,
            });
        }
        else
        {
            Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyFailed, new
            {
                workPlanId,
                reason,
                detail,
                exceptionMessage = ex.Message,
                innerExceptionMessage = inner?.Message,
                innerExceptionType = inner?.GetType().FullName,
                infrastructureReason = ex.Reason,
            });
            await TerminalizeCoordinatorRunAsync(
                context.CoordinatorRunId, RunStatus.Failed, reason, ct).ConfigureAwait(false);
        }

        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, status, edges, ct).ConfigureAwait(false);
        try
        {
            await PersistRunEventsSnapshotAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        }
        catch (Exception persistEx) when (persistEx is not OperationCanceledException)
        {
            _logger.LogWarning(persistEx,
                "Collective assembly: failed to persist Build/Test infrastructure failure events for run {RunId}",
                context.CoordinatorRunId);
        }
        _logger.LogWarning(ex,
            "Collective assembly Build/Test infrastructure failure for run {RunId}: {Reason} (retryable={Retryable}); detail={Detail}; inner={InnerMessage}",
            context.CoordinatorRunId, ex.Reason, ex.Retryable, detail, inner?.Message);
    }

    private static bool IsAutomatedAssemblyGateReviewer(string? reviewer) =>
        string.Equals(reviewer, "build-test", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reviewer, "rubberduck", StringComparison.OrdinalIgnoreCase);

    private static Exception? InnermostException(Exception ex)
    {
        var current = ex.InnerException;
        while (current?.InnerException is not null)
            current = current.InnerException;
        return current;
    }

    private static string BuildInfrastructureFailureDetail(CollectiveBuildTestInfrastructureException ex)
    {
        var inner = InnermostException(ex);
        return inner is null || string.Equals(inner.Message, ex.Message, StringComparison.Ordinal)
            ? ex.Message
            : $"{ex.Message} (inner: {inner.Message})";
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

        // #97: durably persist the assembly-blocked events (including the ENRICHED coordinator.assembly_blocked
        // payload — ineligibleSubtaskIds + the id/title/status/agent detail) to RunEvents RIGHT NOW, instead of
        // relying on the in-memory stream being flushed later by PersistAndCompleteStreamAsync. The block parks
        // the plan and WAITS (WaitForBlockedAssemblySteeringAsync) without completing the stream, so on a
        // reload/reconnect during that park the stream may have been evicted and the UI would otherwise be left
        // with only WorkPlan.AssemblyStatusReason (the opaque "ineligible_subtasks [ids]" code) and NO structured
        // detail — the exact opaque-error symptom in issue #97. Idempotent (skips already-persisted sequences)
        // and best-effort (a persistence fault must never break the block/park path).
        try
        {
            await PersistRunEventsSnapshotAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Collective assembly: failed to durably persist assembly_blocked detail for run {RunId}",
                context.CoordinatorRunId);
        }

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

    /// <summary>
    /// A RED RAI verdict is a safety stop, but not a terminal dead-end. Park the coordinator at the
    /// durable human-review gate so an accountable operator can approve, decline, or request a
    /// revision; recovery uses the ordinary InReview path.
    /// </summary>
    private async Task ParkRaiRedAtHumanReviewAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyCollection<(int, int)> edges,
        string aggregateTreeHash,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        string? feedback,
        CancellationToken ct)
    {
        var won = await _assemblyStore.TryEscalateToInReviewAsync(workPlanId, ct).ConfigureAwait(false);
        if (!won)
            return;

        var integrationBranch = IntegrationBranchName(context.CoordinatorRunId);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, context.CoordinatorRunId, context.SubmittingUser,
            integrationBranch, aggregateTreeHash, ct).ConfigureAwait(false);
        await MarkCoordinatorAwaitingReviewAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        await EmitGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        await EmitTopologyAsync(context.CoordinatorRunId, workPlanId, WorkPlanStatus.InReview, edges, ct)
            .ConfigureAwait(false);
        Emit(context.CoordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, new
        {
            workPlanId,
            integrationBranch,
            treeHash = aggregateTreeHash,
            gateKind = "human-review",
            escalated = true,
            reason = "rai_red",
            feedback,
        });

        var decision = await AwaitReviewDecisionAsync(context, workPlanId, edges, ct).ConfigureAwait(false);
        if (decision is not null)
        {
            await ApplyReviewDecisionAsync(
                context, workPlanId, edges, integrationBranch, aggregateTreeHash,
                touchedFilesBySubtask, decision, ct).ConfigureAwait(false);
        }
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
        string? repositoryPath = null;
        if (RunId.TryParse(coordinatorRunId, out var id))
        {
            var run = await _runStore.GetAsync(id, ct).ConfigureAwait(false);
            repositoryPath = run?.RepositoryPath;
            await _runStore.TrySetTerminalStatusAsync(id, status, DateTimeOffset.UtcNow, result, ct)
                .ConfigureAwait(false);
        }

        // CRITICAL (orphan cleanup): when assembly blocks/fails (e.g. ineligible_subtasks, rai_blocked,
        // review_timeout) the coordinator run terminates but its AgentHost pod (2 CPU / 4 Gi) would
        // otherwise keep running and eventually exhaust the namespace CPU quota. Release it best-effort.
        await CleanupAssemblyBuildTestResourcesAsync(coordinatorRunId, repositoryPath, ct).ConfigureAwait(false);
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

    private async Task CleanupAssemblyBuildTestResourcesAsync(
        string runId,
        string? repositoryPath,
        CancellationToken ct)
    {
        await StopPreviewsSafeAsync(runId, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(repositoryPath))
            await _pipeline.CleanupBuildTestResourcesAsync(runId, repositoryPath, ct).ConfigureAwait(false);
        else
            await ReleaseAgentHostPodSafeAsync(runId, ct).ConfigureAwait(false);
    }

    private async Task StopPreviewsSafeAsync(string runId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var previewService = scope.ServiceProvider.GetService<ISandboxPreviewService>();
            if (previewService is null || !previewService.Enabled)
                return;

            var previews = await previewService.ListForRunAsync(runId, ct).ConfigureAwait(false);
            foreach (var preview in previews)
                await previewService.StopPreviewAsync(preview.Token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "CoordinatorAssemblyService: failed to stop Build/Test previews for run {RunId} (best-effort)",
                runId);
        }
    }

    private async Task EnsurePreviewApplicabilityRecordedAsync(
        string coordinatorRunId,
        int workPlanId,
        string treeHash,
        string aggregateDiff,
        CancellationToken ct)
    {
        if (FindLatestPreviewState(coordinatorRunId, workPlanId, treeHash).Kind is not PreviewOutcomeKind.None)
            return;

        var applicability = InferPreviewApplicability(aggregateDiff);
        Emit(coordinatorRunId, EventTypes.WorkflowStep, new
        {
            step = "preview",
            status = applicability.Required ? "started" : "skipped",
            label = "Preview",
            message = applicability.Required
                ? "Preview applicability requires a live preview outcome before human review."
                : "Preview is not applicable for this assembly.",
        });
        Emit(coordinatorRunId, EventTypes.SandboxPreviewApplicability, new
        {
            run_id = coordinatorRunId,
            work_plan_id = workPlanId,
            tree_hash = treeHash,
            state = applicability.Required ? "preview_required" : "preview_skipped_not_applicable",
            reason = applicability.Reason,
            evidence = applicability.Evidence,
        });

        if (!applicability.Required)
        {
            Emit(coordinatorRunId, EventTypes.SandboxPreviewSkippedNotApplicable, new
            {
                run_id = coordinatorRunId,
                work_plan_id = workPlanId,
                tree_hash = treeHash,
                source = "preview-stage",
                reason = applicability.Reason,
                evidence = applicability.Evidence,
            });
        }

        await PersistRunEventsSnapshotAsync(coordinatorRunId, ct).ConfigureAwait(false);
    }

    private async Task<PreviewOutcomeState> EnsureFinalPreviewOutcomeBeforeApprovalAsync(
        string coordinatorRunId,
        int workPlanId,
        string treeHash,
        CancellationToken ct)
    {
        var outcome = FindLatestPreviewState(coordinatorRunId, workPlanId, treeHash);
        if (outcome.Kind == PreviewOutcomeKind.Pending)
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                outcome = FindLatestPreviewState(coordinatorRunId, workPlanId, treeHash);
                if (outcome.Kind is PreviewOutcomeKind.Ready or PreviewOutcomeKind.Failed or PreviewOutcomeKind.Skipped)
                    return outcome;
            }
        }

        if (outcome.Kind is PreviewOutcomeKind.Ready or PreviewOutcomeKind.Failed or PreviewOutcomeKind.Skipped)
            return outcome;

        Emit(coordinatorRunId, EventTypes.SandboxPreviewFailed, new
        {
            run_id = coordinatorRunId,
            work_plan_id = workPlanId,
            tree_hash = treeHash,
            source = "approval-guard",
            reason = "preview_outcome_missing",
            message = "Build/Test completed without a final live-preview outcome; Human Review may proceed with preview unavailable.",
        });
        Emit(coordinatorRunId, EventTypes.WorkflowStep, new
        {
            step = "preview",
            status = "failed",
            label = "Preview",
            message = "Preview unavailable: no final preview outcome was recorded before Build/Test approval.",
        });
        await PersistRunEventsSnapshotAsync(coordinatorRunId, ct).ConfigureAwait(false);
        return new PreviewOutcomeState(PreviewOutcomeKind.Failed, "preview_outcome_missing");
    }

    private PreviewOutcomeState FindLatestPreviewState(string coordinatorRunId, int workPlanId, string treeHash)
    {
        var events = _streamStore.Get(coordinatorRunId)?.GetSnapshotSince(0).Events;
        if (events is null || events.Count == 0)
            return new PreviewOutcomeState(PreviewOutcomeKind.None, null);

        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (!IsPreviewStateEvent(evt.Type))
                continue;

            var payload = JsonSerializer.SerializeToNode(evt.Payload) as System.Text.Json.Nodes.JsonObject;
            if (payload is null || !PreviewKeyMatches(evt.Type, payload, workPlanId, treeHash))
                continue;

            if (evt.Type == EventTypes.SandboxPreviewReady)
                return new PreviewOutcomeState(PreviewOutcomeKind.Ready, null);
            if (evt.Type == EventTypes.SandboxPreviewFailed)
                return new PreviewOutcomeState(PreviewOutcomeKind.Failed, GetString(payload, "reason"));
            if (evt.Type == EventTypes.SandboxPreviewSkippedNotApplicable)
                return new PreviewOutcomeState(PreviewOutcomeKind.Skipped, GetString(payload, "reason"));
            if (evt.Type == EventTypes.SandboxPreviewPending)
                return new PreviewOutcomeState(PreviewOutcomeKind.Pending, null);
            if (evt.Type == EventTypes.SandboxPreviewApplicability
                && string.Equals(GetString(payload, "state"), "preview_skipped_not_applicable", StringComparison.Ordinal))
                return new PreviewOutcomeState(PreviewOutcomeKind.Skipped, GetString(payload, "reason"));
        }

        return new PreviewOutcomeState(PreviewOutcomeKind.None, null);
    }

    private static bool IsPreviewStateEvent(string type) =>
        type == EventTypes.SandboxPreviewApplicability
        || type == EventTypes.SandboxPreviewReady
        || type == EventTypes.SandboxPreviewFailed
        || type == EventTypes.SandboxPreviewSkippedNotApplicable
        || type == EventTypes.SandboxPreviewPending;

    private static bool PreviewKeyMatches(
        string eventType,
        System.Text.Json.Nodes.JsonObject payload,
        int workPlanId,
        string treeHash)
    {
        var payloadWorkPlanId = GetInt(payload, "work_plan_id") ?? GetInt(payload, "workPlanId");
        var payloadTreeHash = GetString(payload, "tree_hash") ?? GetString(payload, "treeHash");

        if (eventType == EventTypes.SandboxPreviewPending)
        {
            return payloadWorkPlanId == workPlanId
                && !string.IsNullOrWhiteSpace(payloadTreeHash)
                && !string.IsNullOrWhiteSpace(treeHash)
                && string.Equals(payloadTreeHash, treeHash, StringComparison.Ordinal);
        }

        return (payloadWorkPlanId is null || payloadWorkPlanId == workPlanId)
            && (string.IsNullOrWhiteSpace(payloadTreeHash)
                || string.IsNullOrWhiteSpace(treeHash)
                || string.Equals(payloadTreeHash, treeHash, StringComparison.Ordinal));
    }

    private static int? GetInt(System.Text.Json.Nodes.JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        try { return node.GetValue<int>(); }
        catch { return null; }
    }

    private static string? GetString(System.Text.Json.Nodes.JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        try { return node.GetValue<string>(); }
        catch { return null; }
    }

    private static (bool Required, string Reason, IReadOnlyList<string> Evidence) InferPreviewApplicability(string aggregateDiff)
    {
        var changedFiles = ExtractDiffFiles(aggregateDiff);
        if (changedFiles.Count > 0
            && changedFiles.All(IsDocumentationOnlyPath))
            return (false, "docs_only", changedFiles);

        var previewEvidence = changedFiles
            .Where(path => path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.Contains("server", StringComparison.OrdinalIgnoreCase)
                || path.Contains("controller", StringComparison.OrdinalIgnoreCase)
                || path.Contains("app", StringComparison.OrdinalIgnoreCase)
                || path.Contains("vite", StringComparison.OrdinalIgnoreCase)
                || path.Contains("next", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();

        return previewEvidence.Length > 0
            ? (true, "server_files_changed", previewEvidence)
            : (true, "ambiguous_default_required", changedFiles.Take(8).ToArray());
    }

    private static IReadOnlyList<string> ExtractDiffFiles(string aggregateDiff)
    {
        if (string.IsNullOrWhiteSpace(aggregateDiff))
            return [];

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in aggregateDiff.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("diff --git ", StringComparison.Ordinal))
                continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var file = parts[3].StartsWith("b/", StringComparison.Ordinal) ? parts[3][2..] : parts[3];
                files.Add(file.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return files.ToArray();
    }

    private static bool IsDocumentationOnlyPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".adoc", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith($"docs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreviewOnlyFeedback(string? feedback) =>
        !string.IsNullOrWhiteSpace(feedback)
        && feedback.Contains("preview", StringComparison.OrdinalIgnoreCase)
        && !feedback.Contains("test", StringComparison.OrdinalIgnoreCase)
        && !feedback.Contains("build", StringComparison.OrdinalIgnoreCase)
        && !feedback.Contains("compile", StringComparison.OrdinalIgnoreCase);

    private void Emit(string coordinatorRunId, string eventType, object payload) =>
        _streamStore.Get(coordinatorRunId)?.RecordNext(eventType, StampTimestamp(payload));

    private enum PreviewOutcomeKind { None, Pending, Ready, Failed, Skipped }

    private sealed record PreviewOutcomeState(PreviewOutcomeKind Kind, string? Reason);

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
            await PersistRunEventsSnapshotAsync(coordinatorRunId, CancellationToken.None).ConfigureAwait(false);
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

    private async Task PersistRunEventsSnapshotAsync(string coordinatorRunId, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        var events = entry?.GetSnapshotSince(0).Events;
        if (events is not { Count: > 0 })
            return;

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
                PayloadJson = JsonSerializer.Serialize(e.Payload),
                CreatedAt = DateTime.UtcNow,
            })
            .ToList();

        if (toInsert.Count > 0)
        {
            db.RunEvents.AddRange(toInsert);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        var coordinatorModel = (await TryGetCoordinatorRunAsync(coordinatorRunId, ct).ConfigureAwait(false))?.ModelId;
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
                assemblyGates: gates,
                coordinatorModel: coordinatorModel));
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

    private async Task ResetSubtasksToPendingAsync(
        string coordinatorRunId, IReadOnlyCollection<int> subtaskIds, string feedback, CancellationToken ct)
    {
        if (subtaskIds.Count == 0) return;
        // Req-1 (change #1 + #2): for EACH target subtask, build the STABLE AccumulatedReviewFeedback
        // handoff bundle (target+rejection-scoped prior rounds + prior worktree branch) and write its
        // deterministic RenderedGuidance so a fresh/rotated pod addresses every prior requirement AND
        // builds on the prior work — fixing the amnesia loop. Bundles are built BEFORE ChildRunId is
        // cleared, capturing the prior child pointer (PriorChildRunId) that Morpheus's
        // StartChildRevisionHandoffAsync consumes.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Subtasks
            .Where(s => subtaskIds.Contains(s.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in rows)
        {
            var priorChild = s.ChildRunId;
            var bundle = await BuildAccumulatedReviewFeedbackAsync(
                coordinatorRunId, s.Id, feedback, priorChild, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(priorChild))
                s.PriorChildRunId = priorChild;
            s.Status = SubtaskStatus.Pending;
            s.ChildRunId = null;
            s.RecoveryGuidance = bundle.RenderedGuidance;
            s.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Req-1, changes #1/#2) — builds the STABLE
    /// <see cref="AccumulatedReviewFeedback"/> runtime handoff contract (Fix-A "3b") for ONE subtask's
    /// revision. The coordinator is the SINGLE SOURCE OF TRUTH: consumers (e.g. Morpheus's
    /// StartChildRevisionHandoffAsync) MUST consume this DTO (or its <c>RenderedGuidance</c>) and MUST
    /// NOT read <c>SteeringDirective</c> rows directly. <see cref="AccumulatedReviewFeedback.PriorRounds"/>
    /// is TARGET-scoped (only rounds that targeted this subtask) and REJECTION-scoped (request-changes /
    /// blocking only, never advisories — the discriminator). <paramref name="priorChildRunId"/> (usually
    /// the subtask's prior <c>ChildRunId</c>, captured before a reset clears it) resolves the prior
    /// worktree branch so the new agent REUSES the branch while minting a NEW session.
    /// </summary>
    internal async Task<AccumulatedReviewFeedback> BuildAccumulatedReviewFeedbackAsync(
        string coordinatorRunId, int subtaskId, string currentChangeRequest,
        string? priorChildRunId, CancellationToken ct)
    {
        var priorRounds = await BuildPriorReviewRoundsAsync(coordinatorRunId, new[] { subtaskId }, ct)
            .ConfigureAwait(false);

        // Resolve the prior worktree branch (so the consumer can reuse the branch/worktree while minting
        // a NEW SDK session for the non-locked-out agent). Fall back to the integration branch.
        var priorWorktreeBranch = IntegrationBranchName(coordinatorRunId);
        if (!string.IsNullOrWhiteSpace(priorChildRunId) && RunId.TryParse(priorChildRunId, out var priorRunId))
        {
            var priorRun = await _runStore.GetAsync(priorRunId, ct).ConfigureAwait(false);
            if (priorRun is not null && !string.IsNullOrWhiteSpace(priorRun.WorktreeBranch))
                priorWorktreeBranch = priorRun.WorktreeBranch;
        }

        var bundle = new AccumulatedReviewFeedback(
            SubtaskId: subtaskId.ToString(),
            CurrentChangeRequest: currentChangeRequest,
            PriorRounds: priorRounds,
            PriorWorktreeBranch: priorWorktreeBranch);
        return bundle with { RenderedGuidance = bundle.RenderForRevisionPrompt() };
    }

    /// <summary>
    /// The TARGET-scoped, REJECTION-scoped prior review rounds for a single subtask, ordered
    /// oldest→newest. Reads the run's <c>SteeringDirective</c> rows filtered to request-changes/blocking
    /// severities (a reviewer rejection, not an advisory) whose target scope includes this subtask (or is
    /// plan-wide). Unlike <see cref="BuildAccumulatedGateFeedbackAsync"/> (which aggregates ALL run
    /// directives for the human REVIEW CARD), this is scoped to ONE subtask for the child-retry handoff.
    /// </summary>
    internal async Task<IReadOnlyList<ReviewFeedbackRound>> BuildPriorReviewRoundsAsync(
        string coordinatorRunId, IReadOnlyCollection<int> subtaskIds, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.SteeringDirectives.AsNoTracking()
            .Where(d => d.CoordinatorRunId == coordinatorRunId
                && d.Instruction != null
                && (d.Severity == SteeringSeverity.RequestChanges || d.Severity == SteeringSeverity.Blocking))
            .OrderBy(d => d.Id)
            .Select(d => new { d.Source, d.CreatedBy, d.Instruction, d.TargetScopeJson, d.CreatedAt })
            .Take(64)
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<ReviewFeedbackRound>();
        var round = 0;
        foreach (var r in rows)
        {
            // TARGET-scoped: keep a directive only when it targeted one of THESE subtasks (or is plan-wide
            // with no explicit subtask scope). TargetScopeJson parses in memory (the id set is JSON, not
            // queryable).
            var scopeIds = SteeringTargetScope.FromJson(r.TargetScopeJson)?.SubtaskIds;
            var targetsThese = scopeIds is null || scopeIds.Count == 0
                || subtaskIds.Count == 0 || scopeIds.Any(subtaskIds.Contains);
            if (!targetsThese)
                continue;
            round++;
            var reviewer = string.IsNullOrWhiteSpace(r.CreatedBy) ? (r.Source ?? "gate") : r.CreatedBy!;
            result.Add(new ReviewFeedbackRound(
                round, reviewer, Truncate(r.Instruction, 2000) ?? string.Empty, r.CreatedAt));
        }
        return result;
    }


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

        while (!ct.IsCancellationRequested)
        {
            var runStatus = await GetCoordinatorRunStatusAsync(coordinatorRunId, ct).ConfigureAwait(false);
            if (runStatus is RunStatus.Completed or RunStatus.Failed or RunStatus.Merged or RunStatus.Declined or RunStatus.MergeFailed)
                return BlockedAssemblyOutcome.Terminalized;

            var planStatus = await GetWorkPlanStatusAsync(workPlanId, ct).ConfigureAwait(false);
            if (planStatus != WorkPlanStatus.AssemblyBlocked)
                return BlockedAssemblyOutcome.DispatchResumed;

            // #242 defense-in-depth: re-resolve the persisted block reason FRESH on every tick instead
            // of trusting the value captured once when this wait began (the previous behavior). Smith's
            // live FitTrackE2E-v12 wedge (decisions/inbox/smith-fittrack-priority1.md) showed a run
            // parked with a stale cached reason that did not match the "ineligible_subtasks" class at
            // the moment the wait started, which permanently disabled eligibility polling for the WHOLE
            // steering-wait window (default 10 min, Coordinator:AssemblyBlockedSteeringTimeoutMinutes)
            // even though every subtask subsequently went assembly-eligible — the run then silently
            // timed out and auto-terminalized despite being, in fact, recoverable. Re-reading the
            // persisted reason each iteration (same cadence as the existing run/plan-status reads just
            // above) lets a plan whose reason is later corrected/updated to ineligible_subtasks recover
            // normally instead of blindly waiting out the full timeout on a snapshot taken at entry.
            var currentReason = await ResolveBlockedAssemblyReasonAsync(coordinatorRunId, fallback: null, ct)
                .ConfigureAwait(false);
            if (CanRecoverBlockedAssemblyOnEligibility(currentReason)
                && await AreSubtasksAssemblyEligibleAsync(workPlanId, ct).ConfigureAwait(false))
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
