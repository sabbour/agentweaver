using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Agentweaver.Gateway;

public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();
        endpoints.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }))
            .AllowAnonymous();
        endpoints.MapGet(
                "/openapi/v1.json",
                () => Results.Json(GatewayOpenApi.CreateDocument(), GatewayOpenApi.JsonOptions))
            .AllowAnonymous()
            .WithName("getGatewayOpenApiV1");

        foreach (var route in GatewayRouteCatalog.Routes)
        {
            RouteHandlerBuilder endpoint;
            if (route.IsRunEventStream)
            {
                endpoint = endpoints.MapGet(
                    route.PublicPath,
                    (string projectId, string runId, HttpContext context,
                        GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                        owner.SubscribeRunEventsAsync(
                            context, projectId, runId, cancellationToken));
            }
            else
            {
                endpoint = endpoints.MapMethods(
                    route.PublicPath,
                    [route.Method],
                    (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                        owner.ProxyAsync(context, route, cancellationToken));
            }

            endpoint.RequireAuthorization()
                .WithName(route.OperationId)
                .WithSummary(route.Summary)
                .WithDescription(
                    $"Delegates to the {route.Owner} owner API without changing its response status or JSON body. " +
                    (route.AcceptsOnly
                        ? "An accepted response means the owner accepted work; it does not mean that work completed."
                        : "The owner remains authoritative for request validation and current resource authorization."));
        }

        endpoints.MapFallback(
                "/api/v1/{**path}",
                () => Results.Problem(
                    title: "route_not_found",
                    statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?> { ["code"] = "route_not_found" }))
            .AllowAnonymous();
        return endpoints;
    }
}

public sealed class GatewayAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Challenged && !authorizeResult.Forbidden)
        {
            await _fallback.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        var statusCode = authorizeResult.Challenged
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        var code = authorizeResult.Challenged ? "unauthenticated" : "forbidden";
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (authorizeResult.Challenged)
            context.Response.Headers.WWWAuthenticate = "Bearer";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = "about:blank",
                title = code,
                status = statusCode,
                code,
            },
            JsonOptions,
            context.RequestAborted).ConfigureAwait(false);
    }
}
