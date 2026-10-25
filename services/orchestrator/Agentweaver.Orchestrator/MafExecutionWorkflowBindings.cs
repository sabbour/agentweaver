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
        // Fixed specifications are not WorkPlanItems; refuse them until their intent variant can be authorized.
        if (action.Step.Mode == WorkflowStepMode.Fixed)
            throw new CoordinationException(
                "maf_child_spawn_fixed_authorization_unavailable", StatusCodes.Status503ServiceUnavailable);
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
}
