using Microsoft.AspNetCore.Authorization;

namespace Agentweaver.Environment;

/// <summary>Registers the internal provider lifecycle report route.</summary>
public static class EnvironmentProviderLifecycleReportEndpoints
{
    /// <summary>
    /// Maps a fail-closed endpoint while report ingestion is unavailable; it never accepts provider state as applied.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderLifecycleReportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/provider-lifecycle/reports", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new { code = "environment_provider_lifecycle_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization();
        return endpoints;
    }
}
