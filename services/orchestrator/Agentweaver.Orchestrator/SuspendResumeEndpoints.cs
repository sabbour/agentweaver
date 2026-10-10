using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public sealed record SessionSuspendRequest(string IdempotencyKey);

public sealed record SessionResumeRequest(string IdempotencyKey, Guid ManifestId);

public static partial class CoordinationEndpoints
{
    public static IEndpointRouteBuilder MapSuspendResumeEndpoints(this IEndpointRouteBuilder app)
    {
        var coordination = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination")
            .RequireAuthorization();
        coordination.MapPost(
            "/sessions/{sessionId}/suspend",
            SuspendUnavailableAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/resume",
            ResumeUnavailableAsync);
        return app;
    }

    public static IEndpointRouteBuilder MapRuntimeSuspendResumeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/internal/runtime/suspend/require-current",
            RequireCurrentSuspendAsync).RequireAuthorization();
        return app;
    }

    private static Task<IResult> RequireCurrentSuspendAsync(
        RuntimeHostSuspendRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        [Microsoft.AspNetCore.Mvc.FromServices] RuntimeRegistrationOwner registrations,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Proof);
            ArgumentNullException.ThrowIfNull(request.Proof.Registration);
            ArgumentNullException.ThrowIfNull(request.Proof.Registration.Binding);
            var proof = request.Proof;
            var registration = proof.Registration;
            var binding = registration.Binding;
            if (proof.ContractVersion != 1 ||
                proof.SourceGrantId == Guid.Empty ||
                proof.SourceGrantRevision < 1 ||
                proof.Purpose != RuntimeCredentialPurpose.Observe ||
                request.OperationId == Guid.Empty ||
                request.ManifestId == Guid.Empty ||
                request.PhaseVersion < 1)
                throw new CoordinationException(
                    "runtime_suspend_request_invalid", StatusCodes.Status400BadRequest);

            var actor = RequireOwnerActor(
                context, options, binding.ProjectId, binding.RunId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
            if (binding.ActorIssuer != actor.Issuer ||
                binding.ActorId != actor.Subject ||
                binding.TenantId != selection.Authorization.TenantId ||
                binding.AcceptedSelectionHash != CoordinationOwnerStore.HashSelection(selection.Selection))
                throw new CoordinationException(
                    "runtime_suspend_proof_scope_invalid", StatusCodes.Status403Forbidden);

            RuntimeRegistration currentRegistration;
            try
            {
                currentRegistration = await registrations.ReadCurrentAsync(
                    context, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeAuthorizationException exception)
            {
                throw new CoordinationException(
                    exception.Code, StatusCodes.Status403Forbidden, exception);
            }
            if (currentRegistration != registration)
                throw new CoordinationException(
                    "runtime_registration_stale", StatusCodes.Status409Conflict);

            var echo = await coordinator.RequireCurrentSuspendAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, binding.ProjectId, binding.RunId, selection, projects, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var finalRegistration = await registrations.ReadCurrentAsync(
                    context, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
                if (finalRegistration != currentRegistration)
                    throw new CoordinationException(
                        "runtime_registration_stale", StatusCodes.Status409Conflict);
            }
            catch (RuntimeAuthorizationException exception)
            {
                throw new CoordinationException(
                    exception.Code, StatusCodes.Status403Forbidden, exception);
            }

            return Results.Ok(echo);
        }, cancellationToken);

    private static Task<IResult> SuspendUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionSuspendRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ReturnUnavailableAsync(
            projectId,
            runId,
            sessionId,
            SessionSuspendResumeOperationKind.Suspend,
            request.IdempotencyKey,
            sourceManifestId: null,
            context,
            options,
            projects,
            coordinator,
            cancellationToken);

    private static Task<IResult> ResumeUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionResumeRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ReturnUnavailableAsync(
            projectId,
            runId,
            sessionId,
            SessionSuspendResumeOperationKind.Resume,
            request.IdempotencyKey,
            request.ManifestId,
            context,
            options,
            projects,
            coordinator,
            cancellationToken);

    private static Task<IResult> ReturnUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        Guid? sourceManifestId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return await coordinator.RecordUnavailableAsync(
                actor,
                selection,
                new SessionIdentity(projectId, runId, sessionId),
                kind,
                idempotencyKey,
                sourceManifestId,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
