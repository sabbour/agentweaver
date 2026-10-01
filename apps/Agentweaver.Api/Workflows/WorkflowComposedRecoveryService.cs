using System.Data;
using System.Globalization;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Workflows;

internal sealed class WorkflowComposedRecoveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class WorkflowComposedRecoveryService(
    IServiceScopeFactory scopeFactory,
    IRunStore runStore,
    IRunLeaseStore leases,
    IWorktreeOperations worktrees,
    RunWorkflowRegistry registry,
    ILogger<WorkflowComposedRecoveryService> logger)
{
    internal const string RecoveryMarkerPrefix = "composed_recovery_pending:";
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(5);
    internal Func<Run, AgentTurnInput, string, RunLeaseClaim, CancellationToken, Task>? LaunchOverride { get; set; }

    internal static bool IsDecompositionFailure(Run run) =>
        run.ParentRunId is null && run.GetExecutableWorkflowPin() is not null
        && run.Result?.StartsWith("composed_decomposition_failed:", StringComparison.Ordinal) == true;

    public async Task ResumeFailedAsync(Run expectedParent, CancellationToken ct)
    {
        if (expectedParent.Status != RunStatus.Failed || !IsDecompositionFailure(expectedParent))
            throw Rejected("composed_recovery_not_eligible", expectedParent.Id);
        var owner = $"{Environment.MachineName}/composed-recovery/{Guid.NewGuid():N}";
        var acquired = await leases.TryClaimAsync(
            expectedParent.Id.ToString(), owner, LeaseTtl, ct).ConfigureAwait(false);
        if (!acquired.Claimed)
            throw Rejected("composed_recovery_busy", expectedParent.Id);
        var transferred = false;
        try
        {
            if (registry.Get(expectedParent.Id.ToString()) is not null)
                throw Rejected("composed_recovery_busy", expectedParent.Id);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!db.Database.IsNpgsql())
                throw Rejected("composed_recovery_requires_atomic_run_store", expectedParent.Id);

            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var planId = await db.WorkPlans.AsNoTracking()
                .Where(plan => plan.ParentRunId == expectedParent.Id.ToString()
                    && plan.ParentWorkflowNodeId != null && plan.ParentJoinNodeId == null)
                .Select(plan => (int?)plan.Id)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (planId is null)
                throw Rejected("composed_recovery_plan_unavailable", expectedParent.Id);
            var locked = await db.WorkPlans.Where(plan => plan.Id == planId
                    && plan.Status == WorkPlanStatus.AssemblyFailed
                    && plan.CoordinatorCancellationRequestedAt == null
                    && !db.Subtasks.Any(subtask => subtask.WorkPlanId == plan.Id))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(plan => plan.UpdatedAt, plan => plan.UpdatedAt), ct)
                .ConfigureAwait(false);
            if (locked != 1)
                throw Rejected("composed_recovery_plan_changed", expectedParent.Id);
            var plan = await db.WorkPlans.SingleAsync(plan => plan.Id == planId, ct).ConfigureAwait(false);
            var currentParentRecord = await db.Runs.AsNoTracking()
                .SingleAsync(run => run.RunId == expectedParent.Id.ToString(), ct).ConfigureAwait(false);
            var parent = EfRunStore.FromRecord(currentParentRecord);
            if (parent.Status != RunStatus.Failed
                || parent.LifecycleGeneration != expectedParent.LifecycleGeneration
                || parent.Result != expectedParent.Result)
                throw Rejected("composed_recovery_run_changed", parent.Id);
            var childRecord = await db.Runs.AsNoTracking()
                .SingleOrDefaultAsync(run => run.RunId == plan.CoordinatorRunId, ct).ConfigureAwait(false);
            if (childRecord is null)
                throw Rejected("composed_recovery_coordinator_unavailable", parent.Id);
            var child = EfRunStore.FromRecord(childRecord);
            var input = ValidateInput(parent, plan);
            var failure = plan.ParentResumeResultJson is null ? null
                : JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    plan.ParentResumeResultJson, JsonDefaults.Options);
            if (child.Status != RunStatus.Failed || child.ParentRunId != parent.Id.ToString()
                || child.ProjectId != parent.ProjectId || child.Result != parent.Result
                || child.SubtaskId != WorkflowChildWorkService.ChildCoordinatorSubtaskKey(plan.ParentWorkflowNodeId!)
                || failure is null || failure.Succeeded || failure.WorkPlanId != plan.Id
                || failure.ChildCoordinatorRunId != child.Id.ToString()
                || failure.FailureReason != parent.Result || failure.Assembly is not null
                || plan.CoordinatorPodId is not null || plan.MergeEffectId is not null)
                throw Rejected("composed_recovery_not_eligible", parent.Id);

            // Validate the durable provider before reopening either identity.
            await scope.ServiceProvider.GetRequiredService<RunOrchestrator>()
                .ResolveDurableProviderBoundaryAsync(parent, ct).ConfigureAwait(false);
            var lease = new RunLeaseClaim(owner, acquired.FencingToken, parent.LifecycleGeneration);
            if (!await EfRunStore.TryReopenTerminalOnContextAsync(
                    db, parent.Id, ct, parent, lease, clearResult: true).ConfigureAwait(false)
                || !await EfRunStore.TryReopenTerminalOnContextAsync(
                    db, child.Id, ct, child, clearResult: true).ConfigureAwait(false))
                throw Rejected("composed_recovery_run_changed", parent.Id);
            var resumedGeneration = parent.LifecycleGeneration + 1;
            plan.Status = WorkPlanStatus.Planned;
            plan.ParentResumeState = WorkflowChildWorkResumeStates.Committed;
            plan.ParentResumeResultJson = null;
            plan.ParentResumeClaimOwner = null;
            plan.ParentResumeClaimedAt = null;
            plan.ParentResumeDeliveredAt = null;
            plan.AssemblyStage = null;
            plan.AssemblyTerminalStage = null;
            plan.AssemblyStartedAt = null;
            plan.ParentRecoveryGeneration = resumedGeneration;
            plan.AssemblyStatusReason = RecoveryMarkerPrefix
                + resumedGeneration.ToString(CultureInfo.InvariantCulture);
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            var pending = await db.PendingRequests
                .SingleOrDefaultAsync(request => request.RunId == parent.Id.ToString(), ct)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                if (pending.RequestId != plan.ParentResumeRequestId
                    || pending.DeliveryKind != PendingRequestDeliveryKinds.WorkflowChildWork)
                    throw Rejected("composed_recovery_gate_changed", parent.Id);
                pending.DeliveryState = PendingRequestDeliveryStates.Waiting;
                pending.ResponseJson = null;
                pending.DecisionIdentity = null;
                pending.DeliveryKind = null;
                pending.DeliveryClaimOwner = null;
                pending.DeliveryClaimedAt = null;
                pending.DeliveredAt = null;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            parent = await runStore.GetAsync(parent.Id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Recovered composed workflow disappeared.");
            lease = new RunLeaseClaim(owner, acquired.FencingToken, resumedGeneration);
            await LaunchAsync(parent, input, plan.ParentWorkflowNodeId!, lease, ct).ConfigureAwait(false);
            transferred = true;
            logger.LogInformation(
                "Recovered composed workflow {RunId} at node {NodeId}, preserving plan {PlanId} and coordinator {CoordinatorRunId}",
                parent.Id, plan.ParentWorkflowNodeId, plan.Id, child.Id);
        }
        finally
        {
            if (!transferred)
                await leases.ReleaseAsync(
                    expectedParent.Id.ToString(), owner, acquired.FencingToken, CancellationToken.None)
                    .ConfigureAwait(false);
        }
    }

    public async Task<bool> TryRestartPendingAsync(Run parent, RunLeaseClaim lease, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var marker = RecoveryMarkerPrefix + parent.LifecycleGeneration.ToString(CultureInfo.InvariantCulture);
        var plan = await db.WorkPlans.AsNoTracking()
            .SingleOrDefaultAsync(plan => plan.ParentRunId == parent.Id.ToString()
                && plan.ParentJoinNodeId == null && plan.ParentWorkflowNodeId != null
                && plan.AssemblyStatusReason == marker
                && plan.ParentRecoveryGeneration == parent.LifecycleGeneration
                && plan.CoordinatorCancellationRequestedAt == null
                && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered
                && plan.ParentResumeState != WorkflowChildWorkResumeStates.Suppressed, ct)
            .ConfigureAwait(false);
        if (plan is null)
            return false;
        if (parent.Status is not (RunStatus.InProgress or RunStatus.AwaitingReview)
            || lease.LifecycleGeneration != parent.LifecycleGeneration)
            throw Rejected("composed_recovery_run_changed", parent.Id);
        var input = ValidateInput(parent, plan);
        if (parent.Status == RunStatus.AwaitingReview)
        {
            if (!await runStore.TryResumeFromChildWorkAsync(
                    parent.Id, parent.LifecycleGeneration, ct).ConfigureAwait(false))
                throw Rejected("composed_recovery_run_changed", parent.Id);
            parent = parent with { Status = RunStatus.InProgress };
        }
        await LaunchAsync(parent, input, plan.ParentWorkflowNodeId!, lease, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> HasPendingRecoveryAsync(Run parent, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var marker = RecoveryMarkerPrefix + parent.LifecycleGeneration.ToString(CultureInfo.InvariantCulture);
        return await db.WorkPlans.AsNoTracking().AnyAsync(plan =>
            plan.ParentRunId == parent.Id.ToString() && plan.ParentJoinNodeId == null
            && plan.ParentWorkflowNodeId != null && plan.AssemblyStatusReason == marker
            && plan.ParentRecoveryGeneration == parent.LifecycleGeneration
            && plan.CoordinatorCancellationRequestedAt == null
            && plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered
            && plan.ParentResumeState != WorkflowChildWorkResumeStates.Suppressed, ct).ConfigureAwait(false);
    }

    private AgentTurnInput ValidateInput(Run parent, WorkPlan plan)
    {
        var pin = parent.GetExecutableWorkflowPin()
            ?? throw Rejected("composed_recovery_workflow_unavailable", parent.Id);
        var definition = ExecutableWorkflowSnapshots.Load(parent.Id.ToString(), pin);
        var node = definition.Nodes.SingleOrDefault(node => node.Type == WorkflowNodeType.CoordinatorComposed);
        var input = plan.ParentTurnInputJson is null ? null
            : JsonSerializer.Deserialize<AgentTurnInput>(plan.ParentTurnInputJson, JsonDefaults.Options);
        if (node is null || node.Id != plan.ParentWorkflowNodeId
            || definition.Id != plan.ParentWorkflowId || plan.ProjectId != parent.ProjectId?.ToString()
            || input is null || input.RunId != parent.Id.ToString()
            || input.WorktreePath != parent.WorktreePath || input.WorktreeBranch != parent.WorktreeBranch
            || input.RepositoryPath != parent.RepositoryPath || input.SubmittingUser != parent.SubmittingUser
            || input.ProjectId != parent.ProjectId?.ToString()
            || parent.ArchivedAt is not null || parent.WorktreePath is null
            || parent.TreeHash is null || plan.ExecutionBaseTreeHash != parent.TreeHash
            || !worktrees.WorktreeExists(parent.WorktreePath)
            || worktrees.GetTreeHash(parent.WorktreePath) != parent.TreeHash)
            throw Rejected("composed_recovery_input_changed", parent.Id);
        using var repository = new Repository(parent.WorktreePath);
        if (repository.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).IsDirty)
            throw Rejected("composed_recovery_worktree_changed", parent.Id);
        return input;
    }

    private async Task LaunchAsync(
        Run parent, AgentTurnInput input, string nodeId, RunLeaseClaim lease, CancellationToken ct)
    {
        if (LaunchOverride is { } launch)
        {
            await launch(parent, input, nodeId, lease, ct).ConfigureAwait(false);
            return;
        }
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RunOrchestrator>()
            .StartRevisionAsync(
                parent, input.Task, ct, existingLease: lease,
                isAuthorizedAsync: async token =>
                    await leases.IsLeaseOwnerAsync(
                        parent.Id.ToString(), lease.OwnerId, lease.FencingToken, token).ConfigureAwait(false)
                    && (await runStore.GetAsync(parent.Id, token).ConfigureAwait(false)) is
                        { Status: RunStatus.InProgress } current
                    && current.LifecycleGeneration == lease.LifecycleGeneration,
                composedRecoveryInput: input, recoveryComposedNodeId: nodeId)
            .ConfigureAwait(false);
    }

    private WorkflowComposedRecoveryException Rejected(string code, RunId runId)
    {
        logger.LogWarning("Composed workflow recovery rejected for {RunId}: {Code}", runId, code);
        return new WorkflowComposedRecoveryException(code);
    }
}
