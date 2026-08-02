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
    private readonly AuthMode _authMode;

    // Paths that bypass the org/team check entirely.
    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/healthz",
        "/api/health",
        "/api/ping",
        "/api/version",
        // Public server metadata (data directory + configured auth mode): the web app reads this
        // BEFORE sign-in to pick the right sign-in button, so it must never require a GitHub token.
        "/api/server/info",
        "/auth",
        "/api/auth",
        "/mcp",
        // MCP OAuth 2.1 Authorization Server: public discovery + public-client flow must be
        // reachable without a GitHub token (the flow is how a token is obtained in the first place).
        "/oauth",
        "/.well-known",
        // OpenAPI contract (spec-006 api-harness): pure endpoint/schema metadata, no live data —
        // the LLM-driven curl harness needs to fetch this before it has authenticated.
        "/openapi",
        // GitHub webhook receiver (issue #53 follow-up): GitHub's delivery has no Agentweaver bearer
        // token/org membership to check — the HMAC-SHA256 signature verification inside the endpoint
        // itself (GitHubWebhookEndpoints) IS this path's authentication.
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
        _authMode = AuthModeResolver.Resolve(configuration);

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
        if (_authMode != AuthMode.GitHubLegacy)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

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

        // T7: callers authenticated via an Agentweaver-minted OAuth access token had membership
        // enforced by the Authorization Server at token issuance (and re-checked on each refresh).
        // The token is audience-bound and signature-validated, so we trust its `org` claim — which
        // under the team-membership authz model carries the CANONICAL RULE STRING that was matched
        // at issuance (e.g. "azure/aks" for a team-scoped rule, or "microsoft" for a bare-org rule).
        // We re-check that rule string against the CURRENT allow-list on every request so a config
        // change (rule removed, or bare-org demoted to team-scoped) invalidates in-flight JWTs.
        if (caller.IsOAuthJwt)
        {
            var callerOrg = caller.Org;
            if (!string.IsNullOrWhiteSpace(callerOrg))
            {
                // Parse the claim back into an entity. A legacy JWT (minted before the
                // team-membership change) carries a plain org name, which parses as a bare-org
                // entity — it will only match bare-org entities in the current allow-list, never
                // a team-scoped entity. This prevents grandfathering a team-scoped rule with a
                // pre-existing bare-org JWT after a config demotion.
                var callerEntities = GitHubOrgList.ParseEntities(callerOrg);
                if (callerEntities.Count == 1)
                {
                    var claimed = callerEntities[0];
                    if (_authzService.AllowedEntities.Any(e => e.Matches(claimed)))
                    {
                        await _next(context).ConfigureAwait(false);
                        return;
                    }
                }
            }

            _logger.LogWarning(
                "Access denied for OAuth caller '{Login}': token rule claim '{Org}' does not match any current allow-rule.",
                caller.GitHubLogin ?? caller.User, caller.Org);
            await WriteForbiddenAsync(context,
                "Access denied. OAuth token is not scoped to any allowed GitHub organization/team.").ConfigureAwait(false);
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

            case OrgAuthResult.Inconclusive:
                // The authenticated GitHub org check could not complete (transient outage / token
                // problem). The caller's GitHub token already passed validation upstream, so this is a
                // transient condition — fail closed but report it as unavailable rather than a denial.
                _logger.LogWarning(
                    "Org membership check for '{Login}' was inconclusive (transient GitHub failure).",
                    login);
                await WriteForbiddenAsync(context,
                    "Could not verify GitHub organization membership at this time. Please retry.").ConfigureAwait(false);
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
