using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Enforces GitHub organization (and optionally team) membership on every request.
/// Must run AFTER <see cref="ApiKeyAuthMiddleware"/> so the caller identity is already resolved.
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
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider _accessTokenProvider;
    private readonly ILogger<GitHubOrgAuthorizationMiddleware> _logger;

    // Paths that bypass the org/team check entirely.
    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/auth",
        "/api/auth",
        "/mcp",
    ];

    public GitHubOrgAuthorizationMiddleware(
        RequestDelegate next,
        IGitHubOrgAuthorizationService authzService,
        IGitHubTokenStore tokenStore,
        IGitHubTokenScopeProvider scopeProvider,
        IGitHubAccessTokenProvider accessTokenProvider,
        ILogger<GitHubOrgAuthorizationMiddleware> logger)
    {
        _next = next;
        _authzService = authzService;
        _tokenStore = tokenStore;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (IsExempt(path))
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

        // Resolve the caller. ApiKeyAuthMiddleware sets this for /api/* paths; for any other
        // non-exempt path there is no caller context → treat as unauthenticated.
        var caller = context.Items["agentweaver.caller"] as CallerContext;
        if (caller is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        // Obtain a valid access token for the caller's scope.
        var scope = _scopeProvider.Resolve(caller.User);
        string? accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetValidAccessTokenAsync(scope, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch
        {
            accessToken = null;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        // Resolve the GitHub login. CallerContext already has it if the token store had it; fall
        // back to querying the identity store directly (rare: e.g. cache miss at startup).
        var login = caller.GitHubLogin;
        if (string.IsNullOrWhiteSpace(login))
        {
            try
            {
                var identity = await _tokenStore.GetIdentityAsync(scope, context.RequestAborted)
                    .ConfigureAwait(false);
                login = identity?.Login;
            }
            catch { /* best-effort */ }
        }

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
            // StartsWithSegments requires the char after the match to be '/' or end-of-string,
            // so "/auth" correctly matches "/auth", "/auth/github/authorize", etc.
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
