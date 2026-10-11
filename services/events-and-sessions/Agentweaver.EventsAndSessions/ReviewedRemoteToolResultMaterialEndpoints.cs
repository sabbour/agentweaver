using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.EventsAndSessions;

public static class ReviewedRemoteToolResultMaterialEndpoints
{
    public static IEndpointRouteBuilder MapReviewedRemoteToolResultMaterialEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/sessions/{sessionId}/remote-tool-results",
            Unavailable)
            .RequireAuthorization();
        endpoints.MapPost(
            "/internal/sessions/{sessionId}/remote-tool-results/read",
            Unavailable)
            .RequireAuthorization();
        return endpoints;
    }

    private static IResult Unavailable(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var error = context.Request.IsHttps
            ? "runtime_remote_tool_result_route_unavailable"
            : "runtime_remote_tool_result_https_required";
        return Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
