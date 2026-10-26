using System.Collections.Immutable;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

/// <summary>Read-only evidence about the latest checkpoint for a coordinator session.</summary>
public sealed record MafExecutionRecoverySnapshot(
    string State,
    string? UnavailableCode,
    string? CheckpointId,
    long? CheckpointRevision,
    string? WorkPlanId,
    long? CheckpointDecisionStateVersion,
    long CurrentDecisionStateVersion,
    ImmutableArray<MafExecutionRecoveryStepSnapshot> Steps,
    ImmutableArray<MafExecutionRecoveryPendingDispatchSnapshot> PendingChildDispatches,
    ImmutableArray<MafExecutionRecoveryPlatformOperationSnapshot> PlatformGateOperations,
    ImmutableArray<string> FailedDependencyIds,
    SessionStatusBlocker? PendingGate);

/// <summary>Checkpoint status and dependency readiness for one workflow step or work association.</summary>
public sealed record MafExecutionRecoveryStepSnapshot(
    string StepId,
    string? WorkItemId,
    string? AssociationId,
    string CheckpointStatus,
    string? Readiness,
    string? EffectRecoveryState);

/// <summary>A persisted child-dispatch intent whose delivery may be retried after restart.</summary>
public sealed record MafExecutionRecoveryPendingDispatchSnapshot(
    string AssociationId,
    string ChildSessionId,
    Guid MessageId,
    string EffectRecoveryState);

/// <summary>A platform-gate operation and the intent or terminal receipt recorded in the checkpoint.</summary>
public sealed record MafExecutionRecoveryPlatformOperationSnapshot(
    string StepId,
    WorkflowPlatformGate Gate,
    Guid OperationId,
    string EvidenceState,
    string? EffectRecoveryState);

/// <summary>Projects checkpoint evidence without reconciling, mutating, or advancing execution.</summary>
internal static class MafExecutionRecoveryProjector
{
    internal static MafExecutionRecoverySnapshot Project(
        MafExecutionCheckpointSnapshot? checkpoint,
        WorkPlanSnapshot? confirmedWorkPlan,
        long currentDecisionStateVersion,
        bool canDispatch,
        SessionStatusBlocker? pendingGate)
    {
        // Without a bound checkpoint and accepted plan, no execution state can be presented as current.
        if (checkpoint is null)
            return Unavailable(
                "maf_execution_checkpoint_missing", null, confirmedWorkPlan?.Plan.Id,
                currentDecisionStateVersion, pendingGate);
        if (confirmedWorkPlan is null)
            return Unavailable(
                "maf_execution_work_plan_unavailable", checkpoint, null, currentDecisionStateVersion, pendingGate);

        var state = checkpoint.State;
        var plan = confirmedWorkPlan;
        if (!string.Equals(state.WorkPlanId, plan.Plan.Id, StringComparison.Ordinal))
            return Unavailable(
                "maf_execution_checkpoint_plan_mismatch", checkpoint, plan.Plan.Id,
                currentDecisionStateVersion, pendingGate);
        if (state.DecisionStateVersion > currentDecisionStateVersion)
            return Unavailable(
                "maf_execution_checkpoint_decision_mismatch", checkpoint, plan.Plan.Id,
                currentDecisionStateVersion, pendingGate);
        if (!HasCompatibleProgress(plan, state))
            return Unavailable(
                "maf_execution_checkpoint_progress_mismatch", checkpoint, plan.Plan.Id,
                currentDecisionStateVersion, pendingGate);

        // The frontier reports dependency readiness only; the status read never dispatches or reconciles work.
        var frontier = MafExecutionPlanner.BuildFrontier(
            plan,
            state.Progress,
            registeredChildCount: 0,
            activeChildCount: 0,
            maximumChildren: int.MaxValue,
            maximumConcurrentChildren: int.MaxValue,
            hasNonModelExecutor: _ => true,
            fixedWorkAssociations: state.FixedWorkAssociations);
        var readyWorkItemIds = frontier.ReadyActions
            .Where(action => action.WorkItem is not null)
            .Select(action => action.WorkItem!.Id)
            .ToHashSet(StringComparer.Ordinal);
        var readyStepIds = frontier.ReadyActions
            .Where(action => action.WorkItem is null)
            .Select(action => action.Step.Id)
            .ToHashSet(StringComparer.Ordinal);
        var failedDependencyIds = frontier.FailedDependencyIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var steps = ProjectSteps(
            plan, state, readyWorkItemIds, readyStepIds,
            failedDependencyIds.ToHashSet(StringComparer.Ordinal), canDispatch);

        return new MafExecutionRecoverySnapshot(
            "available",
            null,
            checkpoint.Info.CheckpointId,
            state.Revision,
            state.WorkPlanId,
            state.DecisionStateVersion,
            currentDecisionStateVersion,
            steps,
            state.PendingDispatches.Values
                .OrderBy(intent => intent.AssociationId, StringComparer.Ordinal)
                .Select(intent => new MafExecutionRecoveryPendingDispatchSnapshot(
                    intent.AssociationId, intent.ChildSessionId, intent.MessageId, "mayRepeat"))
                .ToImmutableArray(),
            ProjectPlatformOperations(state),
            failedDependencyIds,
            pendingGate);
    }

    private static bool HasCompatibleProgress(
        WorkPlanSnapshot plan,
        MafExecutionCheckpoint state)
    {
        // A reused plan ID does not make old progress valid for associations removed by a confirmed replan.
        var steps = plan.Workflow.Definition.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var itemIds = plan.Plan.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (state.Progress.WorkItems.Any(item =>
                !itemIds.Contains(item.Key) || !Enum.IsDefined(item.Value)))
            return false;
        if (state.Progress.NonModelSteps.Any(item =>
                !steps.TryGetValue(item.Key, out var step) ||
                step.Mode != WorkflowStepMode.Platform ||
                !Enum.IsDefined(item.Value)))
            return false;

        foreach (var association in state.FixedWorkAssociations.Values
                     .Where(association => association.WorkPlanId == plan.Plan.Id))
        {
            if (!steps.TryGetValue(association.StepId, out var step) ||
                step.Mode != WorkflowStepMode.Fixed ||
                !string.Equals(step.FixedWork?.RoleId, association.Specification.RoleId, StringComparison.Ordinal))
                return false;
        }
        foreach (var (associationId, status) in state.Progress.FixedWorkItems)
        {
            if (!state.FixedWorkAssociations.TryGetValue(associationId, out var association) ||
                association.WorkPlanId != plan.Plan.Id ||
                !steps.TryGetValue(association.StepId, out var step) ||
                step.Mode != WorkflowStepMode.Fixed ||
                !string.Equals(step.FixedWork?.RoleId, association.Specification.RoleId, StringComparison.Ordinal) ||
                !Enum.IsDefined(status))
                return false;
        }

        return PlatformOperationsMatchPlan(
            state.BuildTestIntents.Keys.Concat(state.BuildTestReceipts.Keys),
            WorkflowPlatformGate.BuildTest,
            steps) &&
            PlatformOperationsMatchPlan(
                state.OpenPullRequestIntents.Keys.Concat(state.OpenPullRequestReceipts.Keys),
                WorkflowPlatformGate.OpenPullRequest,
                steps) &&
            PlatformOperationsMatchPlan(
                state.MergeIntents.Keys.Concat(state.MergeReceipts.Keys),
                WorkflowPlatformGate.Merge,
                steps);
    }

    private static bool PlatformOperationsMatchPlan(
        IEnumerable<string> stepIds,
        WorkflowPlatformGate gate,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps)
    {
        // Platform receipts are only current evidence when their step still declares the same gate.
        return stepIds.All(stepId =>
            steps.TryGetValue(stepId, out var step) &&
            step.Mode == WorkflowStepMode.Platform &&
            step.PlatformGate == gate);
    }

    private static ImmutableArray<MafExecutionRecoveryStepSnapshot> ProjectSteps(
        WorkPlanSnapshot plan,
        MafExecutionCheckpoint state,
        IReadOnlySet<string> readyWorkItemIds,
        IReadOnlySet<string> readyStepIds,
        IReadOnlySet<string> failedDependencyIds,
        bool canDispatch)
    {
        // Persisted running work is not treated as still active after restart; its effect remains uncertain.
        var steps = ImmutableArray.CreateBuilder<MafExecutionRecoveryStepSnapshot>();
        foreach (var item in plan.Plan.Items.OrderBy(item => item.WorkflowStepId, StringComparer.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var status = state.Progress.WorkItems.GetValueOrDefault(item.Id);
            steps.Add(new MafExecutionRecoveryStepSnapshot(
                item.WorkflowStepId,
                item.Id,
                item.Id,
                ToStatus(status),
                Readiness(item.Id, item.WorkflowStepId, status, readyWorkItemIds, readyStepIds,
                    failedDependencyIds, canDispatch),
                ChildEffectRecoveryState(item.Id, status, state)));
        }

        foreach (var association in state.FixedWorkAssociations.Values
                     .Where(association => association.WorkPlanId == plan.Plan.Id)
                     .OrderBy(association => association.StepId, StringComparer.Ordinal)
                     .ThenBy(association => association.AssociationId, StringComparer.Ordinal))
        {
            var status = state.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId);
            steps.Add(new MafExecutionRecoveryStepSnapshot(
                association.StepId,
                null,
                association.AssociationId,
                ToStatus(status),
                Readiness(association.AssociationId, association.StepId, status,
                    readyWorkItemIds, readyStepIds, failedDependencyIds, canDispatch),
                ChildEffectRecoveryState(association.AssociationId, status, state)));
        }

        foreach (var step in plan.Workflow.Definition.Steps
                     .Where(step => step.Mode == WorkflowStepMode.Platform ||
                         step.Mode == WorkflowStepMode.Fixed &&
                         !state.FixedWorkAssociations.Values.Any(association =>
                             association.WorkPlanId == plan.Plan.Id &&
                             string.Equals(association.StepId, step.Id, StringComparison.Ordinal)))
                     .OrderBy(step => step.Order))
        {
            var status = step.Mode == WorkflowStepMode.Platform
                ? state.Progress.NonModelSteps.GetValueOrDefault(step.Id)
                : MafExecutionTaskStatus.Pending;
            steps.Add(new MafExecutionRecoveryStepSnapshot(
                step.Id,
                null,
                null,
                ToStatus(status),
                Readiness(step.Id, step.Id, status, readyWorkItemIds, readyStepIds,
                    failedDependencyIds, canDispatch),
                PlatformEffectRecoveryState(step.Id, status, state)));
        }

        return steps.ToImmutable();
    }

    private static string? Readiness(
        string associationId,
        string stepId,
        MafExecutionTaskStatus status,
        IReadOnlySet<string> readyWorkItemIds,
        IReadOnlySet<string> readyStepIds,
        IReadOnlySet<string> failedDependencyIds,
        bool canDispatch)
    {
        // A failed frontier association is a terminal blocker, never a ready action.
        if (status != MafExecutionTaskStatus.Pending)
            return null;
        if (failedDependencyIds.Contains(associationId) || failedDependencyIds.Contains(stepId))
            return "blockedByFailedDependency";
        if (!readyWorkItemIds.Contains(associationId) && !readyStepIds.Contains(stepId))
            return "waitingOnDependencies";
        return canDispatch ? "dependencyReady" : "blockedByDecisionGate";
    }

    private static string? ChildEffectRecoveryState(
        string associationId,
        MafExecutionTaskStatus status,
        MafExecutionCheckpoint state)
    {
        // A saved dispatch intent may have reached the child before the parent stopped.
        if (state.PendingDispatches.ContainsKey(associationId))
            return "mayRepeat";
        if (status == MafExecutionTaskStatus.Running)
            return "indeterminate";
        return state.Results.ContainsKey(associationId) ? "checkpointResultRecorded" : null;
    }

    private static string? PlatformEffectRecoveryState(
        string stepId,
        MafExecutionTaskStatus status,
        MafExecutionCheckpoint state)
    {
        // Only a saved terminal receipt can resolve whether the external gate call returned.
        var hasIntent = state.BuildTestIntents.ContainsKey(stepId) ||
            state.OpenPullRequestIntents.ContainsKey(stepId) ||
            state.MergeIntents.ContainsKey(stepId);
        if (hasIntent && !HasPlatformReceipt(stepId, state))
            return "mayRepeat";
        if (status == MafExecutionTaskStatus.Running && !HasPlatformReceipt(stepId, state))
            return "indeterminate";
        return HasPlatformReceipt(stepId, state) ? "terminalReceiptRecorded" : null;
    }

    private static ImmutableArray<MafExecutionRecoveryPlatformOperationSnapshot> ProjectPlatformOperations(
        MafExecutionCheckpoint state)
    {
        // A terminal receipt is exposed as recorded evidence, never as a claim of physical completion or safety.
        var operations = ImmutableArray.CreateBuilder<MafExecutionRecoveryPlatformOperationSnapshot>();
        foreach (var (stepId, intent) in state.BuildTestIntents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var hasReceipt = state.BuildTestReceipts.ContainsKey(stepId);
            operations.Add(new MafExecutionRecoveryPlatformOperationSnapshot(
                stepId, WorkflowPlatformGate.BuildTest, intent.OperationId,
                hasReceipt ? "terminalReceiptRecorded" : "intentOnly",
                hasReceipt ? null : "mayRepeat"));
        }
        foreach (var (stepId, intent) in state.OpenPullRequestIntents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var hasReceipt = state.OpenPullRequestReceipts.ContainsKey(stepId);
            operations.Add(new MafExecutionRecoveryPlatformOperationSnapshot(
                stepId, WorkflowPlatformGate.OpenPullRequest, intent.OperationId,
                hasReceipt ? "terminalReceiptRecorded" : "intentOnly",
                hasReceipt ? null : "mayRepeat"));
        }
        // Merge uses the same recorded-evidence rule; a receipt is not a claim of physical exactly-once execution.
        foreach (var (stepId, intent) in state.MergeIntents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var hasReceipt = state.MergeReceipts.ContainsKey(stepId);
            operations.Add(new MafExecutionRecoveryPlatformOperationSnapshot(
                stepId, WorkflowPlatformGate.Merge, intent.OperationId,
                hasReceipt ? "terminalReceiptRecorded" : "intentOnly",
                hasReceipt ? null : "mayRepeat"));
        }

        return operations
            .OrderBy(operation => operation.StepId, StringComparer.Ordinal)
            .ThenBy(operation => operation.Gate)
            .ToImmutableArray();
    }

    private static bool HasPlatformReceipt(string stepId, MafExecutionCheckpoint state) =>
        state.BuildTestReceipts.ContainsKey(stepId) ||
        state.OpenPullRequestReceipts.ContainsKey(stepId) ||
        state.MergeReceipts.ContainsKey(stepId);

    private static string ToStatus(MafExecutionTaskStatus status) =>
        status.ToString().ToLowerInvariant();

    private static MafExecutionRecoverySnapshot Unavailable(
        string code,
        MafExecutionCheckpointSnapshot? checkpoint,
        string? workPlanId,
        long currentDecisionStateVersion,
        SessionStatusBlocker? pendingGate)
    {
        // Unknown checkpoint state intentionally exposes no steps or effect-recovery claims.
        return new(
            "unavailable",
            code,
            checkpoint?.Info.CheckpointId,
            checkpoint?.State.Revision,
            workPlanId ?? checkpoint?.State.WorkPlanId,
            checkpoint?.State.DecisionStateVersion,
            currentDecisionStateVersion,
            [],
            [],
            [],
            [],
            pendingGate);
    }
}
