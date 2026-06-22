namespace Agentweaver.Api.Security;

using Agentweaver.Domain;

/// <summary>
/// Authenticated caller resolved from the bearer API key. Set on the request by
/// <see cref="ApiKeyAuthMiddleware"/> and read by the run endpoints to enforce
/// per-run ownership.
/// </summary>
public sealed class CallerContext
{
    public required string User { get; init; }

    /// <summary>
    /// The signed-in GitHub login resolved for this caller's token scope, or null when signed out /
    /// unavailable. A run is owned by the caller when its <c>SubmittingUser</c> matches EITHER the
    /// API-key principal (<see cref="User"/>) OR this GitHub login. This is what lets a backlog-pickup
    /// run — whose <c>SubmittingUser</c> is the captured GitHub login (e.g. "sabbour"), not the
    /// API-key principal (e.g. "local-developer") — remain viewable by the signed-in human.
    /// </summary>
    public string? GitHubLogin { get; init; }

    /// <summary>
    /// True when this caller owns a resource attributed to <paramref name="ownerUser"/>: it matches
    /// the API-key principal or the signed-in GitHub login (Ordinal, null-safe).
    /// </summary>
    public bool Owns(string? ownerUser) =>
        ownerUser is not null &&
        (string.Equals(User, ownerUser, StringComparison.Ordinal) ||
         (GitHubLogin is not null && string.Equals(GitHubLogin, ownerUser, StringComparison.Ordinal)));
}

/// <summary>
/// Validates the bearer API key on every request and attaches the resolved
/// caller identity. Requests without a valid key are rejected with 401 before
/// any run logic runs (Principles III, XI). Non-API routes are allowed through
/// so the root health endpoint stays reachable.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private const string CallerItemKey = "agentweaver.caller";

    private readonly RequestDelegate _next;
    private readonly ApiKeyRegistry _registry;

    public ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyRegistry registry)
    {
        _next = next;
        _registry = registry;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IGitHubTokenStore tokenStore,
        IGitHubTokenScopeProvider scopeProvider)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var token = header[scheme.Length..].Trim();
        if (!_registry.TryResolveUser(token, out var user))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        // Enrich the caller with the signed-in GitHub login once per request so the owner check stays
        // a cheap, synchronous string compare. Identity resolution reads from local storage (file / OS
        // credential store / in-memory) — never the network — and is wrapped so a failure can never
        // block or fail the request (the login simply stays null).
        var gitHubLogin = await TryResolveGitHubLoginAsync(tokenStore, scopeProvider, user, context.RequestAborted)
            .ConfigureAwait(false);

        context.Items[CallerItemKey] = new CallerContext { User = user, GitHubLogin = gitHubLogin };
        await _next(context).ConfigureAwait(false);
    }

    public static CallerContext GetCaller(HttpContext context) =>
        (CallerContext)context.Items[CallerItemKey]!;

    private static async Task<string?> TryResolveGitHubLoginAsync(
        IGitHubTokenStore tokenStore, IGitHubTokenScopeProvider scopeProvider, string user, CancellationToken ct)
    {
        try
        {
            var scope = scopeProvider.Resolve(user);
            var entry = await tokenStore.GetAsync(scope, ct).ConfigureAwait(false);
            if (entry.Status != GitHubTokenStatus.SignedIn) return null;
            return (await tokenStore.GetIdentityAsync(scope, ct).ConfigureAwait(false))?.Login;
        }
        catch
        {
            // Ownership enrichment is best-effort: never block the request on identity resolution.
            return null;
        }
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"unauthorized\"}").ConfigureAwait(false);
    }
}
