using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    private static Task<IResult> ReadRuntimeExecutionObservationAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore sessions,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore selectionContexts,
        PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var owner = await sessions.ReadRuntimeOwnerStateForObservationAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            var root = new SessionIdentity(projectId, runId, owner.RootSessionId);
            var decision = await decisions.ReadCurrentAsync(
                actor, root, selection, cancellationToken).ConfigureAwait(false);
            var ownerContext = await ReadRuntimeOwnerContextCoreAsync(
                projectId, runId, sessionId, context, options, projects, sessions, decisions,
                selectionContexts, checkpoints, cancellationToken, allowTerminalChild: true).ConfigureAwait(false);
            RequireCurrentObservationOwner(ownerContext, actor, identity, owner, decision);

            var execution = new MafExecutionCheckpointStore(
                checkpoints,
                CreateExecutionCheckpointBinding(
                    root, actor, owner.ExecutionFence, MafExecutionCheckpointStore.CurrentSdkVersion,
                    PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot)));
            var checkpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "maf_execution_checkpoint_unavailable", StatusCodes.Status409Conflict);
            if (checkpoint.Info.SessionId != root.SessionId ||
                checkpoint.State.WorkPlanId != decision.State.ConfirmedWorkPlan?.Plan.Id)
                throw new CoordinationException(
                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);

            var (associationId, status) = ResolveCurrentAssociation(
                owner, identity, root, decision, checkpoint.State);
            checkpoint.State.PendingDispatches.TryGetValue(associationId, out var pendingIntent);
            if (pendingIntent is not null && pendingIntent.ChildSessionId != sessionId)
                throw new CoordinationException(
                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
            checkpoint.State.Results.TryGetValue(associationId, out var result);
            if (pendingIntent is not null && result is not null)
                throw new CoordinationException(
                    "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);

            var observation = RuntimeExecutionObservationContract.Create(
                owner.RootSessionId,
                sessionId,
                owner.ExecutionFence,
                decision.StateVersion,
                checkpoint,
                associationId,
                status,
                pendingIntent,
                result);

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var currentOwner = await sessions.ReadRuntimeOwnerStateForObservationAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            var currentDecision = await decisions.ReadCurrentAsync(
                actor, root, selection, cancellationToken).ConfigureAwait(false);
            var currentOwnerContext = await ReadRuntimeOwnerContextCoreAsync(
                projectId, runId, sessionId, context, options, projects, sessions, decisions,
                selectionContexts, checkpoints, cancellationToken, allowTerminalChild: true).ConfigureAwait(false);
            if (currentOwner != owner || currentDecision.StateVersion != decision.StateVersion ||
                currentDecision.SelectionHash != decision.SelectionHash ||
                currentDecision.State.Fence != decision.State.Fence ||
                currentDecision.State.ConfirmedWorkPlan?.Plan.Id != checkpoint.State.WorkPlanId ||
                currentOwnerContext != ownerContext)
                throw new CoordinationException(
                    "runtime_owner_context_stale", StatusCodes.Status409Conflict);
            return Results.Ok(observation);
        }, cancellationToken);

    private static void RequireCurrentObservationOwner(
        RuntimeOwnerContext ownerContext,
        CoordinationActor actor,
        SessionIdentity identity,
        SessionRuntimeOwnerState owner,
        CoordinatorDecisionCurrentState decision)
    {
        if (ownerContext.ContractVersion != 1 ||
            ownerContext.ActorIssuer != actor.Issuer ||
            ownerContext.ActorId != actor.Subject ||
            ownerContext.ProjectId != identity.ProjectId ||
            ownerContext.RunId != identity.RunId ||
            ownerContext.SessionId != identity.SessionId ||
            ownerContext.TenantId != owner.TenantId ||
            ownerContext.TurnId != owner.RuntimeTurnId ||
            ownerContext.ExecutionFence != owner.ExecutionFence ||
            ownerContext.LogicalTurnOrdinal != owner.LogicalTurnOrdinal ||
            ownerContext.OwnerStateVersion != owner.StateVersion ||
            !string.Equals(
                ownerContext.AcceptedSelectionHash, owner.AcceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase) ||
            decision.State.Fence != owner.ExecutionFence ||
            decision.SelectionHash != owner.AcceptedSelectionHash ||
            decision.StateVersion != ownerContext.DecisionStateVersion ||
            decision.State.ConfirmedWorkPlan is null)
            throw new CoordinationException(
                "runtime_owner_context_stale", StatusCodes.Status409Conflict);
    }

    private static (string AssociationId, MafExecutionTaskStatus Status) ResolveCurrentAssociation(
        SessionRuntimeOwnerState owner,
        SessionIdentity identity,
        SessionIdentity root,
        CoordinatorDecisionCurrentState decision,
        MafExecutionCheckpoint state)
    {
        if (owner.WorkPlanItemId is { } workPlanItemId)
        {
            if (!decision.State.ConfirmedWorkPlan!.Plan.Items.Any(item =>
                    item.Id == workPlanItemId) ||
                MafExecutionPlanner.CreateChildSessionId(root, state.WorkPlanId, workPlanItemId) !=
                    identity.SessionId ||
                !state.Progress.WorkItems.TryGetValue(workPlanItemId, out var status))
                throw new CoordinationException(
                    "runtime_owner_context_stale", StatusCodes.Status409Conflict);
            return (workPlanItemId, status);
        }

        var fixedAssociation = state.FixedWorkAssociations.Values.SingleOrDefault(association =>
            association.ChildSessionId == identity.SessionId);
        if (fixedAssociation is null ||
            fixedAssociation.WorkPlanId != state.WorkPlanId ||
            fixedAssociation.AcceptedSelectionHash != owner.AcceptedSelectionHash ||
            fixedAssociation.ExecutionFence != owner.ExecutionFence ||
            fixedAssociation.DecisionStateVersion != decision.StateVersion ||
            !state.Progress.FixedWorkItems.TryGetValue(
                fixedAssociation.AssociationId, out var fixedStatus))
            throw new CoordinationException(
                "runtime_owner_context_stale", StatusCodes.Status409Conflict);
        return (fixedAssociation.AssociationId, fixedStatus);
    }
}

internal static class RuntimeExecutionObservationContract
{
    internal static RuntimeExecutionObservation Create(
        string rootSessionId,
        string sessionId,
        long executionFence,
        long currentDecisionStateVersion,
        MafExecutionCheckpointSnapshot checkpoint,
        string associationId,
        MafExecutionTaskStatus status,
        MafExecutionDispatchIntent? pendingIntent,
        string? result)
    {
        var resultBytes = result is null ? null : Encoding.UTF8.GetBytes(result);
        return new RuntimeExecutionObservation(
            1,
            rootSessionId,
            sessionId,
            executionFence,
            checkpoint.State.WorkPlanId,
            associationId,
            checkpoint.State.Revision,
            checkpoint.Info.CheckpointId,
            checkpoint.State.DecisionStateVersion,
            currentDecisionStateVersion,
            ToContractStatus(status))
        {
            PendingDispatch = pendingIntent is null
                ? null
                : new RuntimeExecutionPendingDispatch(
                    pendingIntent.MessageId, pendingIntent.PromptHash.ToLowerInvariant()),
            ResultDigest = resultBytes is null
                ? null
                : new RuntimeExecutionResultDigest(
                    Convert.ToHexStringLower(SHA256.HashData(resultBytes)),
                    resultBytes.LongLength)
        }.Validate();
    }

    private static RuntimeExecutionTaskStatus ToContractStatus(MafExecutionTaskStatus status) =>
        status switch
        {
            MafExecutionTaskStatus.Pending => RuntimeExecutionTaskStatus.Pending,
            MafExecutionTaskStatus.Running => RuntimeExecutionTaskStatus.Running,
            MafExecutionTaskStatus.Succeeded => RuntimeExecutionTaskStatus.Succeeded,
            MafExecutionTaskStatus.Failed => RuntimeExecutionTaskStatus.Failed,
            MafExecutionTaskStatus.Indeterminate => RuntimeExecutionTaskStatus.Indeterminate,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}
