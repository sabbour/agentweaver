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
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ReadCoordinatorBuildTestCommandCoreAsync(
            projectId, runId, sessionId, checkpointId, stepId, workPlanId,
            checkpointRevision, decisionStateVersion, executionFence, acceptedSelectionHash,
            context, options, projects, decisions, checkpoints, sourceControlOwner, false, cancellationToken);

    private static Task<IResult> ReadCoordinatorBuildTestSeedAsync(
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
        SourceControlOwnerStore sourceControlOwner,
        CancellationToken cancellationToken) =>
        ReadCoordinatorBuildTestCommandCoreAsync(
            projectId, runId, sessionId, checkpointId, stepId, workPlanId,
            checkpointRevision, decisionStateVersion, executionFence, acceptedSelectionHash,
            context, options, projects, decisions, checkpoints, sourceControlOwner, true, cancellationToken);

    private static Task<IResult> ReadCoordinatorBuildTestCommandCoreAsync(
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
        SourceControlOwnerStore sourceControlOwner,
        bool includeSeed,
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
            var store = new MafExecutionCheckpointStore(checkpoints, binding);
            var latest = await store
                .ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
            var command = ResolveBuildTestCommand(
                latest, current, reference, selection.Authorization.TenantId);
            if (command.WorkspaceSource is { } initialSource)
                await SourceControlEndpoints.RequireCurrentBuildTestInputAsync(
                    context, initialSource, reference, options, projects, decisions, sourceControlOwner,
                    cancellationToken).ConfigureAwait(false);
            if (includeSeed)
            {
                if (command.WorkspaceSource is null)
                    throw new CoordinationException(
                        "maf_execution_workspace_seed_unavailable", StatusCodes.Status404NotFound);
                var hydrated = await store.HydrateBuildTestIntentAsync(
                    latest.State.BuildTestIntents[reference.StepId], cancellationToken).ConfigureAwait(false);
                command = hydrated.ToAcceptedCommand();
            }
            latest = await store.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            current = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            var currentCommand = ResolveBuildTestCommand(
                latest, current, reference, selection.Authorization.TenantId);
            if (currentCommand.ImmutableHash != command.ImmutableHash)
                throw new CoordinationException(
                    "maf_execution_build_test_checkpoint_stale", StatusCodes.Status409Conflict);
            if (currentCommand.WorkspaceSource is { } source)
                await SourceControlEndpoints.RequireCurrentBuildTestInputAsync(
                    context, source, reference, options, projects, decisions, sourceControlOwner,
                    cancellationToken).ConfigureAwait(false);
            if (includeSeed)
                return Results.Bytes(command.WorkspaceSeed!.Value.ToArray(), "application/octet-stream");
            return Results.Ok(command);
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
        return (intent with { WorkspaceSeed = null }).ToAcceptedCommand(requireSeed: false);
    }
}
