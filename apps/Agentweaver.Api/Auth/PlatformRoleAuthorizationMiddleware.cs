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
    private readonly IConfiguration _configuration;

    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/healthz",
        "/api/health",
        "/api/ping",
        "/api/version",
        // Public server metadata (data directory + configured auth mode) is what the web app
        // reads BEFORE sign-in to decide which sign-in button to render, so it must stay
        // anonymous in both middlewares.
        "/api/server/info",
        "/auth",
        "/api/auth/config",
        // The one-time-code session bootstrap is anonymous by design (the opaque code is the
        // credential) and runs BEFORE any platform role exists, so it must be exempt here exactly as
        // it is in the bearer-token auth middleware; otherwise Entra web sign-in cannot complete.
        "/api/auth/session/exchange",
        // A signed-in caller with zero platform roles must still be able to read their own
        // identity/roles back — otherwise they hit a 403 brick wall with no way to see what
        // Entra actually sent, and no way to self-diagnose or report the right details to an admin.
        "/api/auth/session",
        "/oauth",
        "/.well-known",
        "/openapi",
        "/mcp",
    ];

    public PlatformRoleAuthorizationMiddleware(
        RequestDelegate next,
        IAuthorizationService authorizationService,
        IConfiguration configuration)
    {
        _next = next;
        _authorizationService = authorizationService;
        _configuration = configuration;
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
            var rawRoles = context.User.FindAll("raw_role").Select(c => c.Value).ToArray();
            var clientId = _configuration["Auth:Entra:ClientId"];
            var tenantId = context.User.FindFirst("tid")?.Value ?? _configuration["Auth:Entra:TenantId"];
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access denied. A recognized Agentweaver platform role is required.",
                entra_object_id = context.User.FindFirst("oid")?.Value,
                entra_tenant_id = tenantId,
                entra_client_id = clientId,
                // What Microsoft Entra actually put on the token's `roles` claim (may be empty if
                // no App Role assignment exists at all, or non-empty-but-unrecognized if the wrong
                // role name was assigned). Empty means: grant an App Role assignment on this app
                // registration/service principal for this user or one of their groups.
                roles_found_on_token = rawRoles,
                recognized_platform_roles = PlatformRoles.All,
            }).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsExempt(PathString path)
    {
        if (path.Equals("/api/github/webhooks/repo-app", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var prefix in ExemptPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
