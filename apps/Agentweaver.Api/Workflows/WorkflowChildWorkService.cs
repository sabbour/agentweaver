using System.Data;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
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
    IReadOnlyList<StaticWorkflowBranch> Branches,
    AgentTurnInput? IncomingInput = null,
    string? ExecutionBaseTreeHash = null);

internal sealed record WorkflowComposedWorkRequest(
    DomainRun ParentRun,
    string ParentWorkflowId,
    string ParentWorkflowNodeId,
    string Prompt,
    AgentTurnInput IncomingInput,
    string? ExecutionBaseTreeHash);

internal sealed record WorkflowChildWorkAttachment(
    int WorkPlanId,
    string ChildCoordinatorRunId,
    string? ParentResumeRequestId,
    string ParentResumeState,
    bool Reattached,
    IReadOnlyList<WorkflowChildWorkBranch> Branches);

internal sealed record WorkflowChildWorkBranch(
    int SubtaskId,
    string NodeId,
    int Ordinal,
    string Status,
    string? ChildRunId,
    string? Output = null,
    string? WorktreeBranch = null,
    string? TreeHash = null,
    string? Diff = null,
    string? OutputRevisionId = null);

internal sealed record WorkflowFanProjection(
    int ParentLifecycleGeneration,
    string BaseCommitHash,
    string BaseTreeHash,
    string PreparedCommitHash,
    string PreparedTreeHash);

internal sealed record WorkflowChildWorkResult(
    int WorkPlanId,
    string ChildCoordinatorRunId,
    string ParentWorkflowId,
    string ParentWorkflowNodeId,
    string? ParentJoinNodeId,
    bool Succeeded,
    string WorkPlanStatus,
    string? FailureReason,
    IReadOnlyList<WorkflowChildWorkBranch> Branches,
    string JoinedOutput,
    WorkflowComposedAssembly? Assembly = null,
    WorkflowFanProjection? FanProjection = null);

internal sealed record WorkflowComposedAssembly(
    string IntegrationBranch,
    string TreeHash,
    string AggregateDiff,
    IReadOnlyList<string> IncludedChildRunIds);

internal sealed record WorkflowChildWorkPauseRequest(
    int WorkPlanId,
    string ParentRunId,
    string ParentWorkflowNodeId,
    string ParentJoinNodeId,
    string ChildCoordinatorRunId);

internal sealed record WorkflowFanInOutput(
    int WorkPlanId,
    string ChildCoordinatorRunId,
    bool Succeeded,
    string? FailureReason,
    IReadOnlyList<WorkflowChildWorkBranch> Branches,
    string JoinedOutput);

internal sealed record WorkflowFanCompletedOutput(
    string RunId,
    string JoinedOutput,
    int WorkPlanId,
    string ChildCoordinatorRunId);

internal sealed record WorkflowComposedCompletedOutput(
    string RunId,
    int WorkPlanId,
    string ChildCoordinatorRunId,
    WorkflowComposedAssembly Assembly);

internal interface IWorkflowChildWorkRuntime
{
    bool DispatchEnabled { get; }
    bool IsDispatchActive(string coordinatorRunId);
    bool IsParentResumeActive(string parentRunId);
    void StartDispatch(CoordinatorDispatchContext context);
    void StartAssembly(CoordinatorDispatchContext context);
    Task<bool> TryDeliverParentResumeAsync(
        string parentRunId,
        PendingDelivery delivery,
        WorkflowChildWorkResult result,
        CancellationToken ct);
    void RecordParentStep(string parentRunId, object payload);
    Task<bool> RecordParentReadyStepAsync(
        int workPlanId,
        string parentRunId,
        string eventIdentity,
        object payload,
        CancellationToken ct);
    void EnsureRunStream(string runId, string ownerUser);
    Task PublishParentGraphAsync(
        string parentRunId,
        string parentWorkflowNodeId,
        string childCoordinatorRunId,
        CancellationToken ct);
    Task CancelRunAsync(DomainRun run, string requestedByRunId, CancellationToken ct);
}

internal sealed class WorkflowChildWorkRuntime(
    RunWorkflowRegistry workflowRegistry,
    IRunStore runStore,
    RunStreamStore streamStore,
    IRunEventStream eventStream,
    IWorktreeOperations worktreeOperations,
    IServiceProvider services,
    IOptions<SandboxRuntimeOptions> sandboxRuntime,
    TerminalOutcomeProjector terminalOutcomeProjector,
    ILogger<WorkflowChildWorkRuntime> logger) : IWorkflowChildWorkRuntime
{
    private CoordinatorDispatchService Dispatch =>
        services.GetRequiredService<CoordinatorDispatchService>();

    public bool DispatchEnabled => true;

    public bool IsDispatchActive(string coordinatorRunId) => Dispatch.IsDispatchActive(coordinatorRunId);

    public bool IsParentResumeActive(string parentRunId) => workflowRegistry.Get(parentRunId) is not null;

    public void StartDispatch(CoordinatorDispatchContext context) => Dispatch.StartDispatch(context);

    public void StartAssembly(CoordinatorDispatchContext context) =>
        services.GetRequiredService<ICoordinatorAssembly>().StartAssembly(context);

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

    public void RecordParentStep(string parentRunId, object payload) =>
        streamStore.Get(parentRunId)?.RecordNext(EventTypes.WorkflowStep, payload);

    public async Task<bool> RecordParentReadyStepAsync(
        int workPlanId,
        string parentRunId,
        string eventIdentity,
        object payload,
        CancellationToken ct)
    {
        var persisted = await eventStream.AppendWorkflowChildWorkReadyAsync(
            workPlanId,
            parentRunId,
            eventIdentity,
            new RunEvent(0, EventTypes.WorkflowStep, payload),
            ct).ConfigureAwait(false);
        if (persisted is null)
            return false;
        streamStore.Get(parentRunId)?.RecordDurable(persisted);
        return true;
    }

    public void EnsureRunStream(string runId, string ownerUser) =>
        _ = streamStore.Get(runId) ?? streamStore.Create(runId, ownerUser);

    public async Task PublishParentGraphAsync(
        string parentRunId,
        string parentWorkflowNodeId,
        string childCoordinatorRunId,
        CancellationToken ct)
    {
        if (!RunId.TryParse(parentRunId, out var parsed))
            return;
        var run = await runStore.GetAsync(parsed, ct).ConfigureAwait(false);
        var entry = streamStore.Get(parentRunId);
        if (run is null || entry is null)
            return;

        var descriptor = await services.GetRequiredService<RunWorkflowFactory>()
            .GetGraphDescriptorAsync(run, ct)
            .ConfigureAwait(false);
        var nodes = descriptor.Nodes
            .Select(node => string.Equals(node.Id, parentWorkflowNodeId, StringComparison.Ordinal)
                ? node with { ChildGraphRef = $"run:{childCoordinatorRunId}" }
                : node)
            .ToArray();
        entry.RecordNext(EventTypes.WorkflowGraph, descriptor with { Nodes = nodes });
    }

    public Task CancelRunAsync(DomainRun run, string requestedByRunId, CancellationToken ct) =>
        EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            workflowRegistry,
            worktreeOperations,
            logger,
            ct,
            services.GetService<IAgentHostPodLifecycle>(),
            sandboxRuntime.Value,
            services.GetService<IRunEventStream>(),
            terminalOutcomeProjector,
            reason: "parent_cancelled",
            requestedByRunId: requestedByRunId);
}

/// <summary>
/// Durable parent-to-child work substrate for static fan regions and dynamic composed stages. It
/// persists correlation, work-plan identity, and the parent continuation before dispatch is allowed.
/// </summary>
internal sealed class WorkflowChildWorkService
{
    internal Func<Task>? BeforeFanProjectionFenceOverride { get; set; }
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

        var attachment = await PrepareStaticAsync(request, ct).ConfigureAwait(false);

        var requestId = ResumeRequestId(
            request.ParentRun.Id.ToString(),
            request.ParentWorkflowNodeId,
            attachment.WorkPlanId);
        var continuation = continuationFactory(requestId);
        if (!string.Equals(continuation.RequestId, requestId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Workflow child continuation request id must be the stable id '{requestId}'.");

        return await ArmContinuationAsync(
            attachment.WorkPlanId,
            continuation,
            request.ParentRun.SubmittingUser,
            ct).ConfigureAwait(false);
    }

    public async Task<WorkflowChildWorkAttachment> PrepareStaticAsync(
        WorkflowChildWorkRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var (plan, reattached) = await EnsurePersistedAsync(request, ct).ConfigureAwait(false);
        await EnsureChildCoordinatorRunAsync(plan, request.ParentRun, ct).ConfigureAwait(false);
        return await GetAttachmentAsync(plan.Id, reattached, ct).ConfigureAwait(false);
    }

    public async Task<WorkflowChildWorkAttachment> PrepareComposedAsync(
        WorkflowComposedWorkRequest request,
        CancellationToken ct = default)
    {
        if (request.ParentRun.ProjectId is null
            || string.IsNullOrWhiteSpace(request.ParentWorkflowId)
            || string.IsNullOrWhiteSpace(request.ParentWorkflowNodeId)
            || string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Composed child work requires a project, workflow, node, and prompt.", nameof(request));

        var (plan, reattached) = await EnsurePersistedAsync(
            new WorkflowChildWorkRequest(
                request.ParentRun, request.ParentWorkflowId, request.ParentWorkflowNodeId,
                null, [], request.IncomingInput, request.ExecutionBaseTreeHash),
            ct, request.Prompt).ConfigureAwait(false);
        if (plan.ParentJoinNodeId is not null)
            throw new InvalidOperationException($"Workflow node '{request.ParentWorkflowNodeId}' is already a static fan.");
        await EnsureChildCoordinatorRunAsync(plan, request.ParentRun, ct).ConfigureAwait(false);
        return await GetAttachmentAsync(plan.Id, reattached, ct).ConfigureAwait(false);
    }

    public async Task<WorkflowChildWorkAttachment> ArmContinuationAsync(
        int workPlanId,
        ExternalRequest continuation,
        string ownerUser,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow child work plan {workPlanId} was not found.");
        if (snapshot.Plan.ParentRunId is null || snapshot.Plan.ParentWorkflowNodeId is null)
            throw new InvalidOperationException($"Work plan {workPlanId} is not correlated to a parent workflow node.");

        var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Parent workflow run '{snapshot.Plan.ParentRunId}' for work plan {workPlanId} was not found.");

        await _pendingRequests.SetAsync(
            snapshot.Plan.ParentRunId,
            continuation,
            ownerUser,
            ct).ConfigureAwait(false);
        await MarkContinuationArmedAsync(workPlanId, continuation.RequestId, ct).ConfigureAwait(false);
        await EnsureParentWaitingAsync(snapshot.Plan, parent, ct).ConfigureAwait(false);
        await _runtime.PublishParentGraphAsync(
            snapshot.Plan.ParentRunId,
            snapshot.Plan.ParentWorkflowNodeId,
            snapshot.Plan.CoordinatorRunId,
            ct).ConfigureAwait(false);
        _runtime.RecordParentStep(snapshot.Plan.ParentRunId, new
        {
            step = snapshot.Plan.ParentWorkflowNodeId,
            status = "waiting_child_work",
            label = snapshot.Plan.ParentJoinNodeId is null ? "Coordinator plan" : "Parallel branches",
            workPlanId,
            childCoordinatorRunId = snapshot.Plan.CoordinatorRunId,
            parentWorkflowId = snapshot.Plan.ParentWorkflowId,
            parentWorkflowNodeId = snapshot.Plan.ParentWorkflowNodeId,
            joinNodeId = snapshot.Plan.ParentJoinNodeId,
            parentJoinNodeId = snapshot.Plan.ParentJoinNodeId,
            branchCount = snapshot.Branches.Count,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });

        await TryStartDispatchAsync(workPlanId, ct).ConfigureAwait(false);
        return await GetAttachmentAsync(workPlanId, reattached: true, ct).ConfigureAwait(false);
    }

    internal async Task<(WorkPlan Plan, bool Reattached)> EnsurePersistedAsync(
        WorkflowChildWorkRequest request,
        CancellationToken ct = default,
        string? composedPrompt = null)
    {
        if (composedPrompt is null)
            ValidateRequest(request);
        var parentRunId = request.ParentRun.Id.ToString();

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var existing = await FindCorrelatedPlanAsync(
                db, parentRunId, request.ParentWorkflowNodeId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if ((existing.ParentJoinNodeId is null) != (composedPrompt is not null))
                    throw new InvalidOperationException($"Workflow node '{request.ParentWorkflowNodeId}' changed child-work kind.");
                return (existing, true);
            }
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
                if ((existing.ParentJoinNodeId is null) != (composedPrompt is not null))
                    throw new InvalidOperationException($"Workflow node '{request.ParentWorkflowNodeId}' changed child-work kind.");
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return (existing, true);
            }

            var now = DateTimeOffset.UtcNow;
            var childRunId = RunId.New().ToString();
            var composedContext = composedPrompt is null
                ? null
                : request.IncomingInput?.Task
                    ?? throw new InvalidOperationException("Composed child work requires the parent turn context.");
            var outcomeSpec = new OutcomeSpec
            {
                ProjectId = request.ParentRun.ProjectId?.ToString() ?? string.Empty,
                CoordinatorRunId = childRunId,
                Goal = composedPrompt ?? $"Execute workflow child work for node '{request.ParentWorkflowNodeId}'.",
                DesiredOutcome = composedPrompt is null
                    ? "Complete every declared static branch and return one ordered result."
                    : $"{composedPrompt}\n\n[Parent workflow context]\n{composedContext}",
                Scope = $"Pinned parent workflow '{request.ParentWorkflowId}', node '{request.ParentWorkflowNodeId}'.",
                Assumptions = composedPrompt is null
                    ? "Static branch declarations are immutable for this parent run and workflow node."
                    : "Decompose into dependent subtasks; assembly returns to the parent without nested review or merge.",
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
                ParentTurnInputJson = request.IncomingInput is null
                    ? null
                    : JsonSerializer.Serialize(request.IncomingInput, JsonDefaults.Options),
                ExecutionBaseTreeHash = request.ExecutionBaseTreeHash,
                WorkflowId = composedPrompt is null ? request.ParentWorkflowId : null,
                Status = WorkPlanStatus.Planned,
                IsolationSummary = composedPrompt is null
                    ? "Static workflow branches; parent continuation must be armed before dispatch."
                    : "Dynamic composed plan; parent continuation must be armed before decomposition.",
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
            if ((winner.ParentJoinNodeId is null) != (composedPrompt is not null))
                throw new InvalidOperationException($"Workflow node '{request.ParentWorkflowNodeId}' changed child-work kind.");
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
        {
            if (snapshot.Plan.ParentJoinNodeId is null && snapshot.Plan.Status == WorkPlanStatus.Planned)
                await FailComposedBeforeDispatchAsync(
                    workPlanId, "composed_coordinator_terminated_before_dispatch", ct).ConfigureAwait(false);
            return false;
        }

        var composed = snapshot.Plan.ParentJoinNodeId is null;
        if (composed && snapshot.Plan.Status is WorkPlanStatus.AwaitingAssembly or WorkPlanStatus.Assembling)
        {
            _runtime.StartAssembly(ComposedDispatchContext(snapshot.Plan, child));
            return true;
        }
        if (composed && snapshot.Plan.Status == WorkPlanStatus.Planned)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var goal = await db.OutcomeSpecs.AsNoTracking()
                .Where(spec => spec.Id == snapshot.Plan.OutcomeSpecId)
                .Select(spec => spec.Goal)
                .SingleAsync(ct).ConfigureAwait(false);
            var incomingInput = DeserializeIncomingInput(snapshot.Plan)
                ?? throw new InvalidOperationException($"Composed work plan {workPlanId} lost its parent input.");
            try
            {
                var orchestration = await scope.ServiceProvider.GetRequiredService<CoordinatorWorkflowFactory>()
                    .OrchestrateComposedAsync(new CoordinatorDraftInput(
                        child.Id.ToString(), snapshot.Plan.ProjectId, goal,
                        child.SubmittingUser, child.RepositoryPath, child.ModelId,
                        ModelSource: incomingInput.ModelSource,
                        ByokProviderFingerprint: incomingInput.ByokProviderFingerprint), ct)
                    .ConfigureAwait(false);
                if (orchestration.WorkPlanId != workPlanId || orchestration.InlineSubtaskCount == 0)
                    throw new InvalidOperationException(
                        $"Composed coordinator {child.Id} did not populate work plan {workPlanId}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Composed coordinator {RunId} failed to decompose plan {WorkPlanId}",
                    child.Id, workPlanId);
                await FailComposedBeforeDispatchAsync(
                    workPlanId, $"composed_decomposition_failed:{ex.Message}", CancellationToken.None)
                    .ConfigureAwait(false);
                return false;
            }
            snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Composed work plan {workPlanId} disappeared after decomposition.");
            using var countScope = _scopeFactory.CreateScope();
            var countDb = countScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!await countDb.Subtasks.AnyAsync(subtask => subtask.WorkPlanId == workPlanId, ct)
                    .ConfigureAwait(false))
                throw new InvalidOperationException($"Composed work plan {workPlanId} has no dispatchable subtasks.");
        }

        if (!await TryClaimDispatchAsync(workPlanId, ct).ConfigureAwait(false))
            return false;

        parent = await TryGetRunAsync(
            snapshot.Plan.ParentRunId ?? throw new InvalidOperationException(
                $"Composed work plan {workPlanId} lost its parent correlation."), ct).ConfigureAwait(false);
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
            var incoming = DeserializeIncomingInput(snapshot.Plan);
            _runtime.StartDispatch(composed
                ? ComposedDispatchContext(snapshot.Plan, child)
                : new CoordinatorDispatchContext(
                    snapshot.Plan.CoordinatorRunId,
                    child.RepositoryPath,
                    child.OriginatingBranch,
                    child.SubmittingUser,
                    child.ProjectId,
                    StaticWorkflowChild: true,
                    StaticParentTask: incoming?.Task));
        }

        return true;
    }

    private static CoordinatorDispatchContext ComposedDispatchContext(WorkPlan plan, DomainRun child) =>
        new(child.Id.ToString(), child.RepositoryPath, child.OriginatingBranch,
            child.SubmittingUser, child.ProjectId, ComposedWorkflowChild: true);

    private async Task FailComposedBeforeDispatchAsync(
        int workPlanId, string failureReason, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.WorkPlans
                .Where(plan => plan.Id == workPlanId
                    && plan.ParentRunId != null
                    && plan.ParentJoinNodeId == null
                    && plan.Status == WorkPlanStatus.Planned
                    && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.Status, WorkPlanStatus.Assembling)
                    .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot?.Plan.Status == WorkPlanStatus.Assembling)
            await CompleteComposedAssemblyAsync(workPlanId, null, failureReason, ct).ConfigureAwait(false);
    }

    internal async Task<bool> CompleteComposedAssemblyAsync(
        int workPlanId,
        WorkflowComposedAssembly? assembly,
        string? failureReason,
        CancellationToken ct)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Composed work plan {workPlanId} was not found.");
        if (snapshot.Plan.ParentRunId is null || snapshot.Plan.ParentWorkflowNodeId is null
            || snapshot.Plan.ParentJoinNodeId is not null || snapshot.Plan.ParentWorkflowId is null)
            throw new InvalidOperationException($"Work plan {workPlanId} is not a composed child.");

        if (snapshot.Plan.Status == WorkPlanStatus.Assembling
            && failureReason is not null
            && snapshot.Plan.ParentResumeResultJson is not null)
        {
            var stagedResult = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                snapshot.Plan.ParentResumeResultJson, JsonDefaults.Options);
            if (stagedResult?.Succeeded == true && stagedResult.Assembly is not null)
            {
                _logger.LogWarning(
                    "Preserving staged composed assembly for work plan {WorkPlanId} after late failure: {FailureReason}",
                    workPlanId, failureReason);
                return false;
            }
        }

        if (snapshot.Plan.Status == WorkPlanStatus.Assembling
            && assembly is not null && failureReason is null)
        {
            var staged = await GetStagedComposedAssemblyAsync(workPlanId, ct).ConfigureAwait(false);
            var parent = await TryGetRunAsync(snapshot.Plan.ParentRunId, ct).ConfigureAwait(false);
            if (staged is null
                || staged.IntegrationBranch != assembly.IntegrationBranch
                || staged.TreeHash != assembly.TreeHash
                || staged.AggregateDiff != assembly.AggregateDiff
                || !staged.IncludedChildRunIds.SequenceEqual(assembly.IncludedChildRunIds)
                || parent?.TreeHash != assembly.TreeHash
                || parent.WorktreeBranch != Git.WorktreeManager.BranchNameFor(parent.Id))
                throw new InvalidOperationException(
                    $"Composed work plan {workPlanId} cannot resume before the verified parent tree is installed.");
        }

        var subtasks = await GetComposedSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        var branches = await EnrichBranchesAsync(subtasks, ct).ConfigureAwait(false);
        var succeeded = assembly is not null && failureReason is null;
        var result = new WorkflowChildWorkResult(
            workPlanId, snapshot.Plan.CoordinatorRunId, snapshot.Plan.ParentWorkflowId,
            snapshot.Plan.ParentWorkflowNodeId, null, succeeded,
            succeeded ? WorkPlanStatus.Complete : WorkPlanStatus.AssemblyFailed,
            failureReason, branches, succeeded ? BuildJoinedOutput(branches) : string.Empty,
            assembly);
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var completed = await db.WorkPlans
                .Where(plan => plan.Id == workPlanId
                    && plan.Status == WorkPlanStatus.Assembling
                    && plan.ParentJoinNodeId == null
                    && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.Status, result.WorkPlanStatus)
                    .SetProperty(plan => plan.AssemblyStage, succeeded ? AssemblyStage.Done : (string?)null)
                    .SetProperty(plan => plan.AssemblyStatusReason, failureReason)
                    .SetProperty(plan => plan.ParentResumeResultJson, json)
                    .SetProperty(plan => plan.CoordinatorPodId, (string?)null)
                    .SetProperty(plan => plan.UpdatedAt, now), ct).ConfigureAwait(false);
            if (completed != 1)
            {
                var current = await db.WorkPlans.AsNoTracking()
                    .SingleAsync(plan => plan.Id == workPlanId, ct).ConfigureAwait(false);
                if (current.Status == WorkPlanStatus.Cancelled
                    || current.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed)
                    return false;
                if (current.ParentResumeResultJson is null || current.Status == WorkPlanStatus.Assembling)
                    throw new InvalidOperationException(
                        $"Composed work plan {workPlanId} left assembly without a result checkpoint (status {current.Status}).");
                result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    current.ParentResumeResultJson, JsonDefaults.Options)
                    ?? throw new InvalidOperationException($"Composed work plan {workPlanId} has an invalid result checkpoint.");
            }
        }

        await TryPrepareResumeAsync(workPlanId, ct).ConfigureAwait(false);
        await TryDeliverResumeAsync(workPlanId, $"workflow-composed:{_podId}", ct: ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    internal async Task<WorkflowComposedAssembly?> GetStagedComposedAssemblyAsync(int workPlanId, CancellationToken ct)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot?.Plan.Status != WorkPlanStatus.Assembling
            || snapshot.Plan.ParentResumeResultJson is null)
            return null;
        var result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
            snapshot.Plan.ParentResumeResultJson, JsonDefaults.Options);
        if (result?.Succeeded != true || result.Assembly is null
            || result.WorkPlanId != workPlanId)
            throw new InvalidOperationException($"Composed assembly {workPlanId} has an invalid staged result.");
        return result.Assembly;
    }

    internal async Task<bool> TryRestoreTransferredParentTreeAsync(
        DomainRun parent, string currentTree, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parent.WorktreeBranch)
            || parent.WorktreeBranch != Git.WorktreeManager.BranchNameFor(parent.Id))
            return false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var candidates = await db.WorkPlans.AsNoTracking()
            .Where(plan => plan.ParentRunId == parent.Id.ToString()
                && plan.ParentJoinNodeId == null
                && plan.ParentResumeResultJson != null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var plan in candidates)
        {
            if (parent.TreeHash != plan.ExecutionBaseTreeHash
                || plan.Status is not (WorkPlanStatus.Assembling or WorkPlanStatus.Complete)
                || plan.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed)
                continue;
            var result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                plan.ParentResumeResultJson!, JsonDefaults.Options);
            if (result?.Succeeded != true || result.Assembly is null
                || result.WorkPlanId != plan.Id || result.ChildCoordinatorRunId != plan.CoordinatorRunId
                || !string.Equals(result.Assembly.TreeHash, currentTree, StringComparison.OrdinalIgnoreCase))
                continue;
            await _runStore.UpdateAssemblyArtifactsAsync(
                parent.Id, currentTree, result.Assembly.AggregateDiff, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    internal async Task StageComposedAssemblyAsync(
        int workPlanId, WorkflowComposedAssembly assembly, CancellationToken ct)
    {
        var snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Composed work plan {workPlanId} was not found.");
        if (snapshot.Plan.ParentRunId is null || snapshot.Plan.ParentJoinNodeId is not null
            || snapshot.Plan.ParentWorkflowId is null || snapshot.Plan.ParentWorkflowNodeId is null
            || snapshot.Plan.Status != WorkPlanStatus.Assembling)
            throw new InvalidOperationException($"Work plan {workPlanId} cannot stage a composed result.");
        var branches = await EnrichBranchesAsync(
            await GetComposedSubtasksAsync(workPlanId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var result = new WorkflowChildWorkResult(
            workPlanId, snapshot.Plan.CoordinatorRunId, snapshot.Plan.ParentWorkflowId,
            snapshot.Plan.ParentWorkflowNodeId, null, true, WorkPlanStatus.Complete,
            null, branches, BuildJoinedOutput(branches), assembly);
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var staged = await db.WorkPlans
            .Where(plan => plan.Id == workPlanId && plan.Status == WorkPlanStatus.Assembling
                && plan.ParentResumeResultJson == null
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeResultJson, json)
                .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        if (staged == 0)
        {
            var existing = await GetStagedComposedAssemblyAsync(workPlanId, ct).ConfigureAwait(false);
            if (existing is null
                || existing.IntegrationBranch != assembly.IntegrationBranch
                || existing.TreeHash != assembly.TreeHash
                || existing.AggregateDiff != assembly.AggregateDiff
                || !existing.IncludedChildRunIds.SequenceEqual(assembly.IncludedChildRunIds))
                throw new InvalidOperationException($"Composed assembly {workPlanId} changed while staging.");
        }
    }

    private async Task<IReadOnlyList<WorkflowChildWorkBranch>> GetComposedSubtasksAsync(
        int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(subtask => subtask.WorkPlanId == workPlanId)
            .OrderBy(subtask => subtask.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return subtasks.Select((subtask, ordinal) => new WorkflowChildWorkBranch(
            subtask.Id, subtask.Title, ordinal, subtask.Status, subtask.ChildRunId)).ToArray();
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
            var branches = await EnrichBranchesAsync(
                snapshot.Plan.ParentJoinNodeId is null
                    ? await GetComposedSubtasksAsync(workPlanId, ct).ConfigureAwait(false)
                    : snapshot.Branches, ct).ConfigureAwait(false);
            var joinedOutput = BuildJoinedOutput(branches);
            WorkflowFanProjection? fanProjection = null;
            string? fanFailure = null;
            if (succeeded && snapshot.Plan.ParentJoinNodeId is not null)
            {
                try
                {
                    fanProjection = await PrepareFanProjectionAsync(snapshot.Plan, parent, branches, ct)
                        .ConfigureAwait(false);
                }
                catch (RunOutputRevisionUnavailableException ex)
                {
                    _logger.LogError(ex, "Fan input projection preparation failed for plan {WorkPlanId}", workPlanId);
                    fanFailure = ex.Reason;
                    succeeded = false;
                    joinedOutput = string.Empty;
                }
            }
            result = new WorkflowChildWorkResult(
                snapshot.Plan.Id,
                snapshot.Plan.CoordinatorRunId,
                snapshot.Plan.ParentWorkflowId,
                snapshot.Plan.ParentWorkflowNodeId,
                snapshot.Plan.ParentJoinNodeId,
                succeeded,
                fanFailure is null ? snapshot.Plan.Status : WorkPlanStatus.AssemblyFailed,
                succeeded ? null : fanFailure ?? snapshot.Plan.AssemblyStatusReason ?? snapshot.Plan.Status,
                branches,
                joinedOutput, FanProjection: fanProjection);
            var resultJson = JsonSerializer.Serialize(result, JsonDefaults.Options);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.WorkPlans
                .Where(plan => plan.Id == workPlanId
                    && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                    && plan.ParentResumeResultJson == null)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.ParentResumeResultJson, resultJson)
                    .SetProperty(plan => plan.Status, result.WorkPlanStatus)
                    .SetProperty(plan => plan.AssemblyStatusReason, result.FailureReason)
                    .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }

        snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot?.Plan.ParentResumeResultJson is null
            || snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed
            || snapshot.Plan.Status == WorkPlanStatus.Cancelled)
            return false;
        if (snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting)
        {
            var staged = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                snapshot.Plan.ParentResumeResultJson, JsonDefaults.Options)
                ?? throw new InvalidOperationException($"Work plan {workPlanId} has an invalid fan result.");
            if (staged.FanProjection is not null)
            {
                try
                {
                    await ApplyFanProjectionAsync(snapshot.Plan, parent, staged, ct).ConfigureAwait(false);
                }
                catch (RunOutputRevisionUnavailableException ex)
                {
                    _logger.LogError(ex, "Fan input projection install failed for plan {WorkPlanId}", workPlanId);
                    var failed = staged with
                    {
                        Succeeded = false,
                        WorkPlanStatus = WorkPlanStatus.AssemblyFailed,
                        FailureReason = ex.Reason,
                        JoinedOutput = string.Empty,
                        FanProjection = null,
                    };
                    using var failureScope = _scopeFactory.CreateScope();
                    var failureDb = failureScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                    await failureDb.WorkPlans
                        .Where(plan => plan.Id == workPlanId
                            && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                            && plan.ParentResumeResultJson == snapshot.Plan.ParentResumeResultJson)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(plan => plan.Status, WorkPlanStatus.AssemblyFailed)
                            .SetProperty(plan => plan.AssemblyStatusReason, ex.Reason)
                            .SetProperty(plan => plan.ParentResumeResultJson,
                                JsonSerializer.Serialize(failed, JsonDefaults.Options))
                            .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct)
                        .ConfigureAwait(false);
                }
            }
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.WorkPlans
                .Where(plan => plan.Id == workPlanId
                    && plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                    && plan.ParentResumeResultJson != null)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.ParentResumeState, WorkflowChildWorkResumeStates.Ready)
                    .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        snapshot = await LoadPlanSnapshotAsync(workPlanId, ct).ConfigureAwait(false);
        if (snapshot?.Plan.ParentResumeResultJson is null
            || snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed
            || snapshot.Plan.Status == WorkPlanStatus.Cancelled)
            return false;
        result = JsonSerializer.Deserialize<WorkflowChildWorkResult>(
            snapshot.Plan.ParentResumeResultJson,
            JsonDefaults.Options)
            ?? throw new InvalidOperationException($"Work plan {workPlanId} has an invalid parent resume result.");
        if (snapshot.Plan.Status == WorkPlanStatus.AssemblyBlocked
            && result.Succeeded && result.FanProjection is not null)
            return false;
        if (result.Succeeded && result.ParentJoinNodeId is not null
            && result.Branches.Any(branch => branch.OutputRevisionId is not null)
            && result.FanProjection is null)
            throw new RunOutputRevisionUnavailableException("fan_projection_missing");
        if (snapshot.Plan.ParentJoinNodeId is null)
            await EnsureComposedTerminalRunAsync(result, ct).ConfigureAwait(false);

        var identity = PendingRequestStore.CreateDecisionIdentity(
            snapshot.Plan.ParentResumeRequestId!,
            result);
        if (result.Succeeded)
        {
            var eventIdentity = $"workflow-child-work-ready:{identity}";
            if (!await _runtime.RecordParentReadyStepAsync(
                    workPlanId,
                snapshot.Plan.ParentRunId!,
                eventIdentity,
                new
                {
                    eventId = eventIdentity,
                    parentRunId = snapshot.Plan.ParentRunId,
                    step = result.ParentJoinNodeId ?? result.ParentWorkflowNodeId,
                    status = "child_work_ready",
                    label = result.Assembly is null ? "Join parallel branches" : "Composed coordinator",
                    workPlanId = result.WorkPlanId,
                    childCoordinatorRunId = result.ChildCoordinatorRunId,
                    parentWorkflowId = result.ParentWorkflowId,
                    parentWorkflowNodeId = result.ParentWorkflowNodeId,
                    parentJoinNodeId = result.ParentJoinNodeId,
                    succeeded = true,
                    branchCount = result.Branches.Count,
                    joinedOutput = result.JoinedOutput,
                    assembly = result.Assembly,
                    fanProjection = result.FanProjection,
                    sourceRevisions = result.Branches
                        .Where(branch => branch.OutputRevisionId is not null)
                        .Select(branch => new
                        {
                            branch.NodeId,
                            branch.ChildRunId,
                            branch.OutputRevisionId,
                            branch.TreeHash,
                        }),
                    timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                },
                    ct).ConfigureAwait(false))
                return false;
        }
        return await _pendingRequests.TryQueueDeliveryAsync(
            snapshot.Plan.ParentRunId!,
            PendingRequestDeliveryKinds.WorkflowChildWork,
            identity,
            result,
            parent.SubmittingUser,
            ct).ConfigureAwait(false);
    }

    private async Task EnsureComposedTerminalRunAsync(WorkflowChildWorkResult result, CancellationToken ct)
    {
        var child = await TryGetRunAsync(result.ChildCoordinatorRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Composed coordinator {result.ChildCoordinatorRunId} was not found.");
        await _runStore.TrySetTerminalOutcomeAsync(
            child.Id,
            TerminalRunOutcome.Create(
                result.Succeeded ? DomainRunStatus.Completed : DomainRunStatus.Failed,
                result.Succeeded ? EventTypes.RunCompleted : EventTypes.RunFailed,
                new { result, result.WorkPlanId },
                DateTimeOffset.UtcNow, child.LifecycleGeneration),
            result.Succeeded
                ? JsonSerializer.Serialize(result, JsonDefaults.Options)
                : result.FailureReason,
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
        if (parent.Status != DomainRunStatus.AwaitingReview)
            return false;

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

        var result = delivery.GetResponse<WorkflowChildWorkResult>();
        if (result.Succeeded && result.ParentJoinNodeId is not null
            && result.Branches.Any(branch => branch.OutputRevisionId is not null))
        {
            try
            {
                if (result.FanProjection is null)
                    throw new RunOutputRevisionUnavailableException("fan_projection_missing");
                await ApplyFanProjectionAsync(snapshot.Plan, parent, result, ct, delivery,
                    staleAfter ?? DeliveryClaimStaleAfter).ConfigureAwait(false);
            }
            catch (RunOutputRevisionUnavailableException ex)
            {
                _logger.LogError(ex,
                    "Fan projection provenance changed before delivery for plan {WorkPlanId}; parent remains parked",
                    workPlanId);
                using var blockedScope = _scopeFactory.CreateScope();
                await blockedScope.ServiceProvider.GetRequiredService<MemoryDbContext>().WorkPlans
                    .Where(plan => plan.Id == workPlanId
                        && plan.ParentResumeState == WorkflowChildWorkResumeStates.Ready)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(plan => plan.Status, WorkPlanStatus.AssemblyBlocked)
                        .SetProperty(plan => plan.AssemblyStatusReason, ex.Reason)
                        .SetProperty(plan => plan.UpdatedAt, DateTimeOffset.UtcNow), CancellationToken.None)
                    .ConfigureAwait(false);
                await _pendingRequests.ReleaseDeliveryAsync(
                    snapshot.Plan.ParentRunId, delivery.DecisionIdentity,
                    delivery.ClaimOwner, delivery.ClaimedAt, CancellationToken.None)
                    .ConfigureAwait(false);
                return false;
            }
        }
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

        var cancellationSnapshot = await LoadPlanSnapshotAsync(
            workPlanId, CancellationToken.None).ConfigureAwait(false);
        if (cancellationSnapshot?.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed
            || cancellationSnapshot?.Plan.Status == WorkPlanStatus.Cancelled)
            return false;

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
        var observed = !marked && (await _pendingRequests.GetDeliveryStateAsync(
                snapshot.Plan.ParentRunId,
                PendingRequestDeliveryKinds.WorkflowChildWork,
                CancellationToken.None).ConfigureAwait(false)) is { State: PendingRequestDeliveryStates.Delivered,
                    DecisionIdentity: var identity } && identity == delivery.DecisionIdentity;
        if (marked || observed)
            await MarkPlanDeliveredAsync(
                workPlanId,
                delivery.ClaimOwner,
                delivery.ClaimedAt,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
        return marked || observed;
    }

    public async Task PrepareRestartRecoveryAsync(CancellationToken ct = default)
    {
        List<int> planIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            planIds = await db.WorkPlans.AsNoTracking()
                .Where(plan =>
                    (plan.ParentRunId != null
                        && plan.ParentWorkflowNodeId != null
                        && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered)
                    || (plan.ParentRunId == null
                        && plan.Status == WorkPlanStatus.Cancelled))
                .Select(plan => plan.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        foreach (var planId in planIds)
        {
            var snapshot = await LoadPlanSnapshotAsync(planId, ct).ConfigureAwait(false);
            if (snapshot is null)
                continue;

            if (snapshot.Plan.ParentResumeState == WorkflowChildWorkResumeStates.Suppressed
                || snapshot.Plan.Status == WorkPlanStatus.Cancelled)
            {
                await SuppressAndCancelAsync(snapshot.Plan, ct, forceDeliverySuppression: true)
                    .ConfigureAwait(false);
                continue;
            }

            if (snapshot.Plan.ParentRunId is null)
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

            // Parking an in-progress parent here is unsafe: another replica can own its
            // execution lease and already be running synthesis after the fan joined.
            // WorkflowRestartService parks undelivered parents only after claiming that lease.
        }
    }

    public async Task<bool> HasDeliveredParentResumeAsync(
        string parentRunId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(plan => plan.ParentRunId == parentRunId && plan.ParentWorkflowNodeId != null)
            .AnyAsync(plan => plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivered
                || db.PendingRequests.Any(request => request.RunId == parentRunId
                    && request.RequestId == plan.ParentResumeRequestId
                    && request.DeliveryKind == PendingRequestDeliveryKinds.WorkflowChildWork
                    && request.DeliveryState == PendingRequestDeliveryStates.Delivered), ct)
            .ConfigureAwait(false);
    }

    public async Task<int> CancelForParentAsync(string parentRunId, CancellationToken ct = default)
    {
        List<int> planIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var nestedPlanIds = db.WorkPlans.AsNoTracking()
                .Where(plan => plan.ParentRunId == parentRunId
                    && plan.ParentWorkflowNodeId != null
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered)
                .Select(plan => plan.Id);
            var topLevelPlanIds = db.WorkPlans.AsNoTracking()
                .Where(plan => plan.CoordinatorRunId == parentRunId
                    && plan.ParentRunId == null
                    && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered)
                .Select(plan => plan.Id);
            planIds = await nestedPlanIds
                .Union(topLevelPlanIds)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var cancellationFailures = new List<Exception>();
        foreach (var planId in planIds)
        {
            var snapshot = await LoadPlanSnapshotAsync(planId, ct).ConfigureAwait(false);
            if (snapshot is null)
                continue;

            try
            {
                await SuppressAndCancelAsync(snapshot.Plan, ct, forceDeliverySuppression: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to cancel child work plan {WorkPlanId} for parent {ParentRunId}; continuing remaining plans",
                    planId,
                    parentRunId);
                cancellationFailures.Add(new InvalidOperationException(
                    $"Failed to cancel child work plan '{planId}' for parent '{parentRunId}'.",
                    ex));
            }
        }

        if (cancellationFailures.Count > 0)
            throw new AggregateException(
                $"Failed to cancel {cancellationFailures.Count} child work plan(s) for parent '{parentRunId}'.",
                cancellationFailures);

        return planIds.Count;
    }

    public async Task<bool> IsCorrelatedRunAsync(string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (await db.WorkPlans.AsNoTracking()
            .AnyAsync(plan => plan.ParentRunId == runId || plan.CoordinatorRunId == runId, ct)
            .ConfigureAwait(false))
            return true;

        var run = await TryGetRunAsync(runId, ct).ConfigureAwait(false);
        return run?.ParentRunId is not null
            && await db.WorkPlans.AsNoTracking()
                .AnyAsync(plan => plan.CoordinatorRunId == run.ParentRunId, ct)
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
        {
            if (existing.Id != childRunId)
                throw new InvalidOperationException(
                    $"Workflow child coordinator {existing.Id} does not match correlated plan {plan.Id} run {childRunId}.");
            await PrepareChildCoordinatorCapabilitiesAsync(existing, ct).ConfigureAwait(false);
            _runtime.EnsureRunStream(existing.Id.ToString(), existing.SubmittingUser);
            return existing;
        }

        var incoming = DeserializeIncomingInput(plan);

        var child = new DomainRun
        {
            Id = childRunId,
            RepositoryPath = incoming?.RepositoryPath ?? parentRun.RepositoryPath,
            OriginatingBranch = incoming?.WorktreeBranch ?? parentRun.OriginatingBranch,
            ModelSource = parentRun.ModelSource,
            Task = plan.ParentJoinNodeId is null
                ? await GetComposedGoalAsync(plan, ct).ConfigureAwait(false)
                : $"Execute durable child work for workflow node '{plan.ParentWorkflowNodeId}'.",
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
        }
        catch
        {
            var winner = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false);
            if (winner is not null)
            {
                await PrepareChildCoordinatorCapabilitiesAsync(winner, ct).ConfigureAwait(false);
                _runtime.EnsureRunStream(winner.Id.ToString(), winner.SubmittingUser);
                return winner;
            }
            throw;
        }
        await PrepareChildCoordinatorCapabilitiesAsync(child, ct).ConfigureAwait(false);
        _runtime.EnsureRunStream(child.Id.ToString(), child.SubmittingUser);
        return child;
    }

    private async Task PrepareChildCoordinatorCapabilitiesAsync(DomainRun child, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<RunGitHubCapabilitySnapshotLifecycle>();
        if (!await lifecycle.PrepareForLaunchAsync(child, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Workflow child coordinator {child.Id} cannot inherit its parent's run-bound repository capability.");
    }

    private async Task<string> GetComposedGoalAsync(WorkPlan plan, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.OutcomeSpecs.AsNoTracking()
            .Where(spec => spec.Id == plan.OutcomeSpecId)
            .Select(spec => spec.Goal)
            .SingleAsync(ct).ConfigureAwait(false);
    }

    internal static AgentTurnInput? DeserializeIncomingInput(WorkPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ParentTurnInputJson))
            return null;

        return JsonSerializer.Deserialize<AgentTurnInput>(
            plan.ParentTurnInputJson,
            JsonDefaults.Options);
    }

    private async Task SuppressAndCancelAsync(
        WorkPlan plan,
        CancellationToken ct,
        bool forceDeliverySuppression = false)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - DeliveryClaimStaleAfter;
        var isTopLevelPlan = plan.ParentRunId is null;
        var requestedByRunId = plan.ParentRunId ?? plan.CoordinatorRunId;
        DomainRun? activeCoordinator = null;
        if (!isTopLevelPlan
            && RunId.TryParse(plan.CoordinatorRunId, out var coordinatorRunId)
            && await _runStore.GetAsync(coordinatorRunId, ct).ConfigureAwait(false) is { } coordinator
            && !TerminalRunOutcome.IsTerminal(coordinator.Status))
        {
            activeCoordinator = coordinator;
        }

        var activeBranchRunIds = new HashSet<string>(StringComparer.Ordinal);
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var childRunIds = await db.Subtasks.AsNoTracking()
                .Where(subtask => subtask.WorkPlanId == plan.Id
                    && subtask.ChildRunId != null
                    && subtask.CancellationRequestedAt == null)
                .Select(subtask => subtask.ChildRunId!)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var childRunId in childRunIds)
            {
                if (RunId.TryParse(childRunId, out var parsed)
                    && await _runStore.GetAsync(parsed, ct).ConfigureAwait(false) is { } child
                    && !TerminalRunOutcome.IsTerminal(child.Status))
                {
                    activeBranchRunIds.Add(childRunId);
                }
            }
        }

        int suppressed;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            suppressed = await db.WorkPlans
                .Where(candidate => candidate.Id == plan.Id
                    && (candidate.ParentResumeState == WorkflowChildWorkResumeStates.Committed
                        || candidate.ParentResumeState == WorkflowChildWorkResumeStates.Waiting
                        || candidate.ParentResumeState == WorkflowChildWorkResumeStates.Ready
                        || (isTopLevelPlan && candidate.ParentResumeState == null)
                        || (forceDeliverySuppression
                            && candidate.ParentResumeState == WorkflowChildWorkResumeStates.Delivering)))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(candidate => candidate.Status, WorkPlanStatus.Cancelled)
                    .SetProperty(candidate => candidate.ParentResumeState, WorkflowChildWorkResumeStates.Suppressed)
                    .SetProperty(candidate => candidate.ParentResumeClaimOwner, (string?)null)
                    .SetProperty(candidate => candidate.ParentResumeClaimedAt, (DateTimeOffset?)null)
                    .SetProperty(candidate => candidate.CoordinatorPodId, (string?)null)
                    .SetProperty(candidate => candidate.UpdatedAt, now), ct)
                .ConfigureAwait(false);

            if (!forceDeliverySuppression
                && suppressed == 0
                && plan.ParentResumeState == WorkflowChildWorkResumeStates.Delivering
                && (forceDeliverySuppression
                    || plan.ParentResumeClaimedAt is null
                    || plan.ParentResumeClaimedAt < staleBefore))
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

            if (activeBranchRunIds.Count > 0)
            {
                await db.Subtasks
                    .Where(subtask => subtask.WorkPlanId == plan.Id
                        && subtask.ChildRunId != null
                        && activeBranchRunIds.Contains(subtask.ChildRunId)
                        && subtask.CancellationRequestedAt == null)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(subtask => subtask.CancellationRequestedAt, now)
                        .SetProperty(subtask => subtask.CancellationRequestedByRunId, requestedByRunId)
                        .SetProperty(subtask => subtask.UpdatedAt, now), ct)
                    .ConfigureAwait(false);
            }

            if (activeCoordinator is not null)
            {
                await db.WorkPlans
                    .Where(candidate => candidate.Id == plan.Id
                        && candidate.CoordinatorCancellationRequestedAt == null)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(candidate => candidate.CoordinatorCancellationRequestedAt, now)
                        .SetProperty(candidate => candidate.CoordinatorCancellationRequestedByRunId, requestedByRunId)
                        .SetProperty(candidate => candidate.UpdatedAt, now), ct)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
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

        var runs = new List<(DomainRun Run, string RequestedByRunId)>();
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var cancellationPlan = await db.WorkPlans.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == plan.Id, ct)
                .ConfigureAwait(false);
            if (cancellationPlan.CoordinatorCancellationRequestedAt is not null
                && cancellationPlan.CoordinatorCancellationRequestedByRunId is not null
                && RunId.TryParse(cancellationPlan.CoordinatorRunId, out var childCoordinatorId)
                && await _runStore.GetAsync(childCoordinatorId, ct).ConfigureAwait(false) is { } persistedCoordinator)
            {
                runs.Add((persistedCoordinator, cancellationPlan.CoordinatorCancellationRequestedByRunId));
            }

            var reservedChildRunIds = await db.Subtasks.AsNoTracking()
                .Where(subtask => subtask.WorkPlanId == plan.Id
                    && subtask.ChildRunId != null
                    && subtask.CancellationRequestedAt != null
                    && subtask.CancellationRequestedByRunId != null)
                .Select(subtask => new
                {
                    ChildRunId = subtask.ChildRunId!,
                    RequestedByRunId = subtask.CancellationRequestedByRunId!,
                })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var reservedChild in reservedChildRunIds)
            {
                if (RunId.TryParse(reservedChild.ChildRunId, out var childRunId)
                    && await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false) is { } child)
                    runs.Add((child, reservedChild.RequestedByRunId));
            }
        }

        var cancellationFailures = new List<Exception>();
        foreach (var run in runs
            .GroupBy(candidate => candidate.Run.Id)
            .Select(group => group.First()))
        {
            try
            {
                await _runtime.CancelRunAsync(run.Run, run.RequestedByRunId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to cancel snapshotted child run {ChildRunId} for parent {ParentRunId}; continuing remaining children",
                    run.Run.Id,
                    run.RequestedByRunId);
                cancellationFailures.Add(new InvalidOperationException(
                    $"Failed to cancel snapshotted child run '{run.Run.Id}' for parent '{run.RequestedByRunId}'.",
                    ex));
            }
        }

        if (cancellationFailures.Count > 0)
            throw new AggregateException(
                $"Failed to cancel {cancellationFailures.Count} snapshotted child run(s) for work plan {plan.Id}.",
                cancellationFailures);
    }

    private async Task MarkContinuationArmedAsync(int workPlanId, string requestId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.WorkPlans
            .Where(plan => plan.Id == workPlanId
                && (plan.ParentResumeRequestId == null || plan.ParentResumeRequestId == requestId)
                && (plan.ParentResumeState == WorkflowChildWorkResumeStates.Committed
                    || plan.ParentResumeState == WorkflowChildWorkResumeStates.Waiting))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(plan => plan.ParentResumeRequestId, requestId)
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
            snapshot.Plan.ParentResumeRequestId,
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

    private async Task<IReadOnlyList<WorkflowChildWorkBranch>> EnrichBranchesAsync(
        IReadOnlyList<WorkflowChildWorkBranch> branches,
        CancellationToken ct)
    {
        var enriched = new List<WorkflowChildWorkBranch>(branches.Count);
        foreach (var branch in branches.OrderBy(branch => branch.Ordinal))
        {
            var run = branch.ChildRunId is not null
                ? await TryGetRunAsync(branch.ChildRunId, ct).ConfigureAwait(false)
                : null;
            enriched.Add(branch with
            {
                Output = run?.CurrentOutputRevisionId is null ? run?.Result : null,
                WorktreeBranch = run?.WorktreeBranch,
                TreeHash = run?.TreeHash,
                Diff = run?.CurrentOutputRevisionId is null ? run?.Diff : null,
                OutputRevisionId = run?.CurrentOutputRevisionId,
            });
        }
        return enriched;
    }

    private static string BuildJoinedOutput(IReadOnlyList<WorkflowChildWorkBranch> branches) =>
        string.Join(
            "\n\n",
            branches
                .OrderBy(branch => branch.Ordinal)
                .Select(branch =>
                {
                    var output = branch.OutputRevisionId is not null
                        ? $"Child run: {branch.ChildRunId}; retained revision: {branch.OutputRevisionId}; tree: {branch.TreeHash}"
                        : !string.IsNullOrWhiteSpace(branch.Output)
                        ? branch.Output
                            : branch.Status;
                    return $"[{branch.Ordinal + 1}. {branch.NodeId}]\n{output}";
                }));

    private async Task<IReadOnlyList<RunOutputTree.File>> ResolveFanFilesAsync(
        WorkPlan plan, DomainRun parent, IReadOnlyList<WorkflowChildWorkBranch> branches,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == plan.Id && s.WorkflowBranchOrdinal != null)
            .OrderBy(s => s.WorkflowBranchOrdinal).ToListAsync(ct).ConfigureAwait(false);
        if (subtasks.Count != branches.Count || plan.ProjectId != parent.ProjectId?.ToString()
            || plan.ParentRunId != parent.Id.ToString()
            || plan.ParentJoinNodeId is null || parent.Status != DomainRunStatus.AwaitingReview)
            throw new RunOutputRevisionUnavailableException("fan_projection_provenance_mismatch");
        var files = new List<RunOutputTree.File>();
        foreach (var subtask in subtasks)
        {
            var branch = branches.SingleOrDefault(b => b.SubtaskId == subtask.Id
                && b.Ordinal == subtask.WorkflowBranchOrdinal
                && b.NodeId == subtask.WorkflowBranchNodeId
                && b.ChildRunId == subtask.ChildRunId)
                ?? throw new RunOutputRevisionUnavailableException("fan_projection_provenance_mismatch");
            var declared = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(
                subtask.DeclaredOutputPathsJson);
            if (declared.State == CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid)
                throw new RunOutputRevisionUnavailableException("invalid_declared_paths");
            if (declared.Paths.Count == 0)
            {
                if (branch.OutputRevisionId is not null)
                    throw new RunOutputRevisionUnavailableException("fan_revision_unexpected");
                continue;
            }
            if (!RunId.TryParse(subtask.ChildRunId, out var childId))
                throw new RunOutputRevisionUnavailableException("fan_child_missing");
            var child = await _runStore.GetAsync(childId, ct).ConfigureAwait(false);
            if (child is null || child.ParentRunId != plan.CoordinatorRunId
                || child.ProjectId != parent.ProjectId
                || child.RepositoryPath != parent.RepositoryPath
                || child.Status != DomainRunStatus.AssembleReady
                || child.CurrentOutputRevisionId != branch.OutputRevisionId
                || child.TreeHash != branch.TreeHash
                || child.WorktreeBranch != branch.WorktreeBranch
                || child.WorktreeBranch != Git.WorktreeManager.BranchNameFor(childId))
                throw new RunOutputRevisionUnavailableException("fan_projection_provenance_mismatch");
            var revision = await _runStore.ResolveOutputRevisionAsync(childId,
                branch.OutputRevisionId ?? throw new RunOutputRevisionUnavailableException("fan_revision_missing"),
                ct).ConfigureAwait(false);
            if (revision.SchemaVersion != RunOutputRevision.FanDeclaredFilesSchemaVersion
                || revision.RunId != childId || revision.LifecycleGeneration != child.LifecycleGeneration
                || revision.WorkPlanId != plan.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || revision.TreeHash != child.TreeHash)
                throw new RunOutputRevisionUnavailableException("fan_revision_mismatch");
            var retained = revision.ResolveFiles();
            if (!retained.Select(file => file.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(declared.Paths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new RunOutputRevisionUnavailableException("fan_declared_files_mismatch");
            files.AddRange(retained);
        }
        if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new RunOutputRevisionUnavailableException("fan_projection_path_collision");
        return files;
    }

    private async Task<WorkflowFanProjection?> PrepareFanProjectionAsync(
        WorkPlan plan, DomainRun parent, IReadOnlyList<WorkflowChildWorkBranch> branches,
        CancellationToken ct)
    {
        var files = await ResolveFanFilesAsync(plan, parent, branches, ct).ConfigureAwait(false);
        if (files.Count == 0)
            return null;
        var input = DeserializeIncomingInput(plan)
            ?? throw new RunOutputRevisionUnavailableException("fan_parent_input_missing");
        if (string.IsNullOrWhiteSpace(input.FanExecutionBaseCommitHash)
            || string.IsNullOrWhiteSpace(plan.ExecutionBaseTreeHash)
            || parent.WorktreeBranch != Git.WorktreeManager.BranchNameFor(parent.Id))
            throw new RunOutputRevisionUnavailableException("fan_parent_base_missing");
        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<Git.WorktreeManager>();
        var staged = manager.PrepareFanInputProjection(
            parent.RepositoryPath, parent.WorktreePath ?? string.Empty, parent.Id,
            input.FanExecutionBaseCommitHash, plan.ExecutionBaseTreeHash, files);
        return new WorkflowFanProjection(parent.LifecycleGeneration, input.FanExecutionBaseCommitHash,
            plan.ExecutionBaseTreeHash, staged.CommitHash, staged.TreeHash);
    }

    private async Task ApplyFanProjectionAsync(
        WorkPlan plan, DomainRun parent, WorkflowChildWorkResult result,
        CancellationToken ct, PendingDelivery? delivery = null, TimeSpan? deliveryStaleAfter = null)
    {
        var projection = result.FanProjection
            ?? throw new RunOutputRevisionUnavailableException("fan_projection_missing");
        var input = DeserializeIncomingInput(plan)
            ?? throw new RunOutputRevisionUnavailableException("fan_parent_input_missing");
        if (projection.ParentLifecycleGeneration != parent.LifecycleGeneration
            || projection.BaseCommitHash != input.FanExecutionBaseCommitHash
            || projection.BaseTreeHash != plan.ExecutionBaseTreeHash
            || parent.WorktreeBranch != Git.WorktreeManager.BranchNameFor(parent.Id))
            throw new RunOutputRevisionUnavailableException("fan_projection_base_changed");
        var files = await ResolveFanFilesAsync(plan, parent, result.Branches, ct).ConfigureAwait(false);
        if (files.Count == 0)
            throw new RunOutputRevisionUnavailableException("fan_projection_sources_missing");
        if (BeforeFanProjectionFenceOverride is not null)
            await BeforeFanProjectionFenceOverride().ConfigureAwait(false);
        using var scope = _scopeFactory.CreateScope();
        await using var claim = await scope.ServiceProvider.GetRequiredService<RunActiveClaimGuard>()
            .AcquireAsync(parent.Id, ct).ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
        var planLocked = await db.WorkPlans.Where(candidate => candidate.Id == plan.Id)
            .ExecuteUpdateAsync(updates => updates.SetProperty(candidate => candidate.UpdatedAt,
                candidate => candidate.UpdatedAt), ct).ConfigureAwait(false);
        if (planLocked != 1)
            throw new RunOutputRevisionUnavailableException("fan_projection_plan_unavailable");
        var currentPlan = await db.WorkPlans.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == plan.Id, ct).ConfigureAwait(false);
        if (currentPlan.Status is WorkPlanStatus.Cancelled or WorkPlanStatus.AssemblyBlocked
            || currentPlan.ParentResumeState != plan.ParentResumeState
            || currentPlan.ParentResumeState is not (
                WorkflowChildWorkResumeStates.Waiting or WorkflowChildWorkResumeStates.Ready)
            || currentPlan.ParentRunId != parent.Id.ToString()
            || currentPlan.ParentResumeResultJson != plan.ParentResumeResultJson
            || currentPlan.ProjectId != parent.ProjectId?.ToString()
            || currentPlan.ExecutionBaseTreeHash != projection.BaseTreeHash)
            throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
        if (delivery is not null)
        {
            var staleBefore = DateTimeOffset.UtcNow - (deliveryStaleAfter ?? DeliveryClaimStaleAfter);
            var ownerLocked = await db.PendingRequests.Where(request =>
                    request.RunId == parent.Id.ToString()
                    && request.RequestId == currentPlan.ParentResumeRequestId
                    && request.DeliveryKind == PendingRequestDeliveryKinds.WorkflowChildWork
                    && request.DecisionIdentity == delivery.DecisionIdentity
                    && request.DeliveryState == PendingRequestDeliveryStates.Delivering
                    && request.DeliveryClaimOwner == delivery.ClaimOwner
                    && request.DeliveryClaimedAt == delivery.ClaimedAt)
                .ExecuteUpdateAsync(updates => updates.SetProperty(request => request.DeliveryClaimOwner,
                    request => request.DeliveryClaimOwner), ct).ConfigureAwait(false);
            if (ownerLocked != 1)
                throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
            var claimedAt = await db.PendingRequests.AsNoTracking()
                .Where(request => request.RunId == parent.Id.ToString())
                .Select(request => request.DeliveryClaimedAt)
                .SingleAsync(ct).ConfigureAwait(false);
            if (claimedAt is null || claimedAt <= staleBefore)
                throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
        }
        if (db.Database.IsNpgsql())
        {
            var runLocked = await db.Runs.Where(run => run.RunId == parent.Id.ToString())
                .ExecuteUpdateAsync(updates => updates.SetProperty(run => run.TreeHash,
                    run => run.TreeHash), ct).ConfigureAwait(false);
            if (runLocked != 1)
                throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
        }
        var currentParent = await _runStore.GetAsync(parent.Id, ct).ConfigureAwait(false);
        if (currentParent?.Status != DomainRunStatus.AwaitingReview
            || currentParent.LifecycleGeneration != projection.ParentLifecycleGeneration
            || currentParent.WorktreeBranch != parent.WorktreeBranch
            || currentParent.RepositoryPath != parent.RepositoryPath
            || currentParent.ProjectId != parent.ProjectId
            || (currentParent.TreeHash is not null
                && currentParent.TreeHash != projection.BaseTreeHash
                && currentParent.TreeHash != projection.PreparedTreeHash))
            throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
        var manager = scope.ServiceProvider.GetRequiredService<Git.WorktreeManager>();
        var worktreePath = await ResolveFanParentWorktreeAsync(
                currentParent, scope.ServiceProvider, ct, db.Database.IsNpgsql() ? db : null)
            .ConfigureAwait(false);
        manager.ApplyFanInputProjection(worktreePath, parent.Id,
            projection.BaseCommitHash, projection.PreparedCommitHash, projection.PreparedTreeHash);
        var recorded = db.Database.IsNpgsql()
            ? await db.Runs
                .Where(run => run.RunId == parent.Id.ToString()
                    && run.LifecycleGeneration == projection.ParentLifecycleGeneration
                    && run.Status == "awaiting_review"
                    && run.WorktreeBranch == parent.WorktreeBranch
                    && (run.TreeHash == null || run.TreeHash == projection.BaseTreeHash))
                .ExecuteUpdateAsync(updates => updates.SetProperty(run => run.TreeHash,
                    projection.PreparedTreeHash), ct).ConfigureAwait(false) == 1
            : await _runStore.TryRecordFanInputProjectionAsync(
                parent.Id, projection.ParentLifecycleGeneration, projection.BaseTreeHash,
                projection.PreparedTreeHash, parent.WorktreeBranch,
                worktreePath == currentParent.WorktreePath ? null : worktreePath, ct).ConfigureAwait(false);
        if (!recorded)
        {
            var receipt = await _runStore.GetAsync(parent.Id, ct).ConfigureAwait(false);
            if (receipt?.Status != DomainRunStatus.AwaitingReview
                || receipt.LifecycleGeneration != projection.ParentLifecycleGeneration
                || receipt.TreeHash != projection.PreparedTreeHash)
                throw new RunOutputRevisionUnavailableException("fan_projection_receipt_mismatch");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveFanParentWorktreeAsync(
        DomainRun parent, IServiceProvider services, CancellationToken ct,
        MemoryDbContext? lockedDb = null)
    {
        if (!string.IsNullOrWhiteSpace(parent.WorktreePath) && Directory.Exists(parent.WorktreePath))
            return parent.WorktreePath;
        var recovered = services.GetRequiredService<IWorktreeOperations>()
            .TryReattachWorktree(parent.RepositoryPath, parent.OriginatingBranch, parent.Id.ToString());
        if (recovered is null || recovered.Value.BranchName != parent.WorktreeBranch)
            throw new RunOutputRevisionUnavailableException("fan_projection_parent_unavailable");
        if (lockedDb is not null)
        {
            var updated = await lockedDb.Runs.Where(run => run.RunId == parent.Id.ToString()
                    && run.LifecycleGeneration == parent.LifecycleGeneration
                    && run.Status == "awaiting_review")
                .ExecuteUpdateAsync(updates => updates.SetProperty(run => run.WorktreePath,
                    recovered.Value.WorktreePath), ct).ConfigureAwait(false);
            if (updated != 1)
                throw new RunOutputRevisionUnavailableException("fan_projection_fence_lost");
        }
        return recovered.Value.WorktreePath;
    }

    private static bool IsTerminalPlan(string status) => status is
        WorkPlanStatus.Complete
        or WorkPlanStatus.AssemblyBlocked
        or WorkPlanStatus.AssemblyFailed
        or WorkPlanStatus.AssemblyUnknown
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
        if (string.IsNullOrWhiteSpace(request.ParentJoinNodeId))
            throw new ArgumentException("Static workflow child work requires a join node.", nameof(request));
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
