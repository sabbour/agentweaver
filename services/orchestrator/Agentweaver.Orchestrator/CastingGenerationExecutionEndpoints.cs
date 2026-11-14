using System.Net.Http;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>Executes proposal generation through the AgentHost of an accepted project run.</summary>
public static partial class CoordinationEndpoints
{
    /// <summary>Maps the run-bound casting generation route.</summary>
    public static IEndpointRouteBuilder MapCastingGenerationExecutionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/casting-generation",
            ExecuteCastingGenerationAsync)
            .RequireAuthorization()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        return endpoints;
    }

    private static Task<IResult> ExecuteCastingGenerationAsync(
        string projectId,
        string runId,
        string sessionId,
        ExecuteCastingGenerationRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore sessions,
        EventsAddressedMessageClient events,
        CoordinatorDecisionOwnerStore decisions,
        RuntimeRegistrationOwner registrations,
        ExecutableActionGrantOwnerStore actionGrants,
        CoordinationRuntimeRunStartStore starts,
        IHttpClientFactory clients,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                throw new CoordinationException(
                    "casting_generation_request_invalid", StatusCodes.Status400BadRequest);
            request.Validate();
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);

            // The saved first message is the generation prompt, so accept and start the run without
            // admitting a second Events message before the direct AgentHost generation turn.
            await sessions.StartProjectRunAsync(
                actor,
                selection.Authorization.TenantId,
                projectId,
                runId,
                async (runRequest, token) =>
                {
                    await AcceptInteractiveRootAsync(
                        projectId,
                        runId,
                        runRequest.RootSessionId,
                        context,
                        options,
                        projects,
                        events,
                        sessions,
                        decisions,
                        token).ConfigureAwait(false);
                },
                token => RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, token),
                cancellationToken,
                validateRequest: runRequest =>
                    CastingGenerationExecutionContract.RequireProjectRunStart(
                        runRequest, runId, sessionId, request.Prompt)).ConfigureAwait(false);

            async Task<SessionStatusSnapshot> ReadCurrentAcceptedRootAsync(CancellationToken token)
            {
                // Read tree and status together so only the active accepted root can drive generation.
                var currentTree = await sessions.ReadSessionTreeAsync(actor, identity, token)
                    .ConfigureAwait(false);
                var currentStatus = await sessions.ReadSessionStatusAsync(actor, identity, token)
                    .ConfigureAwait(false);
                CastingGenerationExecutionContract.RequireAcceptedRoot(
                    projectId, runId, sessionId, currentTree, currentStatus);
                return currentStatus;
            }

            var status = await ReadCurrentAcceptedRootAsync(cancellationToken).ConfigureAwait(false);
            // Pin the turn to the accepted snapshot before contacting the registered host.
            var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var registration = await registrations.ReadCurrentForSessionAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            CastingGenerationExecutionContract.RequireCurrentRegistration(
                registration, projectId, runId, sessionId, status.ExecutionFence, selectionHash, timeProvider);
            var promptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(request.Prompt));
            var storedCompletion = await actionGrants.ReadRootTurnCompletionAsync(
                context, registration, request.RequestId, promptHash, cancellationToken).ConfigureAwait(false);
            // This request and prompt already completed on the current registration. Return the stored
            // Events material and do not call the host again. Authority and registration are checked again first.
            if (storedCompletion is not null &&
                storedCompletion.Completion.Registration == registration)
            {
                var replayActor = await CreateRuntimeActorAsync(context, actor, timeProvider)
                    .ConfigureAwait(false);
                try
                {
                    var user = await events.ReadSessionMaterialAsync(
                        context, identity, storedCompletion.Completion.UserMaterial.EventId,
                        SessionMaterialKind.TurnContent, replayActor, cancellationToken).ConfigureAwait(false);
                    var assistant = await events.ReadSessionMaterialAsync(
                        context, identity, storedCompletion.Completion.AssistantMaterial.EventId,
                        SessionMaterialKind.TurnContent, replayActor, cancellationToken).ConfigureAwait(false);
                    var expectedPrompt = Encoding.UTF8.GetBytes(request.Prompt);
                    if (user.Material != storedCompletion.Completion.UserMaterial ||
                        assistant.Material != storedCompletion.Completion.AssistantMaterial ||
                        !user.Bytes.AsSpan().SequenceEqual(expectedPrompt) ||
                        RuntimeContractValidation.Hash(assistant.Bytes) !=
                            storedCompletion.Completion.NativeTurn.OutputHash)
                        throw new CoordinationException(
                            "casting_generation_material_receipt_invalid",
                            StatusCodes.Status503ServiceUnavailable);
                    var replayOutput = Encoding.UTF8.GetString(assistant.Bytes);
                    var replayStatus = await ReadCurrentAcceptedRootAsync(cancellationToken).ConfigureAwait(false);
                    await RequireUnchangedAuthorizedSelectionAsync(
                        context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                    var replayRegistration = await registrations.ReadCurrentForSessionAsync(
                        context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
                    if (replayRegistration != registration)
                        throw new CoordinationException(
                            "casting_generation_registration_stale", StatusCodes.Status409Conflict);
                    CastingGenerationExecutionContract.RequireCurrentRegistration(
                        replayRegistration, projectId, runId, sessionId,
                        replayStatus.ExecutionFence, selectionHash, timeProvider);
                    return Results.Ok(new ExecuteCastingGenerationResponse(
                        request.RequestId, runId, replayOutput, storedCompletion.Completion.ToEvidence()));
                }
                finally
                {
                    replayActor.Bearer.Invalidate();
                }
            }
            // The stored completion is from an earlier registration. Continue only when a verified host
            // replacement links that registration to the current one.
            if (storedCompletion is not null)
                await RequireCompletedReplacementReplayAsync(
                    starts,
                    projectId,
                    runId,
                    sessionId,
                    registration,
                    storedCompletion,
                    request.RequestId,
                    promptHash,
                    cancellationToken).ConfigureAwait(false);
            var decision = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            // Persist the exact request against this root decision before AgentHost can ask for model authority.
            if (storedCompletion is null)
                await actionGrants.AdmitCoordinatorRootModelTurnAsync(
                    context,
                    registration,
                    request.RequestId,
                    promptHash,
                    decision.StateVersion,
                    cancellationToken).ConfigureAwait(false);
            var actorForReadiness = await CreateRuntimeActorAsync(context, actor, timeProvider)
                .ConfigureAwait(false);
            RuntimeHostReadinessReceipt? readiness;
            try
            {
                readiness = await SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    clients.CreateClient("casting-generation-host"),
                    RuntimeHostEndpoint(registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness"),
                    actorForReadiness,
                    null,
                    allowNotReady: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                actorForReadiness.Bearer.Invalidate();
            }
            if (readiness is null)
                throw new CoordinationException(
                    "casting_generation_host_not_ready", StatusCodes.Status503ServiceUnavailable);

            // Readiness is remote; reject a run that ended or changed fences during that call.
            var currentStatus = await ReadCurrentAcceptedRootAsync(cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            var currentRegistration = await registrations.ReadCurrentForSessionAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            if (currentRegistration != registration)
                throw new CoordinationException(
                    "casting_generation_registration_stale", StatusCodes.Status409Conflict);
            CastingGenerationExecutionContract.RequireCurrentRegistration(
                currentRegistration, projectId, runId, sessionId,
                currentStatus.ExecutionFence, selectionHash, timeProvider);
            var hostRequest = CastingGenerationExecutionContract.CreateHostRequest(
                request, registration, readiness, timeProvider);
            var actorForSend = await CreateRuntimeActorAsync(context, actor, timeProvider)
                .ConfigureAwait(false);
            RuntimeA2AResponse hostResponse;
            try
            {
                hostResponse = await SendRuntimeHostMessageAsync(
                    clients.CreateClient("casting-generation-host"),
                    registration,
                    readiness,
                    actorForSend,
                    hostRequest,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                actorForSend.Bearer.Invalidate();
            }

            // Discard output if authority or host registration changed during the remote turn.
            currentStatus = await ReadCurrentAcceptedRootAsync(cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            currentRegistration = await registrations.ReadCurrentForSessionAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            if (currentRegistration != registration)
                throw new CoordinationException(
                    "casting_generation_registration_stale", StatusCodes.Status409Conflict);
            CastingGenerationExecutionContract.RequireCurrentRegistration(
                currentRegistration, projectId, runId, sessionId,
                currentStatus.ExecutionFence, selectionHash, timeProvider);
            var output = CastingGenerationExecutionContract.ReadOutput(
                hostResponse, RuntimeContractValidation.NativeSessionId(registration.Binding));
            // The replacement host replayed a turn that an earlier host completed. Check the stored receipt,
            // the replacement operation, and the stored material before the replay is recorded.
            if (hostResponse.RootCompletion is null && hostResponse.RootReplay is { } replay)
            {
                var retryPromptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(request.Prompt));
                var original = await actionGrants.ReadRootTurnCompletionAsync(
                    context, registration, request.RequestId, retryPromptHash, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                var intent = await starts.ReadIntentForStatusAsync(
                    projectId, runId, sessionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                if (intent.State != "ready" ||
                    intent.RuntimeInstanceId != registration.RuntimeInstanceId ||
                    !JsonDocument.Parse(intent.ReceiptLinks).RootElement
                        .TryGetProperty("replacementOperationId", out var operationElement) ||
                    !operationElement.TryGetGuid(out var operationId) || operationId == Guid.Empty)
                    throw new CoordinationException(
                        "casting_generation_replay_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                var operation = await starts.ReadReplacementOperationAsync(
                    intent, operationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                using var operationLinks = JsonDocument.Parse(operation.ReceiptLinks);
                if (operation.State != "ready" ||
                    !operationLinks.RootElement.TryGetProperty("previousRegistration", out var previousElement) ||
                    !operationLinks.RootElement.TryGetProperty("currentRegistration", out var currentElement) ||
                    !operationLinks.RootElement.TryGetProperty("completion", out var completionElement))
                    throw new CoordinationException(
                        "casting_generation_replay_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                var previousRegistration = previousElement.Deserialize<RuntimeRegistration>(JsonOptions)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                var savedCompletion = completionElement.Deserialize<RuntimeRootTurnCompletionLedgerReceipt>(JsonOptions)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                var replacementRegistration = currentElement.Deserialize<RuntimeRegistration>(JsonOptions)
                    ?? throw new CoordinationException(
                        "casting_generation_replay_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                if (!savedCompletion.HasSameReceipt(original) ||
                    operation.ExpectedRuntimeInstanceId != previousRegistration.RuntimeInstanceId ||
                    operation.ExpectedRegistrationRevision != previousRegistration.Revision ||
                    replacementRegistration != registration ||
                    !await starts.HasVerifiedReplacementLineageAsync(
                        intent, original.Completion.Registration, registration, cancellationToken)
                        .ConfigureAwait(false) ||
                    replay.OutputSha256 != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(output)))
                    throw new CoordinationException(
                        "casting_generation_replay_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                replay.ValidateFor(
                    original.Completion.ToEvidence(), original.Completion.Registration, registration);

                var replayActor = await CreateRuntimeActorAsync(context, actor, timeProvider)
                    .ConfigureAwait(false);
                try
                {
                    var user = await events.ReadSessionMaterialAsync(
                        context, identity, original.Completion.UserMaterial.EventId,
                        SessionMaterialKind.TurnContent, replayActor, cancellationToken).ConfigureAwait(false);
                    var assistant = await events.ReadSessionMaterialAsync(
                        context, identity, original.Completion.AssistantMaterial.EventId,
                        SessionMaterialKind.TurnContent, replayActor, cancellationToken).ConfigureAwait(false);
                    if (user.Material != original.Completion.UserMaterial ||
                        assistant.Material != original.Completion.AssistantMaterial ||
                        RuntimeContractValidation.Hash(user.Bytes) != original.Completion.PromptHash ||
                        RuntimeContractValidation.Hash(assistant.Bytes) != replay.OutputSha256 ||
                        !user.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(request.Prompt)) ||
                        !assistant.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(output)))
                        throw new CoordinationException(
                            "casting_generation_replay_material_invalid",
                            StatusCodes.Status503ServiceUnavailable);
                }
                finally
                {
                    replayActor.Bearer.Invalidate();
                }
                var recordedReplay = await actionGrants.RecordRootTurnReplayAsync(
                    context,
                    original,
                    original.Completion.Registration,
                    registration,
                    operationId,
                    replay,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(new ExecuteCastingGenerationResponse(
                    request.RequestId, runId, output, original.Completion.ToEvidence())
                {
                    Replay = recordedReplay.Replay
                });
            }
            var completion = hostResponse.RootCompletion
                ?? throw new CoordinationException(
                    "casting_generation_completion_receipt_unavailable",
                    StatusCodes.Status503ServiceUnavailable);
            if (hostResponse.RootReplay is not null)
                throw new CoordinationException(
                    "casting_generation_completion_receipt_invalid",
                    StatusCodes.Status503ServiceUnavailable);
            completion.Validate();
            if (completion.RequestId != request.RequestId ||
                completion.Registration != registration ||
                completion.ObserveGrant != readiness.SourceGrant ||
                completion.PromptHash != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(request.Prompt)) ||
                completion.NativeTurn.OutputHash != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(output)))
                throw new CoordinationException(
                    "casting_generation_completion_receipt_invalid",
                    StatusCodes.Status503ServiceUnavailable);

            var actorForMaterial = await CreateRuntimeActorAsync(context, actor, timeProvider)
                .ConfigureAwait(false);
            try
            {
                var user = await events.ReadSessionMaterialAsync(
                    context, identity, completion.UserMaterial.EventId, SessionMaterialKind.TurnContent,
                    actorForMaterial, cancellationToken).ConfigureAwait(false);
                var assistant = await events.ReadSessionMaterialAsync(
                    context, identity, completion.AssistantMaterial.EventId, SessionMaterialKind.TurnContent,
                    actorForMaterial, cancellationToken).ConfigureAwait(false);
                var cache = await events.ReadSessionMaterialAsync(
                    context, identity, completion.CacheMaterial.EventId, SessionMaterialKind.SdkCache,
                    actorForMaterial, cancellationToken).ConfigureAwait(false);
                if (user.Material != completion.UserMaterial ||
                    assistant.Material != completion.AssistantMaterial ||
                    cache.Material != completion.CacheMaterial ||
                    !user.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(request.Prompt)) ||
                    !assistant.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(output)))
                    throw new CoordinationException(
                        "casting_generation_material_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                actorForMaterial.Bearer.Invalidate();
            }

            var recordedCompletion = await actionGrants.RecordRootTurnCompletionAsync(
                context, registration, completion, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new ExecuteCastingGenerationResponse(
                request.RequestId, runId, output, recordedCompletion.Completion.ToEvidence()));
        }, cancellationToken);

    // A retry after host replacement is allowed only when the receipts prove that the new host
    // restored the completed turn from the saved native cache.
    private static async Task RequireCompletedReplacementReplayAsync(
        CoordinationRuntimeRunStartStore starts,
        string projectId,
        string runId,
        string sessionId,
        RuntimeRegistration currentRegistration,
        RuntimeRootTurnCompletionLedgerReceipt completion,
        Guid requestId,
        string promptHash,
        CancellationToken cancellationToken)
    {
        var intent = await starts.ReadIntentForStatusAsync(
            projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.State != "ready" ||
            intent.RuntimeInstanceId != currentRegistration.RuntimeInstanceId ||
            intent.ExecutionFence != currentRegistration.Binding.ExecutionFence ||
            intent.AcceptedSelectionHash != currentRegistration.Binding.AcceptedSelectionHash)
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);

        using var intentLinks = JsonDocument.Parse(intent.ReceiptLinks);
        if (!intentLinks.RootElement.TryGetProperty("replacementOperationId", out var operationElement) ||
            !operationElement.TryGetGuid(out var operationId) || operationId == Guid.Empty)
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);
        var operation = await starts.ReadReplacementOperationAsync(
            intent, operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.State != "ready" ||
            operation.ExecutionFence != currentRegistration.Binding.ExecutionFence ||
            operation.AcceptedSelectionHash != currentRegistration.Binding.AcceptedSelectionHash)
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);

        using var links = JsonDocument.Parse(operation.ReceiptLinks);
        if (!links.RootElement.TryGetProperty("previousRegistration", out var previousValue) ||
            !links.RootElement.TryGetProperty("currentRegistration", out var currentValue) ||
            !links.RootElement.TryGetProperty("previousPlacement", out var placementValue) ||
            !links.RootElement.TryGetProperty("completion", out var completionValue) ||
            !links.RootElement.TryGetProperty("currentActivation", out var activationValue))
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);
        var previousRegistration = previousValue.Deserialize<RuntimeRegistration>(JsonOptions);
        var replacementRegistration = currentValue.Deserialize<RuntimeRegistration>(JsonOptions);
        var previousPlacement = placementValue.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions);
        var savedCompletion = completionValue.Deserialize<RuntimeRootTurnCompletionLedgerReceipt>(JsonOptions);
        var activation = activationValue.Deserialize<RuntimeHostReadinessReceipt>(JsonOptions);
        if (previousRegistration is null || replacementRegistration is null ||
            previousPlacement is null || savedCompletion is null || activation?.Recovery is null)
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);

        var recovery = activation.Recovery.ValidateFor(currentRegistration);
        _ = RuntimeRunStartContinuation.CreateReplacementSummary(
            operation, previousRegistration, previousPlacement);
        if (!savedCompletion.HasSameReceipt(completion) ||
            completion.Completion.RequestId != requestId ||
            completion.Completion.PromptHash != promptHash ||
            operation.ExpectedRuntimeInstanceId != previousRegistration.RuntimeInstanceId ||
            operation.ExpectedRegistrationRevision != previousRegistration.Revision ||
            replacementRegistration != currentRegistration ||
            !await starts.HasVerifiedReplacementLineageAsync(
                intent, completion.Completion.Registration, previousRegistration, cancellationToken)
                .ConfigureAwait(false) ||
            recovery.RecoveryMode != RuntimeHostRecoveryMode.NativeCache ||
            recovery.NativeCacheAcknowledgment?.Reference != completion.Completion.CacheMaterial.Reference ||
            !RuntimeRunStartContinuation.MatchesReplacementReconstructionInterval(
                recovery, completion.Completion.CacheBaselineCursorHash))
            throw new CoordinationException(
                "casting_generation_replacement_replay_unavailable",
                StatusCodes.Status409Conflict);
    }

    private static async Task<RuntimeActorAuthorization> CreateRuntimeActorAsync(
        HttpContext context,
        CoordinationActor actor,
        TimeProvider timeProvider)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        var bearer = CoordinationIdentity.RequireBearer(context).Parameter!;
        // The caller bearer authorizes this owner call; AgentHost redeems model credentials separately.
        if (!authentication.Succeeded ||
            expiry is null ||
            expiry <= timeProvider.GetUtcNow() ||
            CoordinationIdentity.RequireActor(context.User, actor.Issuer) != actor)
            throw new CoordinationException("runtime_actor_expired", StatusCodes.Status403Forbidden);
        return new(
            new SecretCredential(bearer, expiry.Value, timeProvider),
            CoordinationIdentity.ReadTenantSelector(context));
    }
}
