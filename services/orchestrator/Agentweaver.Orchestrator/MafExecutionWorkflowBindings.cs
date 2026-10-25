using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Registers only dispatch paths backed by the current MAF owner implementation.</summary>
internal static class MafExecutionWorkflowBindings
{
    private sealed class FixedChildExecutor : IFixedWorkflowStepExecutor;
    private sealed class OpenChildExecutor : IOpenWorkflowStepExecutor;
    private sealed class BuildTestExecutor : IPlatformWorkflowStepExecutor
    {
        public WorkflowPlatformGate Gate => WorkflowPlatformGate.BuildTest;
    }

    /// <summary>Binds the accepted catalog before any child is spawned or platform intent is prepared.</summary>
    internal static BoundWorkflowDefinition RequireBound(
        WorkPlanSnapshot plan, bool hasBuildTestOwner)
    {
        // Do not impersonate an unsupported platform gate with a model child or a no-op executor.
        var registrations = new List<KeyValuePair<WorkflowStepExecutorType, IWorkflowStepExecutor>>
        {
            new(WorkflowStepExecutorType.Fixed, new FixedChildExecutor()),
            new(WorkflowStepExecutorType.Open, new OpenChildExecutor())
        };
        if (hasBuildTestOwner &&
            plan.Workflow.Definition.Steps.Any(step =>
                step.PlatformGate == WorkflowPlatformGate.BuildTest &&
                MafBuildTestCommandContract.IsExecutable(step)))
            registrations.Add(new(
                WorkflowStepExecutorType.ForPlatformGate(WorkflowPlatformGate.BuildTest),
                new BuildTestExecutor()));
        try
        {
            // Bind the accepted snapshot once so dispatch cannot resolve a different executor after confirmation.
            return WorkflowDefinitionExecutorBinder.Bind(
                plan.Workflow, new WorkflowStepExecutorRegistry(registrations));
        }
        catch (WorkflowExecutorBindingException exception)
        {
            // Convert binder detail into the endpoint's stable unavailable-executor response.
            throw new CoordinationException(
                "maf_execution_executor_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    /// <summary>Rejects a ready action that no longer belongs to its bound fixed/open/BuildTest step.</summary>
    internal static void RequireBoundAction(BoundWorkflowDefinition bound, MafExecutionAction action)
    {
        // Match both the step identifier and full definition so an edited same-ID step cannot reuse the old binding.
        var step = bound.Steps.SingleOrDefault(candidate =>
            candidate.Definition.Id == action.Step.Id &&
            candidate.Definition == action.Step);
        // Open actions require an item; fixed and platform actions must not carry a model-created item.
        if (step is null ||
            action.WorkItem is not null && step is not BoundWorkflowStep.Open ||
            action.WorkItem is null && step is BoundWorkflowStep.Open)
            throw new CoordinationException(
                "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>Creates a deterministic policy intent only for an exact confirmed open-plan item.</summary>
    internal static MafChildSpawnIntent CreateOpenChildSpawnIntent(
        WorkPlanSnapshot plan,
        SessionIdentity root,
        MafExecutionAction action,
        string contextRevision,
        long planRevision,
        long executionFence)
    {
        // Fixed specifications are not WorkPlanItems; they use the fixed-work intent variant below.
        if (action.Step.Mode == WorkflowStepMode.Fixed)
            throw new CoordinationException(
                "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
        // Platform actions use platform executors and must never be converted into child-spawn intents.
        if (action.Step.Mode != WorkflowStepMode.Open || action.WorkItem is not { } workItem)
            throw new CoordinationException(
                "maf_execution_executor_unavailable", StatusCodes.Status409Conflict);
        // The policy contract must authorize the exact item in the accepted plan, never a caller-built substitute.
        if (!plan.Workflow.Definition.Steps.Contains(action.Step) ||
            !plan.Plan.Items.Contains(workItem) ||
            workItem.WorkflowStepId != action.Step.Id)
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict);

        var intent = new MafChildSpawnIntent(
            MafExecutionPlanner.CreateDispatchMessageId(root, plan.Plan.Id, workItem.Id),
            root.SessionId,
            root.SessionId,
            plan.Plan.Id,
            planRevision,
            action.Step.Id,
            workItem.Id,
            contextRevision,
            executionFence,
            workItem);
        try
        {
            // Validate the full canonical payload before it can reach policy evaluation or durable storage.
            MafChildSpawnAuthorizationContract.ValidateIntent(intent);
        }
        catch (ArgumentException exception)
        {
            // Invalid canonical intent data is a stale accepted-plan request, not a retryable policy outage.
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict, exception);
        }
        return intent;
    }

    /// <summary>Creates a deterministic policy intent bound to a confirmed fixed step and selected role.</summary>
    internal static MafChildSpawnFixedWorkIntent CreateFixedChildSpawnIntent(
        WorkPlanSnapshot plan,
        WorkPlanRunSelectionContext selectionContext,
        SessionIdentity root,
        MafExecutionAction action,
        MafExecutionFixedWorkAssociation association,
        string acceptedSelectionHash,
        string contextRevision,
        long planRevision,
        long executionFence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectionContext);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(association);
        var step = action.Step;
        if (action.WorkItem is not null ||
            step.Mode != WorkflowStepMode.Fixed ||
            step.FixedWork is not { } fixedWork ||
            !plan.Workflow.Definition.Steps.Contains(step) ||
            selectionContext.Roles.IsDefault ||
            selectionContext.ModelTaskSelections.IsDefault ||
            association.WorkPlanId != plan.Plan.Id ||
            association.WorkflowId != plan.Workflow.Definition.Id ||
            association.DefinitionRevision != plan.Workflow.Definition.Revision ||
            association.StepId != step.Id ||
            association.ActivationRevision < 1 ||
            association.Specification != fixedWork ||
            association.DecisionStateVersion != planRevision ||
            association.ExecutionFence != executionFence ||
            association.AssociationId != MafExecutionPlanner.CreateFixedWorkAssociationId(
                root, plan.Plan.Id, step.Id, association.ActivationRevision) ||
            association.ChildSessionId != MafExecutionPlanner.CreateChildSessionId(
                root, plan.Plan.Id, association.AssociationId) ||
            !string.Equals(
                association.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict);

        // Re-derive the unique role binding from the accepted selection rather than trusting a stored association.
        var roles = selectionContext.Roles
            .Where(candidate => string.Equals(candidate.RoleId, fixedWork.RoleId, StringComparison.Ordinal))
            .ToArray();
        var role = roles.Length == 1 ? roles[0] : null;
        if (role is null ||
            role.EligibleAgentIds.IsDefaultOrEmpty ||
            role.ModelSelectionReferences.IsDefault ||
            role.EligibleIsolationChoices.IsDefault ||
            !role.EligibleIsolationChoices.Contains(fixedWork.IsolationChoice, StringComparer.Ordinal))
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict);

        // Match the owner validator's ordinal agent choice and model-task resolution exactly.
        var agentId = role.EligibleAgentIds.Order(StringComparer.Ordinal).First();
        var modelReference = ModelTaskSelectionResolver.ResolveReference(
            step.ModelTaskKind ?? ModelTaskKind.Straightforward,
            fixedWork.RoleId,
            agentId,
            selectionContext.ModelTaskSelections);
        var isolation = selectionContext.IsolationProviderBinding;
        if (isolation is null ||
            isolation.RunId != root.RunId ||
            isolation.Seam != ProviderSeam.Sandbox ||
            isolation.NegotiatedCapabilities is null ||
            step.RequiredProviderCapabilities.IsDefault ||
            !step.RequiredProviderCapabilities.IsDefaultOrEmpty &&
                !step.RequiredProviderCapabilities.All(isolation.NegotiatedCapabilities.Contains) ||
            string.IsNullOrWhiteSpace(modelReference) ||
            !role.ModelSelectionReferences.Contains(modelReference, StringComparer.Ordinal) ||
            !string.Equals(association.AgentId, agentId, StringComparison.Ordinal) ||
            !string.Equals(association.ModelSelectionReference, modelReference, StringComparison.Ordinal) ||
            !string.Equals(association.IsolationProviderId, isolation.ProviderId, StringComparison.Ordinal))
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict);

        var intent = new MafChildSpawnFixedWorkIntent(
            MafExecutionPlanner.CreateDispatchMessageId(root, plan.Plan.Id, association.AssociationId),
            root.SessionId,
            root.SessionId,
            plan.Plan.Id,
            planRevision,
            step.Id,
            contextRevision,
            executionFence,
            plan.Workflow.Definition.Id,
            plan.Workflow.Definition.Revision,
            MafChildSpawnAuthorizationContract.ComputeFixedWorkSpecificationHash(fixedWork),
            new MafChildSpawnRoleBinding(
                fixedWork.RoleId,
                agentId,
                modelReference,
                isolation.ProviderId,
                fixedWork.IsolationChoice));
        try
        {
            // Validate the complete fixed-work payload before policy evaluation or durable receipt lookup.
            MafChildSpawnAuthorizationContract.ValidateIntent(intent);
        }
        catch (ArgumentException exception)
        {
            // Invalid confirmed data is stale authority, never an invitation to issue a weaker intent.
            throw new CoordinationException(
                "maf_child_spawn_plan_stale", StatusCodes.Status409Conflict, exception);
        }
        return intent;
    }
}
