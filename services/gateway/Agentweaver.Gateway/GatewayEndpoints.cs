using System.Text.Json;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

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
            .RequireCors(GatewayWebCors.OpenApiPolicyName)
            .WithName("getGatewayOpenApiV1");

        var repoApp = endpoints.MapGroup("/api/auth/github/repo-app").RequireAuthorization();
        repoApp.MapPost(
            "/authorizations",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", "/api/auth/github/repo-app/authorizations",
                    hasJsonBody: true,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        repoApp.MapGet(
            "/authorization/status",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "GET", "/api/auth/github/repo-app/authorization/status",
                    hasJsonBody: false,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        repoApp.MapGet(
            "/authorizations/{transactionId}",
            (string transactionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "GET",
                    "/api/auth/github/repo-app/authorizations/" + Uri.EscapeDataString(transactionId),
                    hasJsonBody: false,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        repoApp.MapPost(
            "/authorization/refresh",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", "/api/auth/github/repo-app/authorization/refresh",
                    hasJsonBody: false,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        repoApp.MapDelete(
            "/authorization",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "DELETE", "/api/auth/github/repo-app/authorization",
                    hasJsonBody: false,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));

        var repositorySelections = endpoints.MapGroup("/api/github/repository-selections")
            .RequireAuthorization();
        repositorySelections.MapGet(
            "",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "GET", "/api/github/repository-selections",
                    hasJsonBody: false,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        repositorySelections.MapPost(
            "",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", "/api/github/repository-selections",
                    hasJsonBody: true,
                    forwardTenantSelector: false,
                    forwardCookieName: null,
                    cookieErrorCode: "callback_cookie_invalid",
                    cancellationToken: cancellationToken));

        endpoints.MapGet(
                "/auth/github/repo-app/callback",
                (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                    owner.ProxyGitHubRepoAppCallbackAsync(
                        context,
                        "/auth/github/repo-app/callback",
                        "__Host-agentweaver-repo-app-auth",
                        "repo_app_callback_cookie_invalid",
                        ["code", "state", "error"],
                        cancellationToken))
            .AllowAnonymous();
        endpoints.MapGet(
                "/auth/github/repo-app/installation/callback",
                (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                    owner.ProxyGitHubRepoAppCallbackAsync(
                        context,
                        "/auth/github/repo-app/installation/callback",
                        "__Host-agentweaver-repo-app-install-auth",
                        "repo_app_installation_cookie_invalid",
                        ["installation_id", "setup_action", "state"],
                        cancellationToken))
            .AllowAnonymous();

        var copilotConnections = endpoints.MapGroup("/api/connections/copilot-user/v1")
            .RequireAuthorization()
            .RequireCors(GatewayWebCors.CopilotConnectionsPolicyName);
        copilotConnections.MapPost(
            "/begin",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyCopilotConnectionAsync(
                    context,
                    "POST",
                    "/internal/connections/copilot-user/begin",
                    forwardLinkCookie: false,
                    cancellationToken: cancellationToken));
        copilotConnections.MapPost(
            "/complete",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyCopilotConnectionAsync(
                    context,
                    "POST",
                    "/internal/connections/copilot-user/complete",
                    forwardLinkCookie: true,
                    cancellationToken: cancellationToken));
        copilotConnections.MapPost(
            "/refresh",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyCopilotConnectionAsync(
                    context,
                    "POST",
                    "/internal/connections/copilot-user/refresh",
                    forwardLinkCookie: false,
                    cancellationToken: cancellationToken));
        copilotConnections.MapPost(
            "/revoke",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyCopilotConnectionAsync(
                    context,
                    "POST",
                    "/internal/connections/copilot-user/revoke",
                    forwardLinkCookie: false,
                    cancellationToken: cancellationToken));
        copilotConnections.MapGet(
            "/{connectionId:guid}",
            (Guid connectionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyCopilotConnectionAsync(
                    context,
                    "GET",
                    $"/internal/connections/copilot-user/{connectionId:D}",
                    forwardLinkCookie: false,
                    cancellationToken: cancellationToken));

        var remoteMcpConnections = endpoints.MapGroup("/api/connections/remote-mcp/v1")
            .RequireAuthorization()
            .RequireCors(GatewayWebCors.CopilotConnectionsPolicyName);
        remoteMcpConnections.MapPost(
            "/register",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", "/internal/connections/remote-mcp/register",
                    hasJsonBody: true,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        remoteMcpConnections.MapGet(
            "/{connectionId:guid}",
            (Guid connectionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "GET", $"/internal/connections/remote-mcp/{connectionId:D}",
                    hasJsonBody: false,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        remoteMcpConnections.MapPost(
            "/{connectionId:guid}/consent",
            (Guid connectionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", $"/internal/connections/remote-mcp/{connectionId:D}/consent",
                    hasJsonBody: true,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        remoteMcpConnections.MapPost(
            "/{connectionId:guid}/refresh",
            (Guid connectionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", $"/internal/connections/remote-mcp/{connectionId:D}/refresh",
                    hasJsonBody: true,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        remoteMcpConnections.MapPost(
            "/callback",
            (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", "/internal/connections/remote-mcp/callback",
                    hasJsonBody: true,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));
        remoteMcpConnections.MapPost(
            "/{connectionId:guid}/disconnect",
            (Guid connectionId, HttpContext context, GatewayOwnerClient owner,
                CancellationToken cancellationToken) =>
                owner.ProxyIdentityBrokerApiAsync(
                    context, "POST", $"/internal/connections/remote-mcp/{connectionId:D}/disconnect",
                    hasJsonBody: true,
                    forwardTenantSelector: true,
                    forwardCookieName: null,
                    cookieErrorCode: "remote_mcp_callback_cookie_invalid",
                    cancellationToken: cancellationToken));

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
                endpoint = route.OperationId == "getAuthorizationContext"
                    ? endpoints.MapGet(
                        route.PublicPath,
                        (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                            owner.ProxyAuthorizationContextAsync(context, route, cancellationToken))
                    : endpoints.MapMethods(
                        route.PublicPath,
                        [route.Method],
                        (HttpContext context, GatewayOwnerClient owner, CancellationToken cancellationToken) =>
                            owner.ProxyAsync(context, route, cancellationToken));
            }

            if (route.OperationId == "importKnowledgeRecords")
                endpoint.WithMetadata(new RequestSizeLimitAttribute(1024 * 1024));

            endpoint.RequireAuthorization()
                .RequireCors(GatewayWebCors.PolicyName(route))
                .WithName(route.OperationId)
                .WithSummary(route.Summary)
                .WithDescription(
                    $"Delegates to the {route.Owner} owner API without changing its response status or JSON body. " +
                    (route.AcceptsOnly
                        ? "An accepted response means the owner accepted work; it does not mean that work completed."
                        : "The owner remains authoritative for request validation and current resource authorization."));
            if (route.MaximumRequestBodyBytes is { } maximumRequestBodyBytes)
                endpoint.WithMetadata(new RequestSizeLimitAttribute(maximumRequestBodyBytes));
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
