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

        // SECURITY (multi-user / AKS): resolve the caller's OWN GitHub identity for the org check.
        // FixedInstallationScopeProvider.Resolve() ignores the userId and always returns the single
        // shared installation scope. Used unchanged, that lets ANY api-key holder pass the org check
        // as soon as a single user has signed in (impersonation across callers). To prevent that we
        // prefer the caller's own stored token scope and only fall back to the configured scope
        // provider (installation by default) when the caller has no token of their own.
        var scope = await ResolveCallerScopeAsync(caller.User, context.RequestAborted).ConfigureAwait(false);

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

        // Resolve the GitHub login from the SAME scope the token came from, so the org check always
        // runs against the identity that owns this token — never a caller hint that may have been
        // computed under a different (e.g. installation) scope. Only fall back to the cached caller
        // hint when the scope's identity is genuinely unavailable.
        string? login;
        try
        {
            var identity = await _tokenStore.GetIdentityAsync(scope, context.RequestAborted)
                .ConfigureAwait(false);
            login = identity?.Login;
        }
        catch
        {
            login = null;
        }

        if (string.IsNullOrWhiteSpace(login))
            login = caller.GitHubLogin;

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

    /// <summary>
    /// Resolves the token scope used for THIS caller's org check. Prefers the caller's own stored
    /// GitHub token (scope <c>user:{callerUser}</c>) so each api-key holder is verified against their
    /// OWN GitHub identity rather than a single shared installation identity. Falls back to the
    /// configured scope provider (installation by default) only when the caller has no token of their
    /// own (e.g. they authenticate with an API key but never signed in to GitHub).
    ///
    /// NOTE (defense-in-depth limitation): the fallback path still relies on the shared installation
    /// identity, so an api-key-only caller can pass the org check using whichever identity is signed
    /// in at the installation scope. Eliminating that requires every caller to hold their own GitHub
    /// token (Auth:GitHub:ScopeProvider = "caller") or a hard policy that rejects api-key-only callers.
    /// TODO(github.com/Agentweaver/agentweaver/issues): require per-caller GitHub identity in multi-user
    /// deployments and drop the installation fallback for org authorization.
    /// </summary>
    private async Task<GitHubTokenScope> ResolveCallerScopeAsync(string callerUser, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(callerUser))
        {
            try
            {
                var userScope = GitHubTokenScope.ForUser(callerUser);
                var ownToken = await _tokenStore.GetTokenAsync(userScope, ct).ConfigureAwait(false);
                if (ownToken is not null)
                    return userScope;
            }
            catch
            {
                // Fall through to the configured provider on any lookup failure.
            }
        }

        return _scopeProvider.Resolve(callerUser);
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
