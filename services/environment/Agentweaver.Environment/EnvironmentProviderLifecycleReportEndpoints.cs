using Microsoft.AspNetCore.Authorization;

namespace Agentweaver.Environment;

public static class EnvironmentProviderLifecycleReportEndpoints
{
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
