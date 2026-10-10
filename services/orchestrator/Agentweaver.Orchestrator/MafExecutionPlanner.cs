using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record MafExecutionAction(
    WorkflowStepDefinition Step,
    WorkPlanItem? WorkItem);

internal sealed record MafExecutionFrontier(
    ImmutableArray<MafExecutionAction> ReadyActions,
    ImmutableArray<string> UnavailableExecutorStepIds,
    ImmutableArray<string> FailedDependencyIds,
    bool IsComplete);

internal sealed record MafExecutionChildSnapshot(
    string AssociationId,
    SessionIdentity Identity,
    CoordinationLifecycleState Lifecycle,
    long ExecutionFence)
{
    public bool Detached { get; init; }
}

internal static class MafExecutionPlanner
{
    internal static string CreateChildSessionId(
        SessionIdentity parent,
        string workPlanId,
        string associationId) =>
        MafExecutionIds.CreateChildSessionId(parent, workPlanId, associationId);

    internal static Guid CreateDispatchMessageId(
        SessionIdentity parent,
        string workPlanId,
        string associationId)
    {
        var scope = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            parent.ProjectId, parent.RunId, parent.SessionId, workPlanId, associationId, "maf-a2a"
        });
        return new Guid(SHA256.HashData(scope).AsSpan(0, 16));
    }

    internal static bool HasUnsupportedPromptControls(string prompt) =>
        prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    internal static MafExecutionFixedWorkAssociation CreateFixedWorkAssociation(
        WorkPlanSnapshot snapshot,
        WorkPlanRunSelectionContext selectionContext,
        SessionIdentity parent,
        WorkflowStepDefinition step,
        string acceptedSelectionHash,
        long executionFence,
        long decisionStateVersion,
        long activationRevision,
        string childSessionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectionContext);
        ArgumentNullException.ThrowIfNull(step);
        if (step.Mode != WorkflowStepMode.Fixed || step.FixedWork is not { } fixedWork ||
            !snapshot.Workflow.Definition.Steps.Contains(step) ||
            executionFence < 1 || decisionStateVersion < 1 || activationRevision < 1 ||
            acceptedSelectionHash is null || acceptedSelectionHash.Length != 64 ||
            !acceptedSelectionHash.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "maf_fixed_work_unavailable", StatusCodes.Status409Conflict);

        var roles = selectionContext.Roles
            .Where(role => string.Equals(role.RoleId, fixedWork.RoleId, StringComparison.Ordinal))
            .ToArray();
        var role = roles.Length == 1 ? roles[0] : null;
        var agents = role?.EligibleAgentIds.IsDefault == false
            ? role.EligibleAgentIds.Order(StringComparer.Ordinal).ToArray()
            : [];
        var modelReferences = role?.ModelSelectionReferences.IsDefault == false
            ? role.ModelSelectionReferences
            : [];
        var isolationProvider = snapshot.IsolationProviderBinding?.ProviderId;
        if (agents.Length == 0 || modelReferences.Length != 1 ||
            string.IsNullOrWhiteSpace(modelReferences[0]) ||
            string.IsNullOrWhiteSpace(isolationProvider))
            throw new CoordinationException(
                "maf_fixed_work_selection_unavailable", StatusCodes.Status409Conflict);

        var associationId = CreateFixedWorkAssociationId(
            parent, snapshot.Plan.Id, step.Id, activationRevision);
        if (snapshot.Plan.Items.Any(item =>
                string.Equals(item.Id, associationId, StringComparison.Ordinal)))
            throw new CoordinationException(
                "maf_fixed_work_association_conflict", StatusCodes.Status409Conflict);
        var expectedChildSessionId = CreateChildSessionId(parent, snapshot.Plan.Id, associationId);
        if (!string.Equals(childSessionId, expectedChildSessionId, StringComparison.Ordinal))
            throw new CoordinationException(
                "maf_fixed_work_child_identity_invalid", StatusCodes.Status409Conflict);

        return new MafExecutionFixedWorkAssociation(
            associationId,
            activationRevision,
            snapshot.Plan.Id,
            snapshot.Workflow.Definition.Id,
            snapshot.Workflow.Definition.Revision,
            snapshot.Workflow.Definition.CatalogVersion,
            step.Id,
            fixedWork,
            agents[0],
            modelReferences[0],
            isolationProvider,
            acceptedSelectionHash.ToUpperInvariant(),
            executionFence,
            decisionStateVersion,
            childSessionId);
    }

    internal static string CreateFixedWorkAssociationId(
        SessionIdentity parent,
        string workPlanId,
        string stepId,
        long activationRevision) =>
        MafExecutionIds.CreateFixedWorkAssociationId(parent, workPlanId, stepId, activationRevision);

    internal static MafExecutionProgress ReconcileChildren(
        WorkPlanSnapshot snapshot,
        MafExecutionProgress progress,
        SessionIdentity parent,
        ImmutableArray<MafExecutionChildSnapshot> children,
        long executionFence,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation>? fixedWorkAssociations = null,
        IReadOnlySet<string>? pendingDispatchIds = null,
        IReadOnlySet<string>? joinedResultIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(progress);
        if (children.IsDefault || executionFence < 1)
            throw new ArgumentException("Current owner child snapshots and fence are required.", nameof(children));

        fixedWorkAssociations ??= ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
            .WithComparers(StringComparer.Ordinal);
        var items = snapshot.Plan.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var reconciled = progress.WorkItems;
        var reconciledFixed = progress.FixedWorkItems;
        var childItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (!childItemIds.Add(child.AssociationId) ||
                child.Identity.ProjectId != parent.ProjectId ||
                child.Identity.RunId != parent.RunId ||
                child.Identity.SessionId != CreateChildSessionId(
                    parent, snapshot.Plan.Id, child.AssociationId))
                throw new CoordinationException(
                    "maf_execution_child_identity_conflict", StatusCodes.Status409Conflict);

            if (items.ContainsKey(child.AssociationId))
            {
                var current = reconciled.GetValueOrDefault(child.AssociationId);
                if (current == MafExecutionTaskStatus.Pending)
                    throw new CoordinationException(
                        "maf_execution_child_checkpoint_conflict", StatusCodes.Status409Conflict);
                var hasJoinedResult = joinedResultIds?.Contains(child.AssociationId) == true;
                if (current == MafExecutionTaskStatus.Succeeded)
                {
                    if (!hasJoinedResult || child.ExecutionFence != executionFence ||
                        child.Lifecycle is not (CoordinationLifecycleState.Completed or
                            CoordinationLifecycleState.Archived))
                        throw new CoordinationException(
                            "maf_execution_child_checkpoint_conflict", StatusCodes.Status409Conflict);
                    continue;
                }
                if (pendingDispatchIds?.Contains(child.AssociationId) == true)
                    continue;
                if (child.Detached)
                    continue;
                var observed = ObserveChild(child, executionFence);
                reconciled = ReconcileStatus(reconciled, child.AssociationId, current, observed);
                continue;
            }

            if (!fixedWorkAssociations.TryGetValue(child.AssociationId, out var fixedWork) ||
                !string.Equals(fixedWork.WorkPlanId, snapshot.Plan.Id, StringComparison.Ordinal) ||
                !string.Equals(fixedWork.ChildSessionId, child.Identity.SessionId, StringComparison.Ordinal))
                throw new CoordinationException(
                    "maf_execution_child_identity_conflict", StatusCodes.Status409Conflict);
            var fixedStatus = reconciledFixed.GetValueOrDefault(child.AssociationId);
            if (fixedStatus == MafExecutionTaskStatus.Pending)
                throw new CoordinationException(
                    "maf_execution_child_checkpoint_conflict", StatusCodes.Status409Conflict);
            var fixedHasJoinedResult = joinedResultIds?.Contains(child.AssociationId) == true;
            if (fixedStatus == MafExecutionTaskStatus.Succeeded)
            {
                if (!fixedHasJoinedResult || child.ExecutionFence != executionFence ||
                    child.Lifecycle is not (CoordinationLifecycleState.Completed or
                        CoordinationLifecycleState.Archived))
                    throw new CoordinationException(
                        "maf_execution_child_checkpoint_conflict", StatusCodes.Status409Conflict);
                continue;
            }
            if (pendingDispatchIds?.Contains(child.AssociationId) == true)
                continue;
            if (child.Detached)
                continue;
            reconciledFixed = ReconcileStatus(
                reconciledFixed,
                child.AssociationId,
                fixedStatus,
                ObserveChild(child, executionFence));
        }

        foreach (var (itemId, status) in reconciled)
        {
            if (status != MafExecutionTaskStatus.Pending && !childItemIds.Contains(itemId))
                throw new CoordinationException(
                    "maf_execution_child_unavailable", StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var (associationId, status) in reconciledFixed)
        {
            if (status != MafExecutionTaskStatus.Pending && !childItemIds.Contains(associationId))
                throw new CoordinationException(
                    "maf_execution_child_unavailable", StatusCodes.Status503ServiceUnavailable);
        }

        return progress with { WorkItems = reconciled, FixedWorkItems = reconciledFixed };
    }

    internal static MafExecutionFrontier BuildFrontier(
        WorkPlanSnapshot snapshot,
        MafExecutionProgress progress,
        int registeredChildCount,
        int activeChildCount,
        int maximumChildren,
        int maximumConcurrentChildren,
        Func<WorkflowStepDefinition, bool> hasNonModelExecutor,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation>? fixedWorkAssociations = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(hasNonModelExecutor);
        if (registeredChildCount < 0 || activeChildCount < 0 || maximumChildren < 0 ||
            registeredChildCount > maximumChildren || maximumConcurrentChildren < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentChildren));

        var definition = snapshot.Workflow.Definition;
        var steps = definition.Steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        var itemsById = snapshot.Plan.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var itemsByStep = snapshot.Plan.Items
            .GroupBy(item => item.WorkflowStepId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        fixedWorkAssociations ??= ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
            .WithComparers(StringComparer.Ordinal);
        ValidateProgress(progress, steps, itemsById, fixedWorkAssociations);

        var stepStatuses = definition.Steps.ToDictionary(
            step => step.Id,
            step => StepStatus(step, snapshot.Plan.Id, progress, itemsByStep, fixedWorkAssociations),
            StringComparer.Ordinal);
        var ready = ImmutableArray.CreateBuilder<MafExecutionAction>();
        var unavailable = ImmutableArray.CreateBuilder<string>();
        var failedDependencies = ImmutableArray.CreateBuilder<string>();
        var availableChildSlots = Math.Min(
            maximumChildren - registeredChildCount,
            Math.Max(0, maximumConcurrentChildren - activeChildCount));
        var nonModelEffectRunning = progress.NonModelSteps.Values
            .Any(status => status == MafExecutionTaskStatus.Running);
        var nonModelEffectScheduled = false;

        foreach (var step in definition.Steps.OrderBy(step => step.Order))
        {
            var stepStatus = stepStatuses[step.Id];
            if (stepStatus == MafExecutionTaskStatus.Succeeded ||
                step.Mode != WorkflowStepMode.Open &&
                stepStatus is MafExecutionTaskStatus.Failed or MafExecutionTaskStatus.Indeterminate)
                continue;

            var stepDependencies = step.DependsOn
                .Select(dependency => (Id: dependency, Status: stepStatuses[dependency]))
                .ToArray();
            if (stepDependencies.Any(dependency => dependency.Status != MafExecutionTaskStatus.Succeeded))
            {
                if (stepDependencies.Any(dependency => IsTerminalFailure(dependency.Status)))
                    failedDependencies.Add(step.Id);
                continue;
            }

            if (step.Mode == WorkflowStepMode.Fixed)
            {
                if (stepStatus != MafExecutionTaskStatus.Pending || availableChildSlots == 0)
                    continue;
                ready.Add(new MafExecutionAction(step, null));
                availableChildSlots--;
                continue;
            }

            if (step.Mode == WorkflowStepMode.Platform)
            {
                if (stepStatus != MafExecutionTaskStatus.Pending)
                    continue;
                if (nonModelEffectRunning || nonModelEffectScheduled)
                    continue;
                if (!hasNonModelExecutor(step))
                {
                    unavailable.Add(step.Id);
                    continue;
                }

                ready.Add(new MafExecutionAction(step, null));
                nonModelEffectScheduled = true;
                continue;
            }

            if (step.Mode != WorkflowStepMode.Open || availableChildSlots == 0 ||
                !itemsByStep.TryGetValue(step.Id, out var workItems))
                continue;

            foreach (var item in workItems)
            {
                var status = progress.WorkItems.GetValueOrDefault(item.Id);
                if (status != MafExecutionTaskStatus.Pending)
                    continue;

                var dependencies = item.DependsOn
                    .Select(dependency => (Id: dependency, Status: progress.WorkItems.GetValueOrDefault(dependency)))
                    .ToArray();
                if (dependencies.Any(dependency => dependency.Status != MafExecutionTaskStatus.Succeeded))
                {
                    if (dependencies.Any(dependency => IsTerminalFailure(dependency.Status)))
                        failedDependencies.Add(item.Id);
                    continue;
                }

                ready.Add(new MafExecutionAction(step, item));
                availableChildSlots--;
                if (availableChildSlots == 0)
                    break;
            }
        }

        return new MafExecutionFrontier(
            ready.ToImmutable(),
            unavailable.ToImmutable(),
            failedDependencies.ToImmutable(),
            stepStatuses.Values.All(status => status == MafExecutionTaskStatus.Succeeded));
    }

    private static MafExecutionTaskStatus StepStatus(
        WorkflowStepDefinition step,
        string workPlanId,
        MafExecutionProgress progress,
        IReadOnlyDictionary<string, WorkPlanItem[]> itemsByStep,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation> fixedWorkAssociations)
    {
        if (step.Mode == WorkflowStepMode.Platform)
            return progress.NonModelSteps.GetValueOrDefault(step.Id);
        if (step.Mode == WorkflowStepMode.Fixed)
        {
            var fixedItems = fixedWorkAssociations.Values
                .Where(item => item.WorkPlanId == workPlanId &&
                    string.Equals(item.StepId, step.Id, StringComparison.Ordinal))
                .Select(item => progress.FixedWorkItems.GetValueOrDefault(item.AssociationId))
                .ToArray();
            return AggregateStatus(fixedItems);
        }
        if (!itemsByStep.TryGetValue(step.Id, out var items) || items.Length == 0)
            return MafExecutionTaskStatus.Succeeded;

        var statuses = items.Select(item => progress.WorkItems.GetValueOrDefault(item.Id)).ToArray();
        return AggregateStatus(statuses);
    }

    private static MafExecutionTaskStatus AggregateStatus(MafExecutionTaskStatus[] statuses)
    {
        if (statuses.Length == 0)
            return MafExecutionTaskStatus.Pending;
        if (statuses.Any(status => status == MafExecutionTaskStatus.Indeterminate))
            return MafExecutionTaskStatus.Indeterminate;
        if (statuses.Any(status => status == MafExecutionTaskStatus.Failed))
            return MafExecutionTaskStatus.Failed;
        if (statuses.All(status => status == MafExecutionTaskStatus.Succeeded))
            return MafExecutionTaskStatus.Succeeded;
        return statuses.Any(status => status == MafExecutionTaskStatus.Running)
            ? MafExecutionTaskStatus.Running
            : MafExecutionTaskStatus.Pending;
    }

    private static void ValidateProgress(
        MafExecutionProgress progress,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps,
        IReadOnlyDictionary<string, WorkPlanItem> items,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation> fixedWorkAssociations)
    {
        foreach (var (id, status) in progress.WorkItems)
        {
            if (!items.ContainsKey(id) || !Enum.IsDefined(status))
                throw new ArgumentException("MAF progress contains an unknown work item or status.", nameof(progress));
        }

        foreach (var (id, status) in progress.NonModelSteps)
        {
            if (!steps.TryGetValue(id, out var step) || step.Mode != WorkflowStepMode.Platform ||
                !Enum.IsDefined(status))
                throw new ArgumentException("MAF progress contains an unknown non-model step or status.", nameof(progress));
        }

        foreach (var (id, status) in progress.FixedWorkItems)
        {
            if (!fixedWorkAssociations.TryGetValue(id, out var association) ||
                !steps.TryGetValue(association.StepId, out var step) ||
                step.Mode != WorkflowStepMode.Fixed ||
                !string.Equals(step.FixedWork?.RoleId, association.Specification.RoleId, StringComparison.Ordinal) ||
                !Enum.IsDefined(status))
                throw new ArgumentException("MAF progress contains an unknown fixed-work association or status.",
                    nameof(progress));
        }
    }

    private static MafExecutionTaskStatus ObserveChild(
        MafExecutionChildSnapshot child,
        long executionFence)
    {
        if (child.ExecutionFence != executionFence)
            return MafExecutionTaskStatus.Indeterminate;
        return child.Lifecycle switch
        {
            CoordinationLifecycleState.Active => MafExecutionTaskStatus.Running,
            _ => MafExecutionTaskStatus.Indeterminate
        };
    }

    private static ImmutableDictionary<string, MafExecutionTaskStatus> ReconcileStatus(
        ImmutableDictionary<string, MafExecutionTaskStatus> progress,
        string id,
        MafExecutionTaskStatus current,
        MafExecutionTaskStatus observed)
    {
        if (current == observed)
            return progress;
        if (current != MafExecutionTaskStatus.Running ||
            observed is not (MafExecutionTaskStatus.Succeeded or MafExecutionTaskStatus.Indeterminate))
            throw new CoordinationException(
                "maf_execution_child_checkpoint_conflict", StatusCodes.Status409Conflict);
        return progress.SetItem(id, observed);
    }

    private static bool IsTerminalFailure(MafExecutionTaskStatus status) =>
        status is MafExecutionTaskStatus.Failed or MafExecutionTaskStatus.Indeterminate;
}
