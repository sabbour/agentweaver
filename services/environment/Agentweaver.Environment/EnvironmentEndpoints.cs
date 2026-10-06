using System.Net.Http.Headers;
using Agentweaver.Abstractions;

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

        return endpoints;
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

    private static IResult ToProjectAuthorizationResult(ProjectsConfigApiException exception)
    {
        var status = exception.Code switch
        {
            "project_read_not_authorized" or "authorization_context_denied" or
                "authorization_context_mismatch" or "authorization_changed" or
                "tenant_selector_mismatch" => StatusCodes.Status403Forbidden,
            "upstream_unavailable" or "upstream_timeout" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
    }
}
