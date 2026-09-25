using System.Data;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using DomainRun = Agentweaver.Domain.Run;
using DomainRunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Workflows;

internal static class WorkflowChildWorkResumeStates
{
    public const string Committed = "committed";
    public const string Waiting = "waiting";
    public const string Ready = "ready";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
    public const string Suppressed = "suppressed";
}

internal sealed record StaticWorkflowBranch(
    string NodeId,
    string Title,
    string Scope,
    string AssignedAgent,
    string SelectedModelId,
    string Phase = "execution",
    string IsolationStrategy = "shared",
    IReadOnlyList<string>? DeclaredOutputPaths = null,
    string? AgentCharter = null);

internal sealed record WorkflowChildWorkRequest(
    DomainRun ParentRun,
    string ParentWorkflowId,
    string ParentWorkflowNodeId,
    string? ParentJoinNodeId,
    IReadOnlyList<StaticWorkflowBranch> Branches);

internal sealed record WorkflowChildWorkAttachment(
    int WorkPlanId,
    string ChildCoordinatorRunId,
    string ParentResumeRequestId,
    string ParentResumeState,
    bool Reattached,
    IReadOnlyList<WorkflowChildWorkBranch> Branches);

internal sealed record WorkflowChildWorkBranch(
    int SubtaskId,
    string NodeId,
    int Ordinal,
    string Status,
    string? ChildRunId);

internal sealed record WorkflowChildWorkResult(
    int WorkPlanId,
    string ChildCoordinatorRunId,
    string ParentWorkflowId,
    string ParentWorkflowNodeId,
    string? ParentJoinNodeId,
    bool Succeeded,
    string WorkPlanStatus,
    string? FailureReason,
    IReadOnlyList<WorkflowChildWorkBranch> Branches);

internal interface IWorkflowChildWorkRuntime
{
    bool DispatchEnabled { get; }
    bool IsDispatchActive(string coordinatorRunId);
    bool IsParentResumeActive(string parentRunId);
    void StartDispatch(CoordinatorDispatchContext context);
    Task<bool> TryDeliverParentResumeAsync(
        string parentRunId,
        PendingDelivery delivery,
        WorkflowChildWorkResult result,
        CancellationToken ct);
    Task CancelRunAsync(DomainRun run, CancellationToken ct);
}

internal sealed class WorkflowChildWorkRuntime(
    RunWorkflowRegistry workflowRegistry,
    IRunStore runStore,
    RunStreamStore streamStore,
    IWorktreeOperations worktreeOperations,
    IServiceProvider services,
    IOptions<SandboxRuntimeOptions> sandboxRuntime,
    ILogger<WorkflowChildWorkRuntime> logger) : IWorkflowChildWorkRuntime
{
    public bool DispatchEnabled => false;

    public bool IsDispatchActive(string coordinatorRunId) => false;

    public bool IsParentResumeActive(string parentRunId) => workflowRegistry.Get(parentRunId) is not null;

    public void StartDispatch(CoordinatorDispatchContext context) =>
        throw new InvalidOperationException(
            "Workflow child-work dispatch is intentionally disabled until fan node executors are implemented.");

    public async Task<bool> TryDeliverParentResumeAsync(
        string parentRunId,
        PendingDelivery delivery,
        WorkflowChildWorkResult result,
        CancellationToken ct)
    {
        var streamingRun = workflowRegistry.Get(parentRunId);
        if (streamingRun is null)
            return false;

        await streamingRun.SendResponseAsync(delivery.Request.CreateResponse(result)).ConfigureAwait(false);
        return true;
    }

    public Task CancelRunAsync(DomainRun run, CancellationToken ct) =>
        EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            workflowRegistry,
            worktreeOperations,
            logger,
            ct,
            services.GetService<IAgentHostPodLifecycle>(),
            sandboxRuntime.Value);
}

/// <summary>
/// Durable, runtime-hidden parent-to-child work substrate shared by future workflow fan and composed
/// nodes. It persists correlation, declared static branches, and the parent continuation before
/// coordinator dispatch is allowed. No workflow node is bound to this service in PR A.
/// </summary>
internal sealed class WorkflowChildWorkService
{
    private static readonly TimeSpan DeliveryClaimStaleAfter = TimeSpan.FromSeconds(15);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunStore _runStore;
    private readonly PendingRequestStore _pendingRequests;
    private readonly IWorkflowChildWorkRuntime _runtime;
    private readonly ILogger<WorkflowChildWorkService> _logger;
    private readonly string _podId;
    private readonly TimeSpan _dispatchLeaseStaleAfter;

    public WorkflowChildWorkService(
        IServiceScopeFactory scopeFactory,
        IRunStore runStore,
        PendingRequestStore pendingRequests,
        IWorkflowChildWorkRuntime runtime,
        ILogger<WorkflowChildWorkService> logger,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _runStore = runStore;
        _pendingRequests = pendingRequests;
        _runtime = runtime;
        _logger = logger;
        _podId = configuration?.GetValue<string>("App:PodId")
            ?? Environment.GetEnvironmentVariable("HOSTNAME")
            ?? Environment.MachineName;
        var staleSeconds = configuration?.GetValue("Coordinator:PodLeaseStaleTtlSeconds", 120) ?? 120;
        _dispatchLeaseStaleAfter = TimeSpan.FromSeconds(Math.Max(10, staleSeconds));
    }

    internal static string ChildCoordinatorSubtaskKey(string parentWorkflowNodeId) =>
        $"workflow-node:{parentWorkflowNodeId}";

    internal static string ResumeRequestId(string parentRunId, string parentWorkflowNodeId, int workPlanId) =>
        $"workflow-child-work:{parentRunId}:{parentWorkflowNodeId}:{workPlanId}";

    public async Task<WorkflowChildWorkAttachment> CreateOrReattachStaticAsync(
        WorkflowChildWorkRequest request,
        Func<string, ExternalRequest> continuationFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(continuationFactory);
        ValidateRequest(request);

        var (plan, reattached) = await EnsurePersistedAsync(request, ct).ConfigureAwait(false);
        await EnsureChildCoordinatorRunAsync(plan, request.ParentRun, ct).ConfigureAwait(false);

        var requestId = ResumeRequestId(
            request.ParentRun.Id.ToString(),
            request.ParentWorkflowNodeId,
            plan.Id);
        var continuation = continuationFactory(requestId);
        if (!string.Equals(continuation.RequestId, requestId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Workflow child continuation request id must be the stable id '{requestId}'.");

        await _pendingRequests.SetAsync(
            request.ParentRun.Id.ToString(),
            continuation,
            request.ParentRun.SubmittingUser,
            ct).ConfigureAwait(false);
        await MarkContinuationArmedAsync(plan.Id, requestId, ct).ConfigureAwait(false);
        await EnsureParentWaitingAsync(plan, request.ParentRun, ct).ConfigureAwait(false);

        return await GetAttachmentAsync(plan.Id, reattached, ct).ConfigureAwait(false);
    }

    internal async Task<(WorkPlan Plan, bool Reattached)> EnsurePersistedAsync(
        WorkflowChildWorkRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var parentRunId = request.ParentRun.Id.ToString();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var existing = await FindCorrelatedPlanAsync(
                db, parentRunId, request.ParentWorkflowNodeId, ct).ConfigureAwait(false);
            if (existing is not null)
                return (existing, true);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, ct)
                .ConfigureAwait(false);

            var existing = await FindCorrelatedPlanAsync(
                db, parentRunId, request.ParentWorkflowNodeId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return (existing, true);
            }

            var now = DateTimeOffset.UtcNow;
            var childRunId = RunId.New().ToString();
            var outcomeSpec = new OutcomeSpec
            {
                ProjectId = request.ParentRun.ProjectId?.ToString() ?? string.Empty,
                CoordinatorRunId = childRunId,
                Goal = $"Execute workflow child work for node '{request.ParentWorkflowNodeId}'.",
                DesiredOutcome = "Complete every declared static branch and return one ordered result.",
                Scope = $"Pinned parent workflow '{request.ParentWorkflowId}', node '{request.ParentWorkflowNodeId}'.",
                Assumptions = "Static branch declarations are immutable for this parent run and workflow node.",
                Status = "confirmed",
                ConfirmedBy = request.ParentRun.SubmittingUser,
                AllowTaskPromotion = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OutcomeSpecs.Add(outcomeSpec);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var plan = new WorkPlan
            {
                OutcomeSpecId = outcomeSpec.Id,
                ProjectId = outcomeSpec.ProjectId,
                CoordinatorRunId = childRunId,
                ParentRunId = parentRunId,
                ParentWorkflowId = request.ParentWorkflowId,
                ParentWorkflowNodeId = request.ParentWorkflowNodeId,
                ParentJoinNodeId = request.ParentJoinNodeId,
                ParentResumeState = WorkflowChildWorkResumeStates.Committed,
                WorkflowId = request.ParentWorkflowId,
                Status = WorkPlanStatus.Planned,
                IsolationSummary = "Static workflow branches; parent continuation must be armed before dispatch.",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkPlans.Add(plan);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            for (var ordinal = 0; ordinal < request.Branches.Count; ordinal++)
            {
                var branch = request.Branches[ordinal];
                db.Subtasks.Add(new Subtask
                {
                    WorkPlanId = plan.Id,
                    WorkflowBranchNodeId = branch.NodeId,
                    WorkflowBranchOrdinal = ordinal,
                    Title = branch.Title,
                    Scope = branch.Scope,
                    AssignedAgent = branch.AssignedAgent,
                    SelectedModelId = branch.SelectedModelId,
                    Phase = branch.Phase,
                    IsolationStrategy = branch.IsolationStrategy,
                    DeclaredOutputPathsJson = JsonSerializer.Serialize(
                        branch.DeclaredOutputPaths ?? [],
                        JsonDefaults.Options),
                    Status = SubtaskStatus.Pending,
                    AgentCharter = branch.AgentCharter,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            plan.ParentResumeRequestId = ResumeRequestId(parentRunId, request.ParentWorkflowNodeId, plan.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (plan, false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var winner = await FindCorrelatedPlanAsync(
                db, parentRunId, request.ParentWorkflowNodeId, ct).ConfigureAwait(false);
            if (winner is null)
                throw;
            return (winner, true);
        }
    }

    public async Task<bool> TryStartDispatchAsync(int workPlanId, CancellationToken ct = default)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot is null || snapshot.Plan.ParentRunId is null)
            return false;

        var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
        if (parent is null)
            return false;
        if (TerminalRunOutcome.IsTerminal(parent.Status))
        {
            await SuppressAndCancelAsync(snapshot.Plan, ct).ConfigureAwait(false);
            return false;
        }
        if (parent.Status != DomainRunStatus.AwaitingReview)
            return false;

        if (snapshot.Plan.ParentResumeState != WorkflowChildWorkResumeStates.Waiting
            || string.IsNullOrWhiteSpace(snapshot.Plan.ParentResumeRequestId)
            || !await _pendingRequests.ExistsForRequestAsync(
                snapshot.Plan.ParentRunId,
                snapshot.Plan.ParentResumeRequestId,
                ct).ConfigureAwait(false))
            return false;
        if (!_runtime.DispatchEnabled)
            return false;

        var child = await EnsureChildCoordinatorRunAsync(snapshot.Plan, parent, ct).ConfigureAwait(false);
        if (TerminalRunOutcome.IsTerminal(child.Status))
            return false;

        if (!await TryClaimDispatchAsync(workPlanId, ct).ConfigureAwait(false))
            return false;

        parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
        if (parent is null || parent.Status != DomainRunStatus.AwaitingReview)
        {
            if (parent is not null && TerminalRunOutcome.IsTerminal(parent.Status))
                await SuppressAndCancelAsync(snapshot.Plan, ct).ConfigureAwait(false);
            return false;
        }

        if (child.Status == DomainRunStatus.Pending)
        {
            await _runStore.UpdateStatusAsync(
                child.Id,
                DomainRunStatus.InProgress,
                endedAt: null,
                ct).ConfigureAwait(false);
            child = child with { Status = DomainRunStatus.InProgress };
        }

        if (!_runtime.IsDispatchActive(snapshot.Plan.CoordinatorRunId))
        {
            _runtime.StartDispatch(new CoordinatorDispatchContext(
                snapshot.Plan.CoordinatorRunId,
                child.RepositoryPath,
                child.OriginatingBranch,
                child.SubmittingUser,
                child.ProjectId));
        }

        return true;
    }

    public async Task<bool> TryPrepareResumeAsync(int workPlanId, CancellationToken ct = default)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot is null
            || snapshot.Plan.ParentRunId is null
            || snapshot.Plan.ParentWorkflowId is null
            || snapshot.Plan.ParentWorkflowNodeId is null
            || snapshot.Plan.ParentResumeRequestId is null
            || !IsTerminalPlan(snapshot.Plan.Status))
            return false;

        var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
        if (parent is null)
            return false;
        if (TerminalRunOutcome.IsTerminal(parent.Status))
        {
            await SuppressAndCancelAsync(snapshot.Plan, ct).ConfigureAwait(false);
            return false;
        }
        if (parent.Status != DomainRunStatus.AwaitingReview)
            return false;

        WorkflowChildWorkResult result;
        if (snapshot.Plan.ParentResumeResultJson is null)
        {
            var statusById = snapshot.Branches.ToDictionary(branch => branch.SubtaskId, branch => branch.Status);
            var succeeded = snapshot.Plan.Status == WorkPlanStatus.Complete
                && AssemblyPlanning.AllEligible(statusById);
            result = new WorkflowChildWorkResult(
                snapshot.Plan.Id,
                snapshot.Plan.CoordinatorRunId,
                snapshot.Plan.ParentWorkflowId,
                snapshot.Plan.ParentWorkflowNodeId,
                snapshot.Plan.ParentJoinNodeId,
                succeeded,
                snapshot.Plan.Status,
                succeeded ? null : snapshot.Plan.AssemblyStatusReason ?? snapshot.Plan.Status,
                snapshot.Branches);
            var resultJson = JsonSerializer.Serialize(result, JsonDefaults.Options);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.WorkPlans
                .Where(plan => plan.Id == workPlanId
                    && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                    && plan.ParentResumeResultJson == null)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.ParentResumeResultJson, resultJson)
                    .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Ready)
                    .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }

        snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot?.Plan.ParentResumeResultJson is null)
            return false;
        result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
            snapshot.Plan.ParentResumeResultJson,
            JsonDefaults.Options)
            ?? throw new InvalidOperationException($"Work plan {workPlanId} has an invalid parent resume result.");

        var identity = PendingRequestStore.CreateDecisionIdentity(
            snapshot.Plan.ParentResumeRequestId!,
            result);
        return await _pendingRequests.TryQueueDeliveryAsync(
            snapshot.Plan.ParentRunId!,
            PendingRequestDeliveryKinds.WorkflowChildWork,
            identity,
            result,
            parent.SubmittingUser,
            ct).ConfigureAwait(false);
    }

    public async Task<bool> TryDeliverResumeAsync(
        int workPlanId,
        string claimOwner,
        TimeSpan? staleAfter = null,
        CancellationToken ct = default)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot is null
            || snapshot.Plan.ParentRunId is null
            || snapshot.Plan.ParentResumeResultJson is null
            || snapshot.Plan.ParentResumeState is not (
                WorkflowChildWorkResumeStates.Ready or WorkflowChildWorkResumeStates.Delivering))
            return false;

        var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
        if (parent is null)
            return false;
        if (TerminalRunOutcome.IsTerminal(parent.Status))
        {
            await SuppressAndCancelAsync(snapshot.Plan, ct).ConfigureAwait(false);
            return false;
        }
        if (parent.Status != DomainRunStatus.AwaitingReview)
            return false;

        var pendingState = await _pendingRequests.GetDeliveryStateAsync(
            snapshot.Plan.ParentRunId,
            PendingRequestDeliveryKinds.WorkflowChildWork,
            ct).ConfigureAwait(false);
        if (pendingState?.State == PendingRequestDeliveryStates.Delivered)
        {
            await MarkPlanDeliveredAsync(
                workPlanId,
                snapshot.Plan.ParentResumeClaimOwner,
                snapshot.Plan.ParentResumeClaimedAt,
                pendingState.DeliveredAt,
                ct).ConfigureAwait(false);
            return false;
        }

        var delivery = await _pendingRequests.TryClaimDeliveryAsync(
            snapshot.Plan.ParentRunId,
            claimOwner,
            staleAfter ?? DeliveryClaimStaleAfter,
            ct).ConfigureAwait(false);
        if (delivery is null
            || !string.Equals(
                delivery.DeliveryKind,
                PendingRequestDeliveryKinds.WorkflowChildWork,
                StringComparison.Ordinal))
            return false;

        if (!_runtime.IsParentResumeActive(snapshot.Plan.ParentRunId))
        {
            await _pendingRequests.ReleaseDeliveryAsync(
                snapshot.Plan.ParentRunId,
                delivery.DecisionIdentity,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (!await TryClaimPlanDeliveryAsync(workPlanId, delivery, ct).ConfigureAwait(false))
        {
            await _pendingRequests.ReleaseDeliveryAsync(
                snapshot.Plan.ParentRunId,
                delivery.DecisionIdentity,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (!await _runStore.TryResumeFromChildWorkAsync(
                parent.Id,
                parent.LifecycleGeneration,
                ct).ConfigureAwait(false))
        {
            await MarkPlanReadyAsync(workPlanId, delivery, CancellationToken.None).ConfigureAwait(false);
            await _pendingRequests.ReleaseDeliveryAsync(
                snapshot.Plan.ParentRunId,
                delivery.DecisionIdentity,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                CancellationToken.None).ConfigureAwait(false);
            var currentParent = await _runStore.GetAsync(parent.Id, CancellationToken.None).ConfigureAwait(false);
            if (currentParent is not null && TerminalRunOutcome.IsTerminal(currentParent.Status))
                await SuppressAndCancelAsync(snapshot.Plan, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        var result = delivery.GetResponse<WorkflowChildWorkResult>();
        var delivered = false;
        try
        {
            delivered = await _runtime.TryDeliverParentResumeAsync(
                snapshot.Plan.ParentRunId,
                delivery,
                result,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow child-work resume delivery failed for work plan {WorkPlanId}", workPlanId);
        }

        if (!delivered)
        {
            await _runStore.TryParkForChildWorkAsync(
                parent.Id,
                parent.LifecycleGeneration,
                CancellationToken.None).ConfigureAwait(false);
            await _pendingRequests.ReleaseDeliveryAsync(
                snapshot.Plan.ParentRunId,
                delivery.DecisionIdentity,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                CancellationToken.None).ConfigureAwait(false);
            await MarkPlanReadyAsync(workPlanId, delivery, ct).ConfigureAwait(false);
            return false;
        }

        var marked = await _pendingRequests.MarkDeliveredAsync(
            snapshot.Plan.ParentRunId,
            delivery.DecisionIdentity,
            delivery.ClaimOwner,
            delivery.ClaimedAt,
            CancellationToken.None).ConfigureAwait(false);
        if (marked)
            await MarkPlanDeliveredAsync(
                workPlanId,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
        return marked;
    }

    public async Task PrepareRestartRecoveryAsync(CancellationToken ct = default)
    {
        List<int> planIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            planIds = await db.WorkPlans.AsNoTracking()
                .Where(plan => plan.ParentRunId != null
                    && plan.ParentWorkflowNodeId != null
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Suppressed)
                .Select(plan => plan.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        foreach (var planId in planIds)
        {
            var snapshot = await LoadPlanSnapshotAsync(planId, ct).ConfigureAwait(false);
            if (snapshot?.Plan.ParentRunId is null)
                continue;

            if (snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Committed
                && snapshot.Plan.ParentResumeRequestId is not null
                && await _pendingRequests.ExistsForRequestAsync(
                    snapshot.Plan.ParentRunId,
                    snapshot.Plan.ParentResumeRequestId,
                    ct).ConfigureAwait(false))
            {
                await MarkContinuationArmedAsync(
                    planId,
                    snapshot.Plan.ParentResumeRequestId,
                    ct).ConfigureAwait(false);
            }

            var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
            if (parent is not null && parent.Status == DomainRunStatus.InProgress)
                await _runStore.TryParkForChildWorkAsync(
                    parent.Id,
                    parent.LifecycleGeneration,
                    ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsCorrelatedRunAsync(string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .AnyAsync(plan => plan.ParentRunId == runId || plan.CoordinatorRunId == runId, ct)
            .ConfigureAwait(false);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        List<int> planIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            planIds = await db.WorkPlans.AsNoTracking()
                .Where(plan => plan.ParentRunId != null
                    && plan.ParentWorkflowNodeId != null
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Suppressed)
                .Select(plan => plan.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var actions = 0;
        foreach (var planId in planIds)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await LoadPlanSnapshotAsync(planId, ct).ConfigureAwait(false);
            if (snapshot?.Plan.ParentRunId is null)
                continue;

            var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
            if (parent is null)
                continue;
            if (TerminalRunOutcome.IsTerminal(parent.Status))
            {
                await SuppressAndCancelAsync(snapshot.Plan, ct).ConfigureAwait(false);
                actions++;
                continue;
            }

            if (snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Committed)
            {
                if (snapshot.Plan.ParentResumeRequestId is not null
                    && await _pendingRequests.ExistsForRequestAsync(
                        snapshot.Plan.ParentRunId,
                        snapshot.Plan.ParentResumeRequestId,
                        ct).ConfigureAwait(false))
                {
                    await MarkContinuationArmedAsync(
                        planId,
                        snapshot.Plan.ParentResumeRequestId,
                        ct).ConfigureAwait(false);
                    actions++;
                }
                continue;
            }

            if (snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting)
            {
                if (IsTerminalPlan(snapshot.Plan.Status))
                {
                    if (await TryPrepareResumeAsync(planId, ct).ConfigureAwait(false))
                        actions++;
                }
                else if (await TryStartDispatchAsync(planId, ct).ConfigureAwait(false))
                {
                    actions++;
                }
            }

            snapshot = await LoadPlanSnapshotAsync(planId, ct).ConfigureAwait(false);
            if (snapshot?.Plan.ParentResumeState is WorkflowChildWorkResumeStates.Ready
                or WorkflowChildWorkResumeStates.Delivering)
            {
                await TryPrepareResumeAsync(planId, ct).ConfigureAwait(false);
                if (await TryDeliverResumeAsync(
                    planId,
                    $"workflow-child-recovery:{Environment.MachineName}",
                    DeliveryClaimStaleAfter,
                    ct).ConfigureAwait(false))
                    actions++;
            }
        }

        return actions;
    }

    private async Task<DomainRun> EnsureChildCoordinatorRunAsync(
        WorkPlan plan,
        DomainRun parentRun,
        CancellationToken ct)
    {
        if (!RunId.TryParse(plan.CoordinatorRunId, out var childRunId))
            throw new InvalidOperationException(
                $"Correlated work plan {plan.Id} has invalid child coordinator run id '{plan.CoordinatorRunId}'.");

        var existing = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false)
            ?? await _runStore.FindChildAsync(
                parentRun.Id.ToString(),
                ChildCoordinatorSubtaskKey(plan.ParentWorkflowNodeId!),
                ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var child = new DomainRun
        {
            Id = childRunId,
            RepositoryPath = parentRun.RepositoryPath,
            OriginatingBranch = parentRun.OriginatingBranch,
            ModelSource = parentRun.ModelSource,
            Task = $"Execute durable child work for workflow node '{plan.ParentWorkflowNodeId}'.",
            SubmittingUser = parentRun.SubmittingUser,
            Status = DomainRunStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = parentRun.ProjectId,
            ModelId = parentRun.ModelId,
            AgentName = "Coordinator",
            ParentRunId = parentRun.Id.ToString(),
            SubtaskId = ChildCoordinatorSubtaskKey(plan.ParentWorkflowNodeId!),
            Origin = parentRun.Origin,
            LaunchAutoApproveTools = parentRun.LaunchAutoApproveTools,
            LaunchAutopilot = parentRun.LaunchAutopilot,
            ApprovalPolicySnapshotId = parentRun.ApprovalPolicySnapshotId,
            ApprovalPolicySource = parentRun.ApprovalPolicySource,
            ApprovalPolicyCapturedAt = parentRun.ApprovalPolicyCapturedAt,
            ApprovalPolicySettingsUpdatedAt = parentRun.ApprovalPolicySettingsUpdatedAt,
            ApprovalPolicyInheritedFromRunId = parentRun.ApprovalPolicyInheritedFromRunId,
        };

        try
        {
            await _runStore.InsertAsync(child, ct).ConfigureAwait(false);
            return child;
        }
        catch
        {
            var winner = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false);
            if (winner is not null)
                return winner;
            throw;
        }
    }

    private async Task SuppressAndCancelAsync(WorkPlan plan, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - DeliveryClaimStaleAfter;
        int suppressed;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            suppressed = await db.WorkPlans
                .Where(candidate => candidate.Id == plan.Id
                    && (candidate.ParentResumeState == WorkflowChildWorkResumeStates.Committed
                        || candidate.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                        || candidate.ParentResumeState == WorkflowChildWorkResumeStates.Ready))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(candidate => candidate.Status, WorkPlanStatus.Cancelled)
                    .SetProperty(candidate => candidate.ParentResumeState, WorkflowChildWorkResumeStates.Suppressed)
                    .SetProperty(candidate => candidate.ParentResumeClaimOwner, (string?)null)
                    .SetProperty(candidate => candidate.ParentResumeClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.CoordinatorPodId, (string?)null)
                    .SetProperty(candidate => candidate.UpdatedAt, now), ct)
                .ConfigureAwait(false);

            if (suppressed == 0
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivering
                && (plan.ParentResumeClaimedAt is null || plan.ParentResumeClaimedAt < staleBefore))
            {
                var staleClaim = db.WorkPlans.Where(candidate => candidate.Id == plan.Id
                    && candidate.ParentResumeState == WorkflowChildWorkResumeStates.Delivering
                    && candidate.ParentResumeClaimOwner == plan.ParentResumeClaimOwner);
                staleClaim = plan.ParentResumeClaimedAt is null
                    ? staleClaim.Where(candidate => candidate.ParentResumeClaimedAt == null)
                    : staleClaim.Where(candidate => candidate.ParentResumeClaimedAt == plan.ParentResumeClaimedAt);
                suppressed = await staleClaim.ExecuteUpdateAsync(updates => updates
                    .SetProperty(candidate => candidate.Status, WorkPlanStatus.Cancelled)
                    .SetProperty(candidate => candidate.ParentResumeState, WorkflowChildWorkResumeStates.Suppressed)
                    .SetProperty(candidate => candidate.ParentResumeClaimOwner, (string?)null)
                    .SetProperty(candidate => candidate.ParentResumeClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.CoordinatorPodId, (string?)null)
                    .SetProperty(candidate => candidate.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            }
        }

        if (suppressed == 1
            && plan.ParentRunId is not null
            && plan.ParentResumeRequestId is not null)
        {
            await _pendingRequests.DiscardWorkflowChildWorkAsync(
                plan.ParentRunId,
                plan.ParentResumeRequestId,
                ct).ConfigureAwait(false);
        }

        var runs = new List<DomainRun>();
        if (RunId.TryParse(plan.CoordinatorRunId, out var childCoordinatorId)
            && await _runStore.GetAsync(childCoordinatorId, ct).ConfigureAwait(false) is { } coordinator)
            runs.Add(coordinator);
        runs.AddRange(await _runStore.GetRunsByParentAsync(plan.CoordinatorRunId, ct).ConfigureAwait(false));

        foreach (var run in runs
            .Where(run => !TerminalRunOutcome.IsTerminal(run.Status))
            .GroupBy(run => run.Id)
            .Select(group => group.First()))
        {
            await _runtime.CancelRunAsync(run, ct).ConfigureAwait(false);
        }
    }

    private async Task MarkContinuationArmedAsync(int workPlanId, string requestId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && plan.ParentResumeRequestId == requestId
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Committed)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Waiting)
                .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    private async Task MarkPlanReadyAsync(
        int workPlanId,
        PendingDelivery delivery,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivering
                && plan.ParentResumeClaimOwner == delivery.ClaimOwner
                && plan.ParentResumeClaimedAt == delivery.ClaimedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Ready)
                .SetProperty(plan => plan.ParentResumeClaimOwner, (string?)null)
                .SetProperty(plan => plan.ParentResumeClaimedAt, (DateTimeOffset?)null)
                .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    private async Task MarkPlanDeliveredAsync(
        int workPlanId,
        string? claimOwner,
        DateTimeOffset? claimedAt,
        DateTimeOffset? deliveredAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claimOwner) || claimedAt is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivering
                && plan.ParentResumeClaimOwner == claimOwner
                && plan.ParentResumeClaimedAt == claimedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Delivered)
                .SetProperty(plan => plan.ParentResumeDeliveredAt, deliveredAt ?? DateTimeOffset.UtcNow)
                .SetProperty(plan => plan.ParentResumeClaimOwner, (string?)null)
                .SetProperty(plan => plan.ParentResumeClaimedAt, (DateTimeOffset?)null)
                .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    private async Task EnsureParentWaitingAsync(
        WorkPlan plan,
        DomainRun parent,
        CancellationToken ct)
    {
        var current = await _runStore.GetAsync(parent.Id, ct).ConfigureAwait(false);
        if (current is null || TerminalRunOutcome.IsTerminal(current.Status))
        {
            if (current is not null)
                await SuppressAndCancelAsync(plan, ct).ConfigureAwait(false);
            return;
        }

        if (current.Status == DomainRunStatus.AwaitingReview)
            return;

        if (current.Status != DomainRunStatus.InProgress
            || !await _runStore.TryParkForChildWorkAsync(
                current.Id,
                current.LifecycleGeneration,
                ct).ConfigureAwait(false))
        {
            var after = await _runStore.GetAsync(parent.Id, ct).ConfigureAwait(false);
            if (after is not null && TerminalRunOutcome.IsTerminal(after.Status))
                await SuppressAndCancelAsync(plan, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryClaimDispatchAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - _dispatchLeaseStaleAfter;

        if (db.Database.IsSqlite())
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "WorkPlans"
                   SET "Status" = {WorkPlanStatus.Dispatching},
                       "CoordinatorPodId" = {_podId},
                       "UpdatedAt" = {now}
                 WHERE "Id" = {workPlanId}
                   AND ("Status" = {WorkPlanStatus.Planned}
                        OR "Status" = {WorkPlanStatus.Dispatching})
                   AND "ParentResumeState" = {WorkflowChildWorkResumeStates.Waiting}
                   AND ("CoordinatorPodId" IS NULL
                        OR "CoordinatorPodId" = {_podId}
                        OR "UpdatedAt" < {staleBefore})
                """, ct).ConfigureAwait(false);
            return rows == 1;
        }

        var claimed = await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && (plan.Status == WorkPlanStatus.Planned
                    || plan.Status == WorkPlanStatus.Dispatching)
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                && (plan.CoordinatorPodId == null
                    || plan.CoordinatorPodId == _podId
                    || plan.UpdatedAt < staleBefore))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.Status, WorkPlanStatus.Dispatching)
                .SetProperty(plan => plan.CoordinatorPodId, _podId)
                .SetProperty(plan => plan.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return claimed == 1;
    }

    private async Task<bool> TryClaimPlanDeliveryAsync(
        int workPlanId,
        PendingDelivery delivery,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var claimed = await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && (plan.ParentResumeState == WorkflowChildWorkResumeStates.Ready
                    || plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivering))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Delivering)
                .SetProperty(plan => plan.ParentResumeClaimOwner, delivery.ClaimOwner)
                .SetProperty(plan => plan.ParentResumeClaimedAt, delivery.ClaimedAt)
                .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
        return claimed == 1;
    }

    private async Task<WorkflowChildWorkAttachment> GetAttachmentAsync(
        int workPlanId,
        bool reattached,
        CancellationToken ct)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Work plan {workPlanId} disappeared after persistence.");
        return new WorkflowChildWorkAttachment(
            snapshot.Plan.Id,
            snapshot.Plan.CoordinatorRunId,
            snapshot.Plan.ParentResumeRequestId!,
            snapshot.Plan.ParentResumeState!,
            reattached,
            snapshot.Branches);
    }

    private async Task<PlanSnapshot?> LoadPlanSnapshotAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workPlanId, ct)
            .ConfigureAwait(false);
        if (plan is null)
            return null;

        var branches = await db.Subtasks.AsNoTracking()
            .Where(subtask => subtask.WorkPlanId == workPlanId
                && subtask.WorkflowBranchNodeId != null
                && subtask.WorkflowBranchOrdinal != null)
            .OrderBy(subtask => subtask.WorkflowBranchOrdinal)
            .Select(subtask => new WorkflowChildWorkBranch(
                subtask.Id,
                subtask.WorkflowBranchNodeId!,
                subtask.WorkflowBranchOrdinal!.Value,
                subtask.Status,
                subtask.ChildRunId))
            .ToListAsync(ct).ConfigureAwait(false);
        return new PlanSnapshot(plan, branches);
    }

    private static Task<WorkPlan?> FindCorrelatedPlanAsync(
        MemoryDbContext db,
        string parentRunId,
        string parentWorkflowNodeId,
        CancellationToken ct) =>
        db.WorkPlans.AsNoTracking()
            .SingleOrDefaultAsync(plan =>
                plan.ParentRunId == parentRunId
                && plan.ParentWorkflowNodeId == parentWorkflowNodeId, ct);

    private async Task<DomainRun?> TryGetRunAsync(string runId, CancellationToken ct) =>
        RunId.TryParse(runId, out var id)
            ? await _runStore.GetAsync(id, ct).ConfigureAwait(false)
            : null;

    private static bool IsTerminalPlan(string status) => status is
        WorkPlanStatus.Complete
        or WorkPlanStatus.AssemblyBlocked
        or WorkPlanStatus.AssemblyFailed
        or WorkPlanStatus.AssemblyDeclined
        or WorkPlanStatus.RaiBlocked
        or WorkPlanStatus.NeedsResolution
        or WorkPlanStatus.Cancelled;

    private static void ValidateRequest(WorkflowChildWorkRequest request)
    {
        if (request.ParentRun.ProjectId is null)
            throw new ArgumentException("A workflow child-work parent must belong to a project.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ParentWorkflowId))
            throw new ArgumentException("Parent workflow id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ParentWorkflowNodeId))
            throw new ArgumentException("Parent workflow node id is required.", nameof(request));
        if (request.Branches.Count < 2)
            throw new ArgumentException("Static workflow child work requires at least two branches.", nameof(request));
        if (request.Branches.Any(branch => string.IsNullOrWhiteSpace(branch.NodeId)))
            throw new ArgumentException("Every static workflow branch requires a node id.", nameof(request));
        if (request.Branches.Select(branch => branch.NodeId).Distinct(StringComparer.Ordinal).Count()
            != request.Branches.Count)
            throw new ArgumentException("Static workflow branch node ids must be unique.", nameof(request));
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                or SqliteException { SqliteErrorCode: 19 })
                return true;
        }
        return false;
    }

    private sealed record PlanSnapshot(
        WorkPlan Plan,
        IReadOnlyList<WorkflowChildWorkBranch> Branches);
}
