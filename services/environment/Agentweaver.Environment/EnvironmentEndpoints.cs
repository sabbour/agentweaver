using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Environment;

public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
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
            ApplyEnvironmentEgressRequest request,
            HttpContext context,
            EnvironmentEgressManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();
            try
            {
                return ToHttpResult(await manager.VerifyAsync(
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

        MapRuntimePlacement(readiness: false);
        MapRuntimePlacement(readiness: true);

        void MapRuntimePlacement(bool readiness) => endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}" +
                (readiness ? "/readiness" : ""),
            async (
                string projectId,
                string runId,
                string environmentId,
                string sessionId,
                string profileId,
                HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimePlacementReader reader,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeBootstrapProfileRegistry profiles,
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
                    if (readiness)
                    {
                        var observed = await reader.GetReadinessContextAsync(
                            actor, projectId, runId, sessionId, environmentId, profileId, profiles, cancellationToken)
                            .ConfigureAwait(false);
                        return observed is null ? Results.NotFound() : Results.Ok(observed);
                    }
                    var bootstrap = await reader.GetBootstrapContextAsync(
                        actor, projectId, runId, sessionId, environmentId, profileId, profiles, cancellationToken)
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
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
                Results.Ok(await manager.CreateAsync(
                    caller!, projectId, runId, environmentId, volumeId, request, cancellationToken)
                    .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        });
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "provision",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, token) =>
                manager.ProvisionAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "replace",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, token) =>
                manager.ReplaceAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "bind",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, token) =>
                manager.BindVolumeAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "unbind",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, token) =>
                manager.UnbindAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
        MapWorkspaceVolumeTransition(
            workspaceVolumes, "release",
            static (manager, caller, projectId, runId, environmentId, volumeId, request, token) =>
                manager.ReleaseAsync(
                    caller, projectId, runId, environmentId, volumeId, request, token));
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
            return await ExecuteSandboxApiAsync(async () =>
            {
                var result = await manager.ProvisionAsync(
                    caller!, projectId, runId, environmentId, request, cancellationToken).ConfigureAwait(false);
                return ToSandboxResult(result);
            }, cancellationToken).ConfigureAwait(false);
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
            WorkspaceVolumeApiTransitionRequest, CancellationToken, Task<WorkspaceVolumeLifecycleResult>> execute)
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
            return await ExecuteWorkspaceVolumeApiAsync(async () =>
            {
                var result = await execute(
                    manager, caller!, projectId, runId, environmentId, volumeId, request, cancellationToken)
                    .ConfigureAwait(false);
                return ToWorkspaceVolumeTransitionResult(result);
            }, cancellationToken).ConfigureAwait(false);
        });
    }

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
