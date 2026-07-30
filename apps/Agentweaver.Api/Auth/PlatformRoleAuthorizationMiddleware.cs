using Microsoft.AspNetCore.Authorization;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Enforces platform-role presence on every API request when Auth:Mode=Entra.
/// GitHubLegacy deployments skip this middleware entirely.
/// </summary>
public sealed class PlatformRoleAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthorizationService _authorizationService;

    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/healthz",
        "/api/health",
        "/api/ping",
        "/api/version",
        "/auth",
        "/api/auth/config",
        "/oauth",
        "/.well-known",
        "/openapi",
        "/mcp",
    ];

    public PlatformRoleAuthorizationMiddleware(
        RequestDelegate next,
        IAuthorizationService authorizationService)
    {
        _next = next;
        _authorizationService = authorizationService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")
            || IsExempt(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var result = await _authorizationService
            .AuthorizeAsync(context.User, null, "PlatformAccess")
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access denied. A recognized Agentweaver platform role is required."
            }).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsExempt(PathString path)
    {
        if (path.StartsWithSegments("/api/projects", StringComparison.OrdinalIgnoreCase)
            && path.Value?.EndsWith("/webhooks/github", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        foreach (var prefix in ExemptPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
