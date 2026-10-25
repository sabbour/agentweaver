using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// A ready workflow action and its optional model work item.
/// Platform and fixed-work actions have no model work item.
/// </summary>
internal sealed record MafExecutionAction(
    WorkflowStepDefinition Step,
    WorkPlanItem? WorkItem);

/// <summary>
/// The planner's ready actions, missing executors, failed dependencies, and completion result for a checkpoint.
/// </summary>
internal sealed record MafExecutionFrontier(
    ImmutableArray<MafExecutionAction> ReadyActions,
    ImmutableArray<string> UnavailableExecutorStepIds,
    ImmutableArray<string> FailedDependencyIds,
    bool IsComplete);

/// <summary>
/// A current child-session snapshot paired with the association it serves and its execution fence.
/// </summary>
internal sealed record MafExecutionChildSnapshot(
    string AssociationId,
    SessionIdentity Identity,
    CoordinationLifecycleState Lifecycle,
    long ExecutionFence)
{
    public bool Detached { get; init; }
}

/// <summary>
/// Computes deterministic child identities, reconciles child lifecycle against checkpoint progress, and selects ready work.
/// </summary>
internal static class MafExecutionPlanner
{
    /// <summary>
    /// Creates the deterministic child-session identifier for a parent plan association.
    /// </summary>
    internal static string CreateChildSessionId(
        SessionIdentity parent,
        string workPlanId,
        string associationId) =>
        MafExecutionIds.CreateChildSessionId(parent, workPlanId, associationId);

    /// <summary>
    /// Creates a stable message ID so retrying dispatch for the same work association reuses the same addressed message.
    /// </summary>
    internal static Guid CreateDispatchMessageId(
        SessionIdentity parent,
        string workPlanId,
        string associationId)
    {
        // Bind the root identity, plan, association and purpose so unrelated dispatches cannot share a replay key.
        var scope = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            parent.ProjectId, parent.RunId, parent.SessionId, workPlanId, associationId, "maf-a2a"
        });
        // A fixed SHA-256 prefix yields the same compact identifier on every exact retry.
        return new Guid(SHA256.HashData(scope).AsSpan(0, 16));
    }

    /// <summary>
    /// Detects control characters that are not valid in a model prompt.
    /// </summary>
    internal static bool HasUnsupportedPromptControls(string prompt) =>
        prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    /// <summary>
    /// Creates a fixed-work association from the accepted role selection and a single activation revision.
    /// </summary>
    /// <param name="snapshot">The accepted workflow and plan.</param>
    /// <param name="selectionContext">The role-to-agent and model selections authorized for the run.</param>
    /// <param name="parent">The root session that owns this child.</param>
    /// <param name="step">The fixed-work step being activated.</param>
    /// <param name="acceptedSelectionHash">The digest of the accepted run selection.</param>
    /// <param name="executionFence">The active execution fence.</param>
    /// <param name="decisionStateVersion">The current accepted decision-state version.</param>
    /// <param name="activationRevision">The positive revision that identifies this activation.</param>
    /// <param name="childSessionId">The expected deterministic child-session ID.</param>
    /// <returns>The immutable association that pins the selected agent, model, provider, and run authority.</returns>
    /// <exception cref="CoordinationException">Thrown when the fixed step or role selection is missing, ambiguous, or stale.</exception>
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
        var agentId = agents.FirstOrDefault();
        var modelSelectionReference = agentId is null
            ? null
            : ModelTaskSelectionResolver.ResolveReference(
                step.ModelTaskKind ?? ModelTaskKind.Straightforward,
                fixedWork.RoleId,
                agentId,
                selectionContext.ModelTaskSelections);
        var isolationProvider = snapshot.IsolationProviderBinding?.ProviderId;
        if (agentId is null ||
            string.IsNullOrWhiteSpace(modelSelectionReference) ||
            role?.ModelSelectionReferences.IsDefault != false ||
            !role.ModelSelectionReferences.Contains(modelSelectionReference, StringComparer.Ordinal) ||
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
            agentId,
            modelSelectionReference,
            isolationProvider,
            acceptedSelectionHash.ToUpperInvariant(),
            executionFence,
            decisionStateVersion,
            childSessionId);
    }

    /// <summary>
    /// Creates the stable identifier for one fixed-work step activation.
    /// </summary>
    internal static string CreateFixedWorkAssociationId(
        SessionIdentity parent,
        string workPlanId,
        string stepId,
        long activationRevision) =>
        MafExecutionIds.CreateFixedWorkAssociationId(parent, workPlanId, stepId, activationRevision);

    /// <summary>
    /// Reconciles current child records into progress while enforcing identity, fence, and prior-result invariants.
    /// </summary>
    /// <param name="snapshot">The accepted plan whose child associations are being observed.</param>
    /// <param name="progress">The checkpointed work and fixed-work status maps.</param>
    /// <param name="parent">The root identity used to verify child-session IDs.</param>
    /// <param name="children">Current owner snapshots for registered children.</param>
    /// <param name="executionFence">The current root execution fence.</param>
    /// <param name="fixedWorkAssociations">Persisted fixed-work associations, when present.</param>
    /// <param name="pendingDispatchIds">Associations whose delivery request is not yet acknowledged.</param>
    /// <param name="joinedResultIds">Associations whose terminal result was already joined.</param>
    /// <returns>Progress updated only from admissible current child observations.</returns>
    /// <exception cref="CoordinationException">Thrown when a child identity conflicts with its association or a completed checkpoint result is not supported by the owner snapshot.</exception>
    /// <remarks>Detached children and pending dispatches are not reconciled; succeeded children must already have a joined result.</remarks>
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

    /// <summary>
    /// Builds the runnable frontier by applying dependency, executor, concurrency, and child-capacity rules.
    /// </summary>
    /// <param name="snapshot">The accepted workflow and work plan.</param>
    /// <param name="progress">The checkpointed status for work items and workflow steps.</param>
    /// <param name="registeredChildCount">The current number of child sessions registered for the plan.</param>
    /// <param name="activeChildCount">The number of children currently consuming a concurrency slot.</param>
    /// <param name="maximumChildren">The maximum child count allowed for the plan.</param>
    /// <param name="maximumConcurrentChildren">The maximum number of concurrently active children.</param>
    /// <param name="hasNonModelExecutor">Checks whether a platform step has an executor.</param>
    /// <param name="fixedWorkAssociations">The persisted fixed-work associations, when present.</param>
    /// <returns>Ready actions and any unavailable or failed dependency IDs.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when child counts exceed their configured bounds.</exception>
    /// <exception cref="ArgumentException">Thrown when checkpoint progress references unknown work or steps.</exception>
    /// <remarks>At most one non-model side effect is scheduled at a time; fixed and model work also respect available child slots.</remarks>
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
                    if (step.Cardinality.Minimum == 0)
                    {
                        stepStatuses[step.Id] = MafExecutionTaskStatus.Succeeded;
                        continue;
                    }

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

    /// <summary>Rechecks an individual child action against the locked, current checkpoint frontier.</summary>
    internal static void RequireReadyDispatchAction(
        WorkPlanSnapshot snapshot,
        MafExecutionProgress progress,
        ImmutableDictionary<string, MafExecutionFixedWorkAssociation>? fixedAssociations,
        MafExecutionAction action)
    {
        // Child slots are enforced by the owner spawn transaction. This check is only for
        // dependency outcomes, fixed-work activation and normalized output-conflict edges.
        var frontier = BuildFrontier(
            snapshot, progress, 0, 0, int.MaxValue, int.MaxValue,
            _ => false, fixedAssociations);
        // Missing actions include stale dependencies and serialized output conflicts; never commit them from an old frontier.
        if (!frontier.ReadyActions.Any(candidate =>
                candidate.Step.Id == action.Step.Id &&
                candidate.WorkItem?.Id == action.WorkItem?.Id))
            throw new CoordinationException(
                "maf_execution_frontier_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Aggregates item or association statuses into the status of their workflow step.
    /// </summary>
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

    /// <summary>
    /// Applies failure-first aggregation, then success, running, and pending precedence to a status group.
    /// </summary>
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

    /// <summary>
    /// Rejects progress entries that do not refer to a plan item, platform step, or fixed-work association.
    /// </summary>
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

    /// <summary>
    /// Maps a child snapshot to running only when it has the current fence and remains active.
    /// </summary>
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

    /// <summary>
    /// Applies an admissible child observation without allowing terminal progress to move backwards.
    /// </summary>
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

    /// <summary>
    /// Identifies failed and indeterminate dependencies that prevent a dependent action from running.
    /// </summary>
    private static bool IsTerminalFailure(MafExecutionTaskStatus status) =>
        status is MafExecutionTaskStatus.Failed or MafExecutionTaskStatus.Indeterminate;
}
