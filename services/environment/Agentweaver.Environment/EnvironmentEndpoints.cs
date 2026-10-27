using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Environment;

/// <summary>Maps the Environment lifecycle, Sandbox, egress, and workspace-volume HTTP API.</summary>
public static class EnvironmentEndpoints
{
    /// <summary>Registers the authenticated Environment API routes and their service dependencies.</summary>
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var environments = endpoints.MapGroup("/api/projects/{projectId}/runs/{runId}/environments")
            .RequireAuthorization();
        environments.MapPost("", async (
            string projectId,
            string runId,
            EnvironmentRegistrationApiRequest request,
            HttpContext context,
            EnvironmentLifecycleManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is not null)
                return Results.BadRequest(new { code = "p1_internal_route_required" });
            try
            {
                var result = await manager.RegisterAsync(
                    caller!, projectId, runId, request, cancellationToken).ConfigureAwait(false);
                return Results.Json(
                    result,
                    statusCode: result.Replayed
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status201Created);
            }
            catch (ProjectsConfigApiException exception)
            {
                return ToProjectAuthorizationResult(exception);
            }
            catch (RuntimeAuthorizationException exception)
            {
                return Results.Json(
                    new { code = exception.Code },
                    statusCode: exception.Code == "runtime_owner_denied"
                        ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
            }
            catch (EnvironmentLifecycleException exception)
            {
                return Results.Json(
                    new { code = exception.Code, message = exception.Message },
                    statusCode: exception.Code == "environment_unknown"
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status409Conflict);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        endpoints.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/environments",
            async (
                string projectId,
                string runId,
                EnvironmentRegistrationApiRequest request,
                HttpContext context,
                EnvironmentLifecycleManager manager,
                [FromServices] TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!TryReadCaller(context, out var caller))
                    return Results.Unauthorized();
                if (request.P1Binding is null)
                    return Results.BadRequest(new { code = "p1_invocation_binding_expectation_required" });
                SecretCredential? actorCredential = null;
                try
                {
                    var p1Actor = await CreateP1ActorAsync(context, caller!, timeProvider)
                        .ConfigureAwait(false);
                    actorCredential = p1Actor.Credential;
                    var result = await manager.RegisterAsync(
                        caller!, projectId, runId, request, cancellationToken, p1Actor.Actor)
                        .ConfigureAwait(false);
                    return Results.Json(
                        result,
                        statusCode: result.Replayed
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status201Created);
                }
                catch (ProjectsConfigApiException exception)
                {
                    return ToProjectAuthorizationResult(exception);
                }
                catch (RuntimeAuthorizationException exception)
                {
                    return Results.Json(
                        new { code = exception.Code },
                        statusCode: exception.Code == "runtime_owner_denied"
                            ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
                }
                catch (EnvironmentLifecycleException exception)
                {
                    return Results.Json(
                        new { code = exception.Code, message = exception.Message },
                        statusCode: exception.Code == "environment_unknown"
                            ? StatusCodes.Status404NotFound
                            : StatusCodes.Status409Conflict);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(
                        new { code = "invalid_environment_request", message = exception.Message });
                }
                catch (HttpRequestException)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                finally
                {
                    actorCredential?.Invalidate();
                }
            }).RequireAuthorization();
        environments.MapPost("/{environmentId}/release", async (
            string projectId,
            string runId,
            string environmentId,
            EnvironmentReleaseApiRequest request,
            HttpContext context,
            EnvironmentLifecycleManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                var result = await manager.ReleaseAsync(
                    caller!, projectId, runId, environmentId, request, cancellationToken).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (ProjectsConfigApiException exception)
            {
                return ToProjectAuthorizationResult(exception);
            }
            catch (EnvironmentLifecycleException exception)
            {
                return Results.Json(
                    new { code = exception.Code, message = exception.Message },
                    statusCode: exception.Code == "environment_unknown"
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status409Conflict);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        var group = endpoints.MapGroup("/api/network-egress").RequireAuthorization();
        group.MapPost("/apply", async (
            ApplyEnvironmentEgressRequest request,
            HttpContext context,
            EnvironmentEgressManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                return ToHttpResult(await manager.ApplyAndVerifyAsync(
                    caller!,
                    request,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
        });

        group.MapPost("/verify", async (
            RuntimeEgressVerificationRequest request,
            HttpContext context,
            EnvironmentEgressManager manager,
            CancellationToken cancellationToken) =>
        {
            // Owner-to-owner verification responses must never be cached because their receipt is current evidence.
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                return ToEgressVerificationHttpResult(await manager.VerifyCurrentAsync(
                    caller!,
                    request,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
        });

        group.MapPost("/revoke", async (
            ApplyEnvironmentEgressRequest request,
            HttpContext context,
            EnvironmentEgressManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                return ToHttpResult(await manager.RevokeAsync(
                    caller!,
                    request,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
        });

        group.MapPost("/reconcile", async (
            ReconcileEnvironmentEgressRequest request,
            HttpContext context,
            EnvironmentEgressManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                return ToHttpResult(await manager.ReconcileAsync(
                    caller!,
                    request,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "invalid_environment_request", message = exception.Message });
            }
        });

        endpoints.MapGet(
            "/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/lifecycle",
            async (
                string projectId,
                string runId,
                string environmentId,
                HttpContext context,
                EnvironmentEgressManager manager,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!TryReadCaller(context, out var caller))
                    return Results.Unauthorized();
                try
                {
                    var snapshot = await manager.InspectLifecycleAsync(
                        caller!,
                        projectId,
                        runId,
                        environmentId,
                        cancellationToken).ConfigureAwait(false);
                    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
                }
                catch (ProjectsConfigApiException exception)
                {
                    return ToProjectAuthorizationResult(exception);
                }
                catch (HttpRequestException)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            })
            .RequireAuthorization();

        MapRuntimePlacement("");
        MapRuntimePlacement("/readiness");
        MapRuntimePlacement("/pod-observation");
        MapRuntimePlacement("/workspace");

        void MapRuntimePlacement(string suffix) => endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}" +
                suffix,
            async (
                string projectId,
                string runId,
                string environmentId,
                string sessionId,
                string profileId,
                HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimePlacementReader reader,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeBootstrapProfileRegistry profiles,
                [Microsoft.AspNetCore.Mvc.FromServices] IEnvironmentRuntimeBootstrapProfileReplacementStore profileReplacements,
                [Microsoft.AspNetCore.Mvc.FromServices] TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!TryReadCaller(context, out var caller))
                    return Results.Unauthorized();
                var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
                if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                    expiresAt <= timeProvider.GetUtcNow())
                    return Results.Unauthorized();
                var bearer = new SecretCredential(caller!.BearerToken, expiresAt, timeProvider);
                try
                {
                    var actor = new RuntimeActorAuthorization(bearer, caller.TenantSelector);
                    if (suffix == "/pod-observation")
                        return Results.Ok(await reader.GetPodObservationAsync(
                            actor, projectId, runId, sessionId, environmentId, profileId, profiles,
                            cancellationToken, profileReplacements).ConfigureAwait(false));
                    if (suffix == "/workspace")
                    {
                        var workspace = await reader.GetWorkspaceContextAsync(
                            actor, projectId, runId, sessionId, environmentId, profileId, profiles, cancellationToken,
                            profileReplacements)
                            .ConfigureAwait(false);
                        return workspace is null ? Results.NotFound() : Results.Ok(workspace);
                    }
                    if (suffix == "/readiness")
                    {
                        var observed = await reader.GetReadinessContextAsync(
                            actor, projectId, runId, sessionId, environmentId, profileId, profiles, cancellationToken,
                            profileReplacements)
                            .ConfigureAwait(false);
                        return observed is null ? Results.NotFound() : Results.Ok(observed);
                    }
                    var bootstrap = await reader.GetBootstrapContextAsync(
                        actor, projectId, runId, sessionId, environmentId, profileId, profiles, cancellationToken,
                        profileReplacements)
                        .ConfigureAwait(false);
                    return bootstrap is null ? Results.NotFound() : Results.Ok(bootstrap);
                }
                catch (ProjectsConfigApiException exception)
                {
                    return ToProjectAuthorizationResult(exception);
                }
                catch (RuntimeAuthorizationException exception)
                {
                    return Results.Conflict(new { code = exception.Code });
                }
                catch (EnvironmentLifecycleException exception)
                {
                    return Results.Json(new { code = exception.Code, message = exception.Message },
                        statusCode: exception.Code == "environment_unknown"
                            ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);
                }
                catch (CiliumPolicyException exception)
                {
                    return Results.Conflict(new { code = exception.Code });
                }
                catch (SandboxProviderException exception)
                {
                    return Results.Conflict(new { code = exception.Code });
                }
                catch (HttpRequestException)
                {
                    return Results.Json(new { code = "runtime_placement_owner_unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                finally
                {
                    bearer.Invalidate();
                }
            }).RequireAuthorization();

        MapRuntimeResume("prepare");
        MapRuntimeResume("verify");

        void MapRuntimeResume(string stage) => endpoints.MapPost(
            $"/internal/runtime/resume/{stage}", async (
                RuntimeWorkspaceResumeRequest request,
                HttpContext context,
                [FromServices] TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!TryReadCaller(context, out var caller))
                    return Results.Unauthorized();
                var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
                if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                    expiresAt <= timeProvider.GetUtcNow())
                    return Results.Unauthorized();
                var manager = context.RequestServices.GetService<EnvironmentRuntimeWorkspaceResumeManager>();
                if (manager is null)
                    return Results.Json(new { code = "runtime_resume_owner_unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                var bearer = new SecretCredential(caller!.BearerToken, expiresAt, timeProvider);
                try
                {
                    var actor = new RuntimeActorAuthorization(bearer, caller.TenantSelector);
                    return await ExecuteSandboxApiAsync(async () => stage == "prepare"
                        ? Results.Ok(await manager.PrepareAsync(request, actor, cancellationToken).ConfigureAwait(false))
                        : Results.Ok(await manager.VerifyAsync(request, actor, cancellationToken).ConfigureAwait(false)),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    bearer.Invalidate();
                }
            }).RequireAuthorization();

        endpoints.MapPost("/internal/runtime/suspend/resolve", async (
            RuntimeHostSuspendRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                expiresAt <= timeProvider.GetUtcNow())
                return Results.Unauthorized();
            var bearer = new SecretCredential(caller!.BearerToken, expiresAt, timeProvider);
            try
            {
                return await ExecuteWorkspaceVolumeApiAsync(async () =>
                    Results.Ok(await manager.ResolveSuspendAsync(
                        caller, request, new RuntimeActorAuthorization(bearer, caller.TenantSelector),
                        cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                bearer.Invalidate();
            }
        }).RequireAuthorization();

        endpoints.MapPost("/internal/runtime/suspend/release-placement", async (
            RuntimeSuspendPlacementReleaseRequest request,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () => Results.Ok(
                await manager.ReleaseSuspendPlacementAsync(caller!, request, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        }).RequireAuthorization();

        var workspaceVolumes = endpoints.MapGroup(
                "/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes")
            .RequireAuthorization();
        workspaceVolumes.MapGet("/{volumeId}", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
            {
                var snapshot = await manager.InspectAsync(
                    caller!, projectId, runId, environmentId, volumeId, cancellationToken).ConfigureAwait(false);
                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            }, cancellationToken).ConfigureAwait(false);
        });
        workspaceVolumes.MapPost("/{volumeId}", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            CreateWorkspaceVolumeApiRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is not null)
                return Results.BadRequest(new { code = "p1_internal_route_required" });
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
                Results.Ok(await manager.CreateAsync(
                    caller!, projectId, runId, environmentId, volumeId, request, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        var p1WorkspaceVolumes = endpoints.MapGroup(
                "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes")
            .RequireAuthorization();
        p1WorkspaceVolumes.MapPost("/{volumeId}", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            CreateWorkspaceVolumeApiRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is null)
                return Results.BadRequest(new { code = "p1_invocation_binding_expectation_required" });
            SecretCredential? actorCredential = null;
            try
            {
                var p1Actor = await CreateP1ActorAsync(context, caller!, timeProvider)
                    .ConfigureAwait(false);
                actorCredential = p1Actor.Credential;
                return await ExecuteWorkspaceVolumeApiAsync(async () =>
                    Results.Ok(await manager.CreateP1Async(
                        caller!, projectId, runId, environmentId, volumeId,
                        request, p1Actor.Actor, cancellationToken).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                actorCredential?.Invalidate();
            }
        });
        p1WorkspaceVolumes.MapPost("/{volumeId}/provision", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            WorkspaceVolumeApiTransitionRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is null)
                return Results.BadRequest(new { code = "p1_invocation_binding_expectation_required" });
            SecretCredential? actorCredential = null;
            try
            {
                var p1Actor = await CreateP1ActorAsync(context, caller!, timeProvider)
                    .ConfigureAwait(false);
                actorCredential = p1Actor.Credential;
                return await ExecuteWorkspaceVolumeApiAsync(async () =>
                {
                    var result = await manager.ProvisionAsync(
                        caller!, projectId, runId, environmentId, volumeId,
                        request, cancellationToken, p1Actor.Actor).ConfigureAwait(false);
                    return ToWorkspaceVolumeTransitionResult(result);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                actorCredential?.Invalidate();
            }
        });
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "provision",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, actor, token) =>
                manager.ProvisionAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token, actor));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "replace",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, _, token) =>
                manager.ReplaceAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "bind",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, _, token) =>
                manager.BindVolumeAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "unbind",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, _, token) =>
                manager.UnbindAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "release",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, _, token) =>
                manager.ReleaseAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        workspaceVolumes.MapPost("/{volumeId}/flush", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            WorkspaceVolumeApiFlushRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                expiresAt <= timeProvider.GetUtcNow())
                return Results.Unauthorized();
            var bearer = new SecretCredential(caller!.BearerToken, expiresAt, timeProvider);
            try
            {
                return await ExecuteWorkspaceVolumeApiAsync(async () =>
                    ToWorkspaceVolumeTransitionResult(await manager.FlushAsync(
                        caller, projectId, runId, environmentId, volumeId, request,
                        new RuntimeActorAuthorization(bearer, caller.TenantSelector), cancellationToken)
                        .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                bearer.Invalidate();
            }
        });
        workspaceVolumes.MapPost("/cleanup/retry", async (
            string projectId,
            string runId,
            string environmentId,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
            {
                var status = await manager.RetryCleanupAsync(
                    caller!, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
                return status is null
                    ? Results.NoContent()
                    : status.State == EnvironmentWorkspaceVolumeCleanupState.Completed
                        ? Results.Ok(status)
                        : Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }, cancellationToken).ConfigureAwait(false);
        });
        workspaceVolumes.MapPost("/cleanup/{sourceReplaceOperationId:guid}/reconcile", async (
            string projectId,
            string runId,
            string environmentId,
            Guid sourceReplaceOperationId,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
            {
                var status = await manager.ReconcileCleanupAsync(
                    caller!, projectId, runId, environmentId, sourceReplaceOperationId, cancellationToken)
                    .ConfigureAwait(false);
                return status is null
                    ? Results.NotFound()
                    : status.State == EnvironmentWorkspaceVolumeCleanupState.Completed
                        ? Results.Ok(status)
                        : Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }, cancellationToken).ConfigureAwait(false);
        });

        var sandboxes = endpoints.MapGroup(
                "/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox")
            .RequireAuthorization();
        sandboxes.MapPost("/provision", async (
            string projectId,
            string runId,
            string environmentId,
            SandboxProvisionApiRequest request,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is not null)
                return Results.BadRequest(new { code = "p1_internal_route_required" });
            return await ExecuteSandboxApiAsync(async () =>
            {
                var result = await manager.ProvisionAsync(
                    caller!, projectId, runId, environmentId, request, cancellationToken).ConfigureAwait(false);
                return ToSandboxResult(result);
            }, cancellationToken).ConfigureAwait(false);
        });
        var p1Sandboxes = endpoints.MapGroup(
                "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox")
            .RequireAuthorization();
        p1Sandboxes.MapPost("/provision", async (
            string projectId,
            string runId,
            string environmentId,
            SandboxProvisionApiRequest request,
            HttpContext context,
            EnvironmentSandboxManager manager,
            [FromServices] TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is null)
                return Results.BadRequest(new { code = "p1_invocation_binding_expectation_required" });
            SecretCredential? actorCredential = null;
            try
            {
                var p1Actor = await CreateP1ActorAsync(context, caller!, timeProvider)
                    .ConfigureAwait(false);
                actorCredential = p1Actor.Credential;
                return await ExecuteSandboxApiAsync(async () =>
                {
                    var result = await manager.ProvisionAsync(
                        caller!, projectId, runId, environmentId,
                        request, cancellationToken, p1Actor.Actor).ConfigureAwait(false);
                    return ToSandboxResult(result);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                actorCredential?.Invalidate();
            }
        });
        sandboxes.MapGet("/", async (
            string projectId,
            string runId,
            string environmentId,
            long networkPolicyGeneration,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                ToSandboxResult(await manager.InspectAsync(
                    caller!, projectId, runId, environmentId, networkPolicyGeneration, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/v1/placement", async (
            string projectId,
            string runId,
            string environmentId,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
            {
                var projection = await manager.GetCurrentPlacementAsync(
                    caller!, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
                return projection is null ? Results.NotFound() : Results.Ok(projection);
            }, cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/v1/internal/placement", async (
            string projectId,
            string runId,
            string environmentId,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
            {
                var projection = await manager.GetCurrentRunBoundPlacementAsync(
                    caller!, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
                return projection is null ? Results.NotFound() : Results.Ok(projection);
            }, cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/v1/operations/{operationId:guid}/provider-attempts", async (
            string projectId,
            string runId,
            string environmentId,
            Guid operationId,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                Results.Ok(await manager.GetProviderAttemptsAsync(
                    caller!, projectId, runId, environmentId, operationId, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/v1/late-resource-cleanups", async (
            string projectId,
            string runId,
            string environmentId,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                Results.Ok(await manager.GetLateResourceCleanupsAsync(
                    caller!, projectId, runId, environmentId, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/build-test/binding-preparation", async (
            string projectId,
            string runId,
            string environmentId,
            string sessionId,
            string executionProfileReference,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                Results.Ok(await manager.PrepareBindingAsync(
                    caller!, projectId, runId, environmentId, sessionId,
                    executionProfileReference, cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapPost("/build-test/commands", async (
            string projectId,
            string runId,
            string environmentId,
            SandboxBuildTestApiRequest request,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
            {
                var result = await manager.ExecuteAsync(
                    caller!, projectId, runId, environmentId, request, cancellationToken)
                    .ConfigureAwait(false);
                return result.Operation.Status is SandboxBuildTestOperationStatus.Reserved or
                    SandboxBuildTestOperationStatus.Running or
                    SandboxBuildTestOperationStatus.ReconciliationRequired
                    ? Results.Accepted(value: result)
                    : Results.Ok(result);
            }, cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/build-test/commands/{operationId:guid}", async (
            string projectId,
            string runId,
            string environmentId,
            Guid operationId,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
            {
                var result = await manager.GetAsync(
                    caller!, projectId, runId, environmentId, operationId, cancellationToken)
                    .ConfigureAwait(false);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }, cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapPost("/build-test/commands/{operationId:guid}/reconcile", async (
            string projectId,
            string runId,
            string environmentId,
            Guid operationId,
            SandboxBuildTestApiRequest request,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
            {
                var result = await manager.ReconcileAsync(
                    caller!, projectId, runId, environmentId, operationId, request, cancellationToken)
                    .ConfigureAwait(false);
                return result.Operation.Status is SandboxBuildTestOperationStatus.Reserved or
                    SandboxBuildTestOperationStatus.Running or
                    SandboxBuildTestOperationStatus.ReconciliationRequired
                    ? Results.Accepted(value: result)
                    : Results.Ok(result);
            }, cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapGet("/build-test/commands/{operationId:guid}/workspace-capture", async (
            string projectId, string runId, string environmentId, Guid operationId,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () => Results.Bytes(
                (await manager.ReadWorkspaceCaptureAsync(caller!, projectId, runId, environmentId,
                    operationId, cancellationToken).ConfigureAwait(false)).ToArray(),
                "application/octet-stream"), cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapPost("/build-test/commands/{operationId:guid}/workspace-capture/acknowledge", async (
            string projectId, string runId, string environmentId, Guid operationId,
            SandboxBuildTestWorkspaceCaptureAcknowledgmentRequest request,
            HttpContext context,
            [FromServices] EnvironmentSandboxBuildTestCommandManager manager,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () => Results.Ok(
                await manager.AcknowledgeWorkspaceCaptureAsync(caller!, projectId, runId, environmentId,
                    operationId, request, cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapPost("/abandon", async (
            string projectId,
            string runId,
            string environmentId,
            SandboxAbandonApiRequest request,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                ToSandboxResult(await manager.AbandonAsync(
                    caller!, projectId, runId, environmentId, request, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        sandboxes.MapPost("/reconcile", async (
            string projectId,
            string runId,
            string environmentId,
            long? networkPolicyGeneration,
            HttpContext context,
            EnvironmentSandboxManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            return await ExecuteSandboxApiAsync(async () =>
                ToSandboxResult(await manager.ReconcileAsync(
                    caller!, projectId, runId, environmentId, networkPolicyGeneration, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });

        return endpoints;
    }

    private static void MapWorkspaceVolumeTransition(
        RouteGroupBuilder group,
        string route,
        Func<EnvironmentWorkspaceVolumeManager, CurrentCallerRequest, string, string, string, string,
            WorkspaceVolumeApiTransitionRequest, RuntimeActorAuthorization?, CancellationToken,
            Task<WorkspaceVolumeLifecycleResult>> execute)
    {
        group.MapPost($"/{{volumeId}}/{route}", async (
            string projectId,
            string runId,
            string environmentId,
            string volumeId,
            WorkspaceVolumeApiTransitionRequest request,
            HttpContext context,
            EnvironmentWorkspaceVolumeManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            if (request.P1Binding is not null)
                return Results.BadRequest(new { code = "p1_internal_route_required" });
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
            {
                var result = await execute(
                    manager, caller!, projectId, runId, environmentId, volumeId,
                    request, null, cancellationToken).ConfigureAwait(false);
                return ToWorkspaceVolumeTransitionResult(result);
            }, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>Maps transition lifecycle results to the documented HTTP status and response body.</summary>
    internal static IResult ToWorkspaceVolumeTransitionResult(WorkspaceVolumeLifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CleanupPending)
            return Results.Json(result, statusCode: StatusCodes.Status202Accepted);

        var state = result.Completion?.TransitionState ?? result.Reservation.TransitionState;
        return state switch
        {
            EnvironmentWorkspaceVolumeTransitionState.Completed => Results.Ok(result),
            EnvironmentWorkspaceVolumeTransitionState.Failed or
                EnvironmentWorkspaceVolumeTransitionState.Stale =>
                Results.Conflict(new
                {
                    code = "workspace_volume_transition_not_completed",
                    message = "Refresh the owner state and retry with a new idempotency key.",
                    transitionState = state
                }),
            _ => Results.Json(result, statusCode: StatusCodes.Status202Accepted)
        };
    }

    /// <summary>Runs a workspace-volume endpoint operation and translates expected storage and lifecycle failures.</summary>
    private static async Task<IResult> ExecuteWorkspaceVolumeApiAsync(
        Func<Task<IResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ProjectsConfigApiException exception)
        {
            return ToProjectAuthorizationResult(exception);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { code = exception.Code },
                statusCode: exception.Code == "runtime_owner_denied"
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
        }
        catch (EnvironmentLifecycleException exception)
        {
            var status = exception.Code == "environment_unknown"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.Json(
                new { code = "workspace_volume_unknown", message = exception.Message },
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { code = "invalid_workspace_volume_request", message = exception.Message });
        }
        catch (NotSupportedException exception)
        {
            return Results.Conflict(new { code = "workspace_volume_operation_unsupported", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { code = "workspace_volume_state_conflict", message = exception.Message });
        }
        catch (AzureFilesCsiException exception)
        {
            return ToAzureFilesCsiErrorResult(exception);
        }
        catch (AzureFilesKubernetesApiException)
        {
            return Results.Json(
                new { code = "storage_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.Json(
                new { code = "upstream_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { code = "upstream_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Runs a Sandbox endpoint operation and translates expected provider and authorization failures.</summary>
    private static async Task<IResult> ExecuteSandboxApiAsync(
        Func<Task<IResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ProjectsConfigApiException exception)
        {
            return ToProjectAuthorizationResult(exception);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(
                new { code = exception.Code },
                statusCode: exception.Code == "runtime_owner_denied"
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status503ServiceUnavailable);
        }
        catch (EnvironmentLifecycleException exception)
        {
            var status = exception.Code is "environment_unknown" or
                "sandbox_lease_unknown" or "sandbox_operation_unknown" or "workspace_volume_unknown"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status409Conflict;
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
        catch (SandboxProviderException exception)
        {
            return Results.Json(
                new { code = exception.Code },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (CiliumPolicyException exception)
        {
            return Results.Json(
                new { code = exception.Code, message = exception.Message },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { code = "invalid_sandbox_request", message = exception.Message });
        }
        catch (NotSupportedException exception)
        {
            return Results.UnprocessableEntity(
                new { code = "sandbox_operation_unsupported", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { code = "sandbox_state_conflict", message = exception.Message });
        }
        catch (HttpRequestException)
        {
            return Results.Json(
                new { code = "upstream_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { code = "upstream_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Maps a Sandbox result to its HTTP response without exposing internal provider details.</summary>
    private static IResult ToSandboxResult(EnvironmentSandboxResult? result)
    {
        if (result is null)
            return Results.NotFound();
        var status = result.ReadyForDispatch ||
                     result.State is SandboxLeaseState.Released or SandboxLeaseState.Failed
            ? StatusCodes.Status200OK
            : StatusCodes.Status202Accepted;
        return Results.Json(result, statusCode: status);
    }

    /// <summary>Maps an Azure Files CSI failure to an HTTP response using its safe error code.</summary>
    internal static IResult ToAzureFilesCsiErrorResult(AzureFilesCsiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var statusCode = exception.Code is
                "capacity_exceeded" or
                "consistency_unsupported" or
                "storage_class_mismatch" or
                "storage_class_missing" or
                "storage_class_invalid" or
                "mount_options_invalid"
                    ? StatusCodes.Status422UnprocessableEntity
                    : StatusCodes.Status503ServiceUnavailable;
        return Results.Json(new { code = exception.Code }, statusCode: statusCode);
    }

    /// <summary>Extracts the authenticated bearer caller and optional tenant selector from the current request.</summary>
    private static bool TryReadCaller(HttpContext context, out CurrentCallerRequest? caller)
    {
        caller = null;
        if (context.User.Identity?.IsAuthenticated != true ||
            !AuthenticationHeaderValue.TryParse(
                context.Request.Headers.Authorization.ToString(),
                out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return false;

        var tenantSelector = context.Request.Headers.TryGetValue(
            ProjectAuthorizationContextContract.TenantSelectorHeader,
            out var values)
            ? values.ToString()
            : null;
        try
        {
            caller = new CurrentCallerRequest(authorization.Parameter, tenantSelector);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<(RuntimeActorAuthorization Actor, SecretCredential Credential)> CreateP1ActorAsync(
        HttpContext context,
        CurrentCallerRequest caller,
        TimeProvider timeProvider)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!authentication.Succeeded ||
            authentication.Properties?.ExpiresUtc is not { } expiresAt ||
            expiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("p1_invocation_binding_actor_required");
        var credential = new SecretCredential(caller.BearerToken, expiresAt, timeProvider);
        return (new RuntimeActorAuthorization(credential, caller.TenantSelector), credential);
    }

    /// <summary>Maps a network policy operation result to a ready or not-ready API response.</summary>
    private static IResult ToHttpResult(EnvironmentEgressOperationResult operation)
    {
        if (operation.FailureCode is null)
            return Results.Ok(operation);
        var status = operation.FailureCode switch
        {
            "project_write_not_authorized" or "project_read_not_authorized" or
                "run_selection_not_authorized" or "authorization_context_denied" or
                "authorization_context_mismatch" or "authorization_changed" or
                "tenant_selector_mismatch" => StatusCodes.Status403Forbidden,
            "upstream_unavailable" or "upstream_timeout" => StatusCodes.Status503ServiceUnavailable,
            "environment_unknown" => StatusCodes.Status404NotFound,
            "invalid_environment_request" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(operation, statusCode: status);
    }

    /// <summary>Maps current egress verification outcomes to stable HTTP statuses without changing policy state.</summary>
    private static IResult ToEgressVerificationHttpResult(RuntimeEgressVerificationResponse response)
    {
        var status = response.FailureCode switch
        {
            RuntimeEgressVerificationFailureCodes.Missing => StatusCodes.Status404NotFound,
            RuntimeEgressVerificationFailureCodes.AuthorizationChanged => StatusCodes.Status403Forbidden,
            RuntimeEgressVerificationFailureCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
            null => StatusCodes.Status200OK,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(response, statusCode: status);
    }

    /// <summary>Maps Projects &amp; Config authorization failures to their public HTTP response contract.</summary>
    internal static IResult ToProjectAuthorizationResult(ProjectsConfigApiException exception)
    {
        var status = exception.Code switch
        {
            "project_read_not_authorized" or "project_write_not_authorized" or
                "run_selection_not_authorized" or "authorization_context_denied" or
                "authorization_context_mismatch" or "authorization_changed" or
                "tenant_selector_mismatch" => StatusCodes.Status403Forbidden,
            "upstream_unavailable" or "upstream_timeout" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
    }
}
