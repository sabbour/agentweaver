using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Serves versioned runtime execution observations assembled from current owner, selection, decision, and checkpoint state.
/// The newer response also includes checkpoint-parent metadata and runtime evidence sections.
/// </summary>
public static partial class CoordinationEndpoints
{
    /// <summary>
    /// Reads the original runtime execution observation contract.
    /// </summary>
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
        ReadRuntimeExecutionObservationCoreAsync(
            projectId, runId, sessionId, context, options, projects, sessions, decisions,
            selectionContexts, checkpoints, includeCheckpointParent: false, cancellationToken);

    /// <summary>
    /// Reads the version-two execution observation, including checkpoint-parent metadata.
    /// </summary>
    private static Task<IResult> ReadRuntimeExecutionObservationV2Async(
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
        ReadRuntimeExecutionObservationCoreAsync(
            projectId, runId, sessionId, context, options, projects, sessions, decisions,
            selectionContexts, checkpoints, includeCheckpointParent: true, cancellationToken);

    /// <summary>
    /// Reads the version-one aggregate runtime observation and maps owner authorization failures to HTTP responses.
    /// </summary>
    private static async Task<IResult> ReadRuntimeObservationV1Async(
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
        RuntimeRegistrationOwner registrations,
        RuntimeUsageSourceStore evidenceStore,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await ReadRuntimeExecutionObservationCoreAsync(
                projectId, runId, sessionId, context, options, projects, sessions, decisions,
                selectionContexts, checkpoints, includeCheckpointParent: true, cancellationToken,
                registrations, evidenceStore).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException exception)
        {
            var statusCode = exception.Code.EndsWith("_conflict", StringComparison.Ordinal) ||
                    exception.Code.EndsWith("_stale", StringComparison.Ordinal) ||
                    exception.Code is "runtime_registration_unknown" or "runtime_registration_invalid"
                        ? StatusCodes.Status409Conflict
                    : StatusCodes.Status403Forbidden;
            return Results.Json(new { error = exception.Code }, statusCode: statusCode);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_observation_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_observation_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Builds an execution observation from the current accepted selection, runtime owner, decision, and checkpoint.
    /// </summary>
    /// <param name="includeCheckpointParent">Whether to emit version-two checkpoint-parent metadata.</param>
    /// <param name="registrations">Optional registration owner; required together with <paramref name="evidenceStore"/> for the aggregate response.</param>
    /// <param name="evidenceStore">Optional evidence store paired with <paramref name="registrations"/>.</param>
    /// <exception cref="CoordinationException">Thrown when any owner state, selection, checkpoint, or association is stale or invalid.</exception>
    /// <remarks>All participating authority is reread before returning so changes during construction invalidate the observation.</remarks>
    private static Task<IResult> ReadRuntimeExecutionObservationCoreAsync(
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
        bool includeCheckpointParent,
        CancellationToken cancellationToken,
        RuntimeRegistrationOwner? registrations = null,
        RuntimeUsageSourceStore? evidenceStore = null) =>
        ExecuteAsync(async () =>
        {
            if ((registrations is null) != (evidenceStore is null))
                throw new InvalidOperationException("Runtime observation evidence owners must be configured together.");
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
            RequireBoundObservationCheckpoint(
                checkpoint,
                root.SessionId,
                decision.State.ConfirmedWorkPlan?.Plan.Id);

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

            RuntimeExecutionObservationV2? executionV2 = null;
            IResult observation;
            if (includeCheckpointParent)
            {
                executionV2 = RuntimeExecutionObservationContract.CreateV2(
                    owner.RootSessionId,
                    sessionId,
                    owner.ExecutionFence,
                    decision.StateVersion,
                    checkpoint,
                    associationId,
                    status,
                    pendingIntent,
                    result);
                observation = Results.Ok(executionV2);
            }
            else
            {
                observation = Results.Ok(RuntimeExecutionObservationContract.Create(
                    owner.RootSessionId,
                    sessionId,
                    owner.ExecutionFence,
                    decision.StateVersion,
                    checkpoint,
                    associationId,
                    status,
                    pendingIntent,
                    result));
            }

            RuntimeRegistration? registration = null;
            if (registrations is not null && evidenceStore is not null)
            {
                registration = await registrations.ReadCurrentForSessionAsync(
                    context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
                RequireCurrentRuntimeRegistrationForObservation(
                    registration, actor, identity, owner);
                var evidence = await evidenceStore.ReadObservationEvidenceAsync(
                    registration, owner.RootSessionId, cancellationToken).ConfigureAwait(false);
                observation = Results.Ok(new RuntimeObservationV1(
                    1,
                    new(RuntimeObservationSectionState.Available,
                        new RuntimeObservationRegistration(
                            registration.RuntimeInstanceId,
                            registration.Revision,
                            registration.State switch
                            {
                                RuntimeRegistrationState.Active => RuntimeObservationRegistrationState.Active,
                                RuntimeRegistrationState.Revoked => RuntimeObservationRegistrationState.Revoked,
                                _ => throw new ArgumentOutOfRangeException(nameof(registration.State))
                            },
                            registration.ExpiresAt)),
                    new(RuntimeObservationSectionState.Available,
                        RuntimeObservationExecutionV2.From(executionV2 ?? throw new InvalidOperationException(
                            "The runtime observation requires checkpoint-parent evidence."))),
                    evidence.CreditGuard is null
                        ? new(RuntimeObservationSectionState.Unknown, null)
                        : new(RuntimeObservationSectionState.Available, evidence.CreditGuard),
                    new(RuntimeObservationSectionState.Available, evidence.Dispatches),
                    new(RuntimeObservationSectionState.Available, evidence.ToolReservations))
                    .Validate());
            }

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var currentOwner = await sessions.ReadRuntimeOwnerStateForObservationAsync(
                actor, identity, cancellationToken).ConfigureAwait(false);
            var currentDecision = await decisions.ReadCurrentAsync(
                actor, root, selection, cancellationToken).ConfigureAwait(false);
            var currentOwnerContext = await ReadRuntimeOwnerContextCoreAsync(
                projectId, runId, sessionId, context, options, projects, sessions, decisions,
                selectionContexts, checkpoints, cancellationToken, allowTerminalChild: true).ConfigureAwait(false);
            var currentRegistration = registrations is null
                ? null
                : await registrations.ReadCurrentForSessionAsync(
                    context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            if (currentOwner != owner || currentDecision.StateVersion != decision.StateVersion ||
                currentDecision.SelectionHash != decision.SelectionHash ||
                currentDecision.State.Fence != decision.State.Fence ||
                currentDecision.State.ConfirmedWorkPlan?.Plan.Id != checkpoint.State.WorkPlanId ||
                currentOwnerContext != ownerContext ||
                currentRegistration != registration)
                throw new CoordinationException(
                    "runtime_owner_context_stale", StatusCodes.Status409Conflict);
            return observation;
        }, cancellationToken);

    /// <summary>
    /// Requires the active registration to match the authenticated actor and current session owner binding.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the registration is inactive or bound to stale owner state.</exception>
    internal static void RequireCurrentRuntimeRegistrationForObservation(
        RuntimeRegistration registration,
        CoordinationActor actor,
        SessionIdentity identity,
        SessionRuntimeOwnerState owner)
    {
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.ActorIssuer != actor.Issuer ||
            binding.ActorId != actor.Subject ||
            binding.TenantId != owner.TenantId ||
            binding.ProjectId != identity.ProjectId ||
            binding.RunId != identity.RunId ||
            binding.SessionId != identity.SessionId ||
            binding.ExecutionFence != owner.ExecutionFence ||
            !string.Equals(binding.AcceptedSelectionHash, owner.AcceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase))
            throw new CoordinationException(
                "runtime_owner_context_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Requires the observation's owner context and current decision to describe the same session, turn, fence, and selection.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when context, owner, or decision versions no longer agree.</exception>
    internal static void RequireCurrentObservationOwner(
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

    /// <summary>
    /// Requires the checkpoint to belong to the expected root session and confirmed work plan.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when checkpoint ownership or work-plan binding is stale.</exception>
    internal static void RequireBoundObservationCheckpoint(
        MafExecutionCheckpointSnapshot checkpoint,
        string rootSessionId,
        string? expectedWorkPlanId)
    {
        if (checkpoint.Info.SessionId != rootSessionId ||
            expectedWorkPlanId is null ||
            checkpoint.State.WorkPlanId != expectedWorkPlanId)
            throw new CoordinationException(
                "maf_execution_checkpoint_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Resolves the child association and task status from the current checkpoint.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when neither the model-work nor fixed-work binding matches current state.</exception>
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

/// <summary>
/// Creates the validated public contracts for runtime execution observations.
/// </summary>
internal static class RuntimeExecutionObservationContract
{
    /// <summary>
    /// Creates a version-one observation with dispatch intent and result digest evidence.
    /// </summary>
    /// <param name="checkpoint">The current checkpoint containing task status and optional result text.</param>
    /// <returns>A validated observation; result content itself is not included, only its SHA-256 digest and byte length.</returns>
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

    /// <summary>
    /// Creates a version-two observation and attaches validated checkpoint-parent metadata.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when checkpoint parent metadata is malformed.</exception>
    internal static RuntimeExecutionObservationV2 CreateV2(
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
        var observation = Create(
            rootSessionId,
            sessionId,
            executionFence,
            currentDecisionStateVersion,
            checkpoint,
            associationId,
            status,
            pendingIntent,
            result);
        RuntimeExecutionCheckpointParent parent;
        try
        {
            parent = new RuntimeExecutionCheckpointParent(
                checkpoint.HasParentMetadata,
                checkpoint.ParentCheckpointId).Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "maf_execution_checkpoint_parent_metadata_invalid",
                StatusCodes.Status409Conflict,
                exception);
        }
        return new RuntimeExecutionObservationV2(
            2,
            observation,
            parent)
            .Validate();
    }

    /// <summary>
    /// Maps internal checkpoint task states to the stable runtime observation contract.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unknown internal task status.</exception>
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
