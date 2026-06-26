using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Enforces GitHub organization (and optionally team) membership on every request.
/// Must run AFTER <see cref="GitHubTokenAuthMiddleware"/> so the caller identity is already resolved.
/// The caller's own GitHub token (validated by the preceding middleware) is extracted from the
/// Authorization header and used directly for the org membership check.
///
/// Exempt paths: /health, /auth/*, /api/auth/*, /mcp*
///
/// Fail-closed behaviour: if Auth:GitHub:AllowedOrg is not set at all, every non-exempt
/// request is blocked with 403 so the deployment is never accidentally open.
/// </summary>
public sealed class GitHubOrgAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IGitHubOrgAuthorizationService _authzService;
    private readonly ILogger<GitHubOrgAuthorizationMiddleware> _logger;
    private readonly bool _bypassForTests;

    // Paths that bypass the org/team check entirely.
    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/api/health",
        "/api/ping",
        "/auth",
        "/api/auth",
        "/mcp",
        // MCP OAuth 2.1 Authorization Server: public discovery + public-client flow must be
        // reachable without a GitHub token (the flow is how a token is obtained in the first place).
        "/oauth",
        "/.well-known",
    ];

    public GitHubOrgAuthorizationMiddleware(
        RequestDelegate next,
        IGitHubOrgAuthorizationService authzService,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<GitHubOrgAuthorizationMiddleware> logger)
    {
        _next = next;
        _authzService = authzService;
        _logger = logger;

        // F1: org-authorization bypass is honored ONLY in Development. In any other environment the
        // flag is ignored so org membership enforcement cannot be silently disabled in production via
        // an injected env var. TestingBypassGuard hard-fails the process if it is set under Production.
        var bypassConfigured = configuration.GetValue<bool>("Testing:BypassGitHubOrgAuthorization");
        _bypassForTests = environment.IsDevelopment() && bypassConfigured;

        if (_bypassForTests)
        {
            _logger.LogCritical(
                "GitHub org authorization BYPASS is ACTIVE (Testing:BypassGitHubOrgAuthorization=true, " +
                "environment={Environment}). Org/team membership is NOT enforced. Development/test ONLY.",
                environment.EnvironmentName);
        }
        else if (bypassConfigured)
        {
            _logger.LogCritical(
                "Testing:BypassGitHubOrgAuthorization=true was configured but IGNORED because the " +
                "environment is '{Environment}' (not Development). Org authorization remains enforced.",
                environment.EnvironmentName);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (IsExempt(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_bypassForTests)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Fail closed: if org is not configured, no request may proceed.
        if (!_authzService.IsConfigured)
        {
            await WriteForbiddenAsync(context,
                "Authorization not configured. Set Auth:GitHub:AllowedOrg.").ConfigureAwait(false);
            return;
        }

        // Resolve the caller. GitHubTokenAuthMiddleware sets this for /api/* paths; for any other
        // non-exempt path there is no caller context → treat as unauthenticated.
        var caller = context.Items["agentweaver.caller"] as CallerContext;
        if (caller is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        // The caller's GitHub token is already validated by GitHubTokenAuthMiddleware.
        // Extract it directly from the Authorization header for the org membership check.
        var authHeader = context.Request.Headers.Authorization.ToString();
        const string schemePrefix = "Bearer ";
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var accessToken = authHeader[schemePrefix.Length..].Trim();
        var login = caller.GitHubLogin ?? caller.User;

        if (string.IsNullOrWhiteSpace(login))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var result = await _authzService.CheckMembershipAsync(accessToken, login, context.RequestAborted)
            .ConfigureAwait(false);

        switch (result)
        {
            case OrgAuthResult.Allowed:
                await _next(context).ConfigureAwait(false);
                return;

            case OrgAuthResult.OrgAccessNotGranted:
                await WriteForbiddenAsync(context,
                    "Could not verify membership of the required GitHub organization. " +
                    "Ensure your org membership is set to Public in GitHub org settings " +
                    "(the private membership endpoint is blocked by SAML SSO enforcement).").ConfigureAwait(false);
                return;

            case OrgAuthResult.NotConfigured:
                await WriteForbiddenAsync(context,
                    "Authorization not configured. Set Auth:GitHub:AllowedOrg.").ConfigureAwait(false);
                return;

            default: // Denied
                _logger.LogWarning(
                    "Access denied for GitHub login '{Login}': not a member of the required organization.",
                    login);
                await WriteForbiddenAsync(context,
                    "Access denied. Not a member of the required GitHub organization.").ConfigureAwait(false);
                return;
        }
    }

    private static bool IsExempt(PathString path)
    {
        foreach (var prefix in ExemptPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"Unauthorized. GitHub authentication required.\"}").ConfigureAwait(false);
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = message }).ConfigureAwait(false);
    }
}
