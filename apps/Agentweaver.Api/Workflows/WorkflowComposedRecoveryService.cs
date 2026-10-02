using System.Data;
using System.Globalization;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Git;
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
    internal Func<CancellationToken, Task>? AfterCommitOverride { get; set; }

    internal static bool IsDecompositionFailure(Run run) =>
        run.ParentRunId is null && run.GetExecutableWorkflowPin() is not null
        && run.Result?.StartsWith("composed_decomposition_failed:", StringComparison.Ordinal) == true;

    public async Task ResumeFailedAsync(Run expectedParent, CancellationToken ct)
    {
        var pending = expectedParent.Status == RunStatus.InProgress
            && await CanRetryPendingAsync(expectedParent, ct).ConfigureAwait(false);
        if (!pending && (expectedParent.Status != RunStatus.Failed || !IsDecompositionFailure(expectedParent)))
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
            if (pending)
            {
                var current = await runStore.GetAsync(expectedParent.Id, ct).ConfigureAwait(false);
                if (current is null || current.Status != RunStatus.InProgress
                    || current.LifecycleGeneration != expectedParent.LifecycleGeneration
                    || current.TreeHash != expectedParent.TreeHash
                    || current.WorktreePath != expectedParent.WorktreePath
                    || current.WorktreeBranch != expectedParent.WorktreeBranch
                    || !await TryRestartPendingAsync(current,
                        new RunLeaseClaim(owner, acquired.FencingToken, current.LifecycleGeneration),
                        ct, unlaunchedOnly: true).ConfigureAwait(false))
                    throw Rejected("composed_recovery_run_changed", expectedParent.Id);
                transferred = true;
                return;
            }
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
                    && !db.Subtasks.Any(subtask => subtask.WorkPlanId == plan.Id
                        && (subtask.Status != SubtaskStatus.Pending
                            || subtask.ChildRunId != null || subtask.PriorChildRunId != null
                            || subtask.CancellationRequestedAt != null
                            || subtask.RecoveryAttempts != 0 || subtask.InfrastructureRetryCount != 0
                            || subtask.LastResetDirectiveId != null || subtask.LastResetAttempt != null
                            || subtask.RevisionInputRevisionId != null
                            || subtask.RevisionInputCommitHash != null))
                    && !db.Runs.Any(run => run.ParentRunId == plan.CoordinatorRunId)
                    && !db.ExecutionIdentities.Any(identity =>
                        identity.ParentRunId == plan.CoordinatorRunId))
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
                || parent.Result != expectedParent.Result || parent.TreeHash != expectedParent.TreeHash)
                throw Rejected("composed_recovery_run_changed", parent.Id);
            var childRecord = await db.Runs.AsNoTracking()
                .SingleOrDefaultAsync(run => run.RunId == plan.CoordinatorRunId, ct).ConfigureAwait(false);
            if (childRecord is null)
                throw Rejected("composed_recovery_coordinator_unavailable", parent.Id);
            var child = EfRunStore.FromRecord(childRecord);
            var provider = await scope.ServiceProvider.GetRequiredService<RunModelProviderSnapshotStore>()
                .TryGetAsync(parent, ct).ConfigureAwait(false);
            var input = ValidateInput(parent, plan, provider);
            var failure = plan.ParentResumeResultJson is null ? null
                : JsonSerializer.Deserialize<WorkflowChildWorkResult>(
                    plan.ParentResumeResultJson, JsonDefaults.Options);
            if (child.Status != RunStatus.Failed || child.ParentRunId != parent.Id.ToString()
                || child.ProjectId != parent.ProjectId || child.Result != parent.Result
                || child.LifecycleGeneration != parent.LifecycleGeneration
                || child.ArchivedAt is not null
                || child.SubtaskId != WorkflowChildWorkService.ChildCoordinatorSubtaskKey(plan.ParentWorkflowNodeId!)
                || failure is null || failure.Succeeded || failure.WorkPlanId != plan.Id
                || failure.ChildCoordinatorRunId != child.Id.ToString()
                || failure.FailureReason != parent.Result || failure.Assembly is not null
                || plan.CoordinatorPodId is not null || plan.MergeEffectId is not null
                || plan.ParentResumeState != WorkflowChildWorkResumeStates.Delivered
                || plan.ParentRecoveryGeneration is not null)
                throw Rejected("composed_recovery_not_eligible", parent.Id);
            var pendingGate = await db.PendingRequests
                .SingleOrDefaultAsync(request => request.RunId == parent.Id.ToString(), ct)
                .ConfigureAwait(false);
            if (pendingGate is null
                || pendingGate.RequestId != plan.ParentResumeRequestId
                    || pendingGate.DeliveryKind != PendingRequestDeliveryKinds.WorkflowChildWork
                    || pendingGate.DeliveryState != PendingRequestDeliveryStates.Delivered
                    || pendingGate.OwnerUser != parent.SubmittingUser)
                throw Rejected("composed_recovery_gate_changed", parent.Id);

            // Validate the durable provider before reopening either identity.
            await scope.ServiceProvider.GetRequiredService<RunOrchestrator>()
                .ValidateComposedRecoveryLaunchAsync(parent, ct).ConfigureAwait(false);
            if (parent.TreeHash != plan.ExecutionBaseTreeHash)
            {
                var aligned = await db.Runs.Where(run => run.RunId == parent.Id.ToString()
                        && run.Status == RunStatus.Failed.ToApiString()
                        && run.LifecycleGeneration == parent.LifecycleGeneration
                        && run.Result == parent.Result && run.TreeHash == parent.TreeHash
                        && run.WorktreePath == parent.WorktreePath
                        && run.WorktreeBranch == parent.WorktreeBranch
                        && run.CurrentOutputRevisionId == null
                        && run.ApprovedOutputRevisionId == null
                        && run.OwnerId == owner && run.FencingToken == acquired.FencingToken)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(run => run.TreeHash, plan.ExecutionBaseTreeHash), ct)
                    .ConfigureAwait(false);
                if (aligned != 1)
                    throw Rejected("composed_recovery_run_changed", parent.Id);
                parent = parent with { TreeHash = plan.ExecutionBaseTreeHash };
            }
            var lease = new RunLeaseClaim(owner, acquired.FencingToken, parent.LifecycleGeneration);
            if (!await EfRunStore.TryReopenTerminalOnContextAsync(
                    db, parent.Id, ct, parent, lease, clearResult: true).ConfigureAwait(false)
                || !await EfRunStore.TryReopenTerminalOnContextAsync(
                    db, child.Id, ct, child, clearResult: true).ConfigureAwait(false))
                throw Rejected("composed_recovery_run_changed", parent.Id);
            var resumedGeneration = parent.LifecycleGeneration + 1;
            plan.Status = WorkPlanStatus.Planned;
            plan.ParentResumeRequestId = null;
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
            db.PendingRequests.Remove(pendingGate);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (AfterCommitOverride is { } afterCommit)
                await afterCommit(ct).ConfigureAwait(false);
            parent = await runStore.GetAsync(parent.Id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Recovered composed workflow disappeared.");
            var committedPlan = await db.WorkPlans.AsNoTracking()
                .SingleAsync(saved => saved.Id == plan.Id, ct).ConfigureAwait(false);
            if (parent.Status != RunStatus.InProgress || parent.LifecycleGeneration != resumedGeneration
                || committedPlan.ParentRunId != parent.Id.ToString()
                || committedPlan.CoordinatorRunId != child.Id.ToString()
                || committedPlan.AssemblyStatusReason != RecoveryMarkerPrefix
                    + resumedGeneration.ToString(CultureInfo.InvariantCulture)
                || committedPlan.ParentRecoveryGeneration != resumedGeneration
                || committedPlan.CoordinatorCancellationRequestedAt is not null
                || !await IsUnlaunchedAsync(db, parent, committedPlan, ct).ConfigureAwait(false))
                throw Rejected("composed_recovery_run_changed", parent.Id);
            var postCommitProvider = await scope.ServiceProvider.GetRequiredService<RunModelProviderSnapshotStore>()
                .TryGetAsync(parent, ct).ConfigureAwait(false);
            ValidateInput(parent, committedPlan, postCommitProvider);
            await scope.ServiceProvider.GetRequiredService<RunOrchestrator>()
                .ValidateComposedRecoveryLaunchAsync(parent, ct).ConfigureAwait(false);
            if (!await leases.IsLeaseOwnerAsync(parent.Id.ToString(), owner, acquired.FencingToken, ct)
                    .ConfigureAwait(false))
                throw Rejected("composed_recovery_busy", parent.Id);
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

    public async Task<bool> TryRestartPendingAsync(
        Run parent, RunLeaseClaim lease, CancellationToken ct, bool unlaunchedOnly = false)
    {
        if (!await leases.IsLeaseOwnerAsync(parent.Id.ToString(), lease.OwnerId, lease.FencingToken, ct)
                .ConfigureAwait(false))
            throw Rejected("composed_recovery_busy", parent.Id);
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
        if (unlaunchedOnly && !await IsUnlaunchedAsync(db, parent, plan, ct).ConfigureAwait(false))
            throw Rejected("composed_recovery_run_changed", parent.Id);
        if (parent.Status is not (RunStatus.InProgress or RunStatus.AwaitingReview)
            || lease.LifecycleGeneration != parent.LifecycleGeneration)
            throw Rejected("composed_recovery_run_changed", parent.Id);
        var provider = await scope.ServiceProvider.GetRequiredService<RunModelProviderSnapshotStore>()
            .TryGetAsync(parent, ct).ConfigureAwait(false);
        var input = ValidateInput(parent, plan, provider);
        await scope.ServiceProvider.GetRequiredService<RunOrchestrator>()
            .ValidateComposedRecoveryLaunchAsync(parent, ct).ConfigureAwait(false);
        if (parent.Status == RunStatus.AwaitingReview)
        {
            if (!await runStore.TryResumeFromChildWorkAsync(
                    parent.Id, parent.LifecycleGeneration, ct).ConfigureAwait(false))
                throw Rejected("composed_recovery_run_changed", parent.Id);
            parent = parent with { Status = RunStatus.InProgress };
        }
        if (!await leases.IsLeaseOwnerAsync(parent.Id.ToString(), lease.OwnerId, lease.FencingToken, ct)
                .ConfigureAwait(false))
            throw Rejected("composed_recovery_busy", parent.Id);
        await LaunchAsync(parent, input, plan.ParentWorkflowNodeId!, lease, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CanRetryPendingAsync(Run parent, CancellationToken ct)
    {
        if (parent.Status != RunStatus.InProgress || parent.ParentRunId is not null)
            return false;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var marker = RecoveryMarkerPrefix + parent.LifecycleGeneration.ToString(CultureInfo.InvariantCulture);
        var plan = await db.WorkPlans.AsNoTracking().SingleOrDefaultAsync(plan =>
            plan.ParentRunId == parent.Id.ToString()
            && plan.ParentRecoveryGeneration == parent.LifecycleGeneration
            && plan.AssemblyStatusReason == marker
            && plan.ParentWorkflowNodeId != null && plan.ParentJoinNodeId == null
            && plan.CoordinatorCancellationRequestedAt == null, ct).ConfigureAwait(false);
        return plan is not null && await IsUnlaunchedAsync(db, parent, plan, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsUnlaunchedAsync(
        MemoryDbContext db, Run parent, WorkPlan plan, CancellationToken ct) =>
        plan.Status == WorkPlanStatus.Planned
        && plan.ParentResumeState == WorkflowChildWorkResumeStates.Committed
        && plan.ParentResumeRequestId is null && plan.ParentResumeResultJson is null
        && plan.CoordinatorCancellationRequestedAt is null
        && !await db.PendingRequests.AsNoTracking()
            .AnyAsync(request => request.RunId == parent.Id.ToString(), ct).ConfigureAwait(false)
        && !await db.Subtasks.AsNoTracking().AnyAsync(subtask => subtask.WorkPlanId == plan.Id
            && (subtask.Status != SubtaskStatus.Pending || subtask.ChildRunId != null
                || subtask.PriorChildRunId != null || subtask.CancellationRequestedAt != null
                || subtask.RecoveryAttempts != 0 || subtask.InfrastructureRetryCount != 0
                || subtask.LastResetDirectiveId != null || subtask.LastResetAttempt != null
                || subtask.RevisionInputRevisionId != null
                || subtask.RevisionInputCommitHash != null), ct).ConfigureAwait(false)
        && !await db.Runs.AsNoTracking().AnyAsync(run =>
            run.ParentRunId == plan.CoordinatorRunId, ct).ConfigureAwait(false)
        && await db.Runs.AsNoTracking().AnyAsync(child =>
            child.RunId == plan.CoordinatorRunId && child.ParentRunId == parent.Id.ToString()
            && child.Status == RunStatus.InProgress.ToApiString()
            && child.LifecycleGeneration == parent.LifecycleGeneration, ct).ConfigureAwait(false);

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

    private AgentTurnInput ValidateInput(
        Run parent, WorkPlan plan, ResolvedRunModelProviderBoundary? provider)
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
            || input.OriginatingBranch != parent.OriginatingBranch
            || input.ModelSource != parent.ModelSource.ToApiString()
            || input.ModelId != parent.ModelId
            || provider is null || provider.Provider.ToModelSource() != parent.ModelSource
            || input.ByokProviderFingerprint != provider.ByokProviderFingerprint
            || parent.ArchivedAt is not null || parent.WorktreePath is null
            || parent.ParentRunId is not null || parent.TreeHash is null
            || parent.CurrentOutputRevisionId is not null
            || parent.ApprovedOutputRevisionId is not null
            || plan.ExecutionBaseTreeHash is null
            || input.WorktreeBranch != WorktreeManager.BranchNameFor(parent.Id))
            throw Rejected("composed_recovery_input_changed", parent.Id);
        ValidatePhysicalTree(parent, plan.ExecutionBaseTreeHash);
        using var repository = new Repository(parent.WorktreePath);
        if (parent.TreeHash != plan.ExecutionBaseTreeHash
            && (parent.Status != RunStatus.Failed || repository.Head.Tip?.Parents.Count() != 1
                || repository.Head.Tip.Parents.Single().Tree.Sha != parent.TreeHash))
            throw Rejected("composed_recovery_input_changed", parent.Id);
        return input;
    }

    private void ValidatePhysicalTree(Run parent, string treeHash)
    {
        if (parent.WorktreePath is null || !worktrees.WorktreeExists(parent.WorktreePath)
            || worktrees.GetTreeHash(parent.WorktreePath) != treeHash)
            throw Rejected("composed_recovery_input_changed", parent.Id);
        using var repository = new Repository(parent.WorktreePath);
        if (repository.Info.IsHeadDetached
            || repository.Head.FriendlyName != WorktreeManager.BranchNameFor(parent.Id)
            || repository.Head.Tip?.Tree.Sha != treeHash
            || repository.RetrieveStatus(new StatusOptions { IncludeUntracked = true }).IsDirty)
            throw Rejected("composed_recovery_worktree_changed", parent.Id);
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
