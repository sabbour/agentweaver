using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    private static Task<IResult> ReadCoordinatorBuildTestCommandAsync(
        string projectId,
        string runId,
        string sessionId,
        string checkpointId,
        string stepId,
        string workPlanId,
        long checkpointRevision,
        long decisionStateVersion,
        long executionFence,
        string acceptedSelectionHash,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinatorDecisionOwnerStore decisions,
        PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var reference = new SandboxBuildTestCheckpointReference(
                projectId, runId, sessionId, checkpointId, workPlanId, stepId,
                checkpointRevision, decisionStateVersion, executionFence, acceptedSelectionHash).Validate();
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var binding = CreateExecutionCheckpointBinding(
                identity, actor, current.State.Fence, MafExecutionCheckpointStore.CurrentSdkVersion,
                PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot));
            var latest = await new MafExecutionCheckpointStore(checkpoints, binding)
                .ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            return Results.Ok(ResolveBuildTestCommand(
                latest, current, reference, selection.Authorization.TenantId));
        }, cancellationToken);

    internal static SandboxBuildTestAcceptedCommand ResolveBuildTestCommand(
        MafExecutionCheckpointSnapshot latest,
        CoordinatorDecisionCurrentState current,
        SandboxBuildTestCheckpointReference reference,
        string tenantId)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(current);
        _ = reference.Validate();
        MafExecutionCheckpointContract.ValidateState(latest.State);
        if (!current.State.CanDispatch ||
            current.State.ConfirmedWorkPlan is not { } plan ||
            current.StateVersion != reference.DecisionStateVersion ||
            current.State.Fence != reference.ExecutionFence ||
            current.SelectionHash != reference.AcceptedSelectionHash ||
            latest.Info.SessionId != reference.SessionId ||
            latest.State.WorkPlanId != reference.WorkPlanId ||
            latest.State.DecisionStateVersion != reference.DecisionStateVersion ||
            plan.Plan.Id != reference.WorkPlanId ||
            !latest.State.BuildTestIntents.TryGetValue(reference.StepId, out var intent) ||
            intent.CheckpointReference() != reference ||
            intent.ExpectedBinding.Fence.Owner.TenantId != tenantId)
            throw new CoordinationException(
                "maf_execution_build_test_checkpoint_stale", StatusCodes.Status409Conflict);

        var step = plan.Workflow.Definition.Steps.SingleOrDefault(candidate => candidate.Id == reference.StepId);
        if (step is null || !MafBuildTestCommandContract.IsExecutable(step) ||
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(step.BuildTestCommand),
                JsonSerializer.SerializeToElement(intent.Command)) ||
            !MafBuildTestCommandContract.MatchesProvider(
                plan.IsolationProviderBinding, intent.ExpectedBinding, reference.RunId))
            throw new CoordinationException(
                "maf_execution_build_test_binding_stale", StatusCodes.Status409Conflict);
        return intent.ToAcceptedCommand();
    }
}
