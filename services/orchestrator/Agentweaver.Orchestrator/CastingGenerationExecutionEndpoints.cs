using System.Net.Http;
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
        RuntimeRegistrationOwner registrations,
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

            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var status = await ReadCurrentAcceptedRootAsync(cancellationToken).ConfigureAwait(false);
            // Pin the turn to the accepted snapshot before contacting the registered host.
            var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);

            var registration = await registrations.ReadCurrentForSessionAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            CastingGenerationExecutionContract.RequireCurrentRegistration(
                registration, projectId, runId, sessionId, status.ExecutionFence, selectionHash, timeProvider);
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
            return Results.Ok(new ExecuteCastingGenerationResponse(request.RequestId, runId, output));
        }, cancellationToken);

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
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        return new(
            new SecretCredential(bearer, expiry.Value, timeProvider),
            CoordinationIdentity.ReadTenantSelector(context));
    }
}
