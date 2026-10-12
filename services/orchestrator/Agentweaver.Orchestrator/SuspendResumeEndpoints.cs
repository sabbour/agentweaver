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
            SuspendOperationAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/resume",
            ResumeOperationAsync);
        return app;
    }

    public static IEndpointRouteBuilder MapRuntimeSuspendResumeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/internal/runtime/suspend/require-current",
            RequireCurrentSuspendAsync).RequireAuthorization();
        app.MapPost(
            "/internal/runtime/suspend/release/require-current",
            RequireCurrentSuspendReleaseAsync).RequireAuthorization();
        app.MapPost(
            "/internal/runtime/resume/require-current",
            RequireCurrentResumeAsync).RequireAuthorization();
        app.MapPost(
            "/internal/runtime/resume/require-bootstrap-current",
            RequireCurrentResumeBootstrapAsync).RequireAuthorization();
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

    private static Task<IResult> RequireCurrentSuspendReleaseAsync(
        RuntimeSuspendPlacementReleaseRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ArgumentNullException.ThrowIfNull(request);
            var binding = request.HostReceipt?.Registration?.Binding
                ?? throw new CoordinationException(
                    "runtime_suspend_release_request_invalid", StatusCodes.Status400BadRequest);
            var actor = RequireOwnerActor(
                context, options, binding.ProjectId, binding.RunId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
            if (binding.ActorIssuer != actor.Issuer ||
                binding.ActorId != actor.Subject ||
                binding.TenantId != selection.Authorization.TenantId ||
                binding.AcceptedSelectionHash != CoordinationOwnerStore.HashSelection(selection.Selection))
                throw new CoordinationException(
                    "runtime_suspend_release_scope_invalid", StatusCodes.Status403Forbidden);

            var echo = await coordinator.RequireCurrentSuspendReleaseAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, binding.ProjectId, binding.RunId, selection, projects, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(echo);
        }, cancellationToken);

    private static Task<IResult> RequireCurrentResumeAsync(
        RuntimeWorkspaceResumeRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ArgumentNullException.ThrowIfNull(request);
            var binding = request.SourceRelease?.HostReceipt?.Registration?.Binding
                ?? throw new CoordinationException(
                    "runtime_resume_request_invalid", StatusCodes.Status400BadRequest);
            var identity = request.SourceManifest?.Identity
                ?? throw new CoordinationException(
                    "runtime_resume_request_invalid", StatusCodes.Status400BadRequest);
            var actor = RequireOwnerActor(
                context, options, identity.ProjectId, identity.RunId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
            if (identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
                binding.ActorIssuer != actor.Issuer ||
                binding.ActorId != actor.Subject ||
                binding.TenantId != selection.Authorization.TenantId ||
                binding.AcceptedSelectionHash != CoordinationOwnerStore.HashSelection(selection.Selection))
                throw new CoordinationException(
                    "runtime_resume_scope_invalid", StatusCodes.Status403Forbidden);

            var echo = await coordinator.RequireCurrentResumeAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(echo);
        }, cancellationToken);

    private static Task<IResult> RequireCurrentResumeBootstrapAsync(
        RuntimeResumeBootstrapRequest request,
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
            var binding = request.Registration?.Binding
                ?? throw new CoordinationException(
                    "runtime_resume_bootstrap_request_invalid", StatusCodes.Status400BadRequest);
            var identity = request.Resume?.SourceManifest?.Identity
                ?? throw new CoordinationException(
                    "runtime_resume_bootstrap_request_invalid", StatusCodes.Status400BadRequest);
            var actor = RequireOwnerActor(
                context, options, identity.ProjectId, identity.RunId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, identity.ProjectId, identity.RunId, cancellationToken).ConfigureAwait(false);
            if (identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
                binding.ActorIssuer != actor.Issuer ||
                binding.ActorId != actor.Subject ||
                binding.TenantId != selection.Authorization.TenantId ||
                binding.AcceptedSelectionHash != CoordinationOwnerStore.HashSelection(selection.Selection))
                throw new CoordinationException(
                    "runtime_resume_bootstrap_scope_invalid", StatusCodes.Status403Forbidden);

            RuntimeRegistration currentRegistration;
            try
            {
                currentRegistration = await registrations.ReadCurrentAsync(
                    context, request.Registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
            }
            catch (RuntimeAuthorizationException exception)
            {
                throw new CoordinationException(
                    exception.Code, StatusCodes.Status403Forbidden, exception);
            }
            if (currentRegistration != request.Registration)
                throw new CoordinationException(
                    "runtime_registration_stale", StatusCodes.Status409Conflict);

            var echo = await coordinator.RequireCurrentResumeBootstrapAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
            await RequireUnchangedAuthorizedSelectionAsync(
                context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var finalRegistration = await registrations.ReadCurrentAsync(
                    context, request.Registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
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

    private static Task<IResult> SuspendOperationAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionSuspendRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var services = context.RequestServices;
            var registrations = services.GetService<RuntimeRegistrationOwner>();
            var environment = services.GetService<RuntimeWorkspaceSuspendHttpClient>();
            if (registrations is null || environment is null)
                return await coordinator.RecordUnavailableAsync(
                    actor,
                    selection,
                    identity,
                    SessionSuspendResumeOperationKind.Suspend,
                    request.IdempotencyKey,
                    sourceManifestId: null,
                    cancellationToken).ConfigureAwait(false);
            return await coordinator.SuspendAsync(
                context,
                actor,
                selection,
                identity,
                request.IdempotencyKey,
                options,
                registrations,
                services.GetRequiredService<CoordinatorDecisionOwnerStore>(),
                services.GetRequiredService<PostgresMafCheckpointStore>(),
                services.GetRequiredService<EventsAddressedMessageClient>(),
                environment,
                services.GetRequiredService<TimeProvider>(),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static Task<IResult> ResumeOperationAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionResumeRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            var services = context.RequestServices;
            var environment = services.GetService<RuntimeWorkspaceResumeHttpClient>();
            var bootstrap = services.GetService<RuntimeResumeBootstrapHttpClient>();
            var registrations = services.GetService<RuntimeRegistrationOwner>();
            if (environment is not null && bootstrap is not null && registrations is not null)
                return await coordinator.ResumeAsync(
                    context,
                    actor,
                    selection,
                    new SessionIdentity(projectId, runId, sessionId),
                    request.IdempotencyKey,
                    request.ManifestId,
                    options,
                    environment,
                    bootstrap,
                    registrations,
                    services.GetRequiredService<TimeProvider>(),
                    cancellationToken).ConfigureAwait(false);
            return await coordinator.RecordUnavailableAsync(
                actor,
                selection,
                new SessionIdentity(projectId, runId, sessionId),
                SessionSuspendResumeOperationKind.Resume,
                request.IdempotencyKey,
                request.ManifestId,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
