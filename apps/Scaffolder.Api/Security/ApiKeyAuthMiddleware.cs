namespace Scaffolder.Api.Security;

/// <summary>
/// Authenticated caller resolved from the bearer API key. Set on the request by
/// <see cref="ApiKeyAuthMiddleware"/> and read by the run endpoints to enforce
/// per-run ownership.
/// </summary>
public sealed class CallerContext
{
    public required string User { get; init; }
}

/// <summary>
/// Validates the bearer API key on every request and attaches the resolved
/// caller identity. Requests without a valid key are rejected with 401 before
/// any run logic runs (Principles III, XI). Non-API routes are allowed through
/// so the root health endpoint stays reachable.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private const string CallerItemKey = "scaffolder.caller";

    private readonly RequestDelegate _next;
    private readonly ApiKeyRegistry _registry;

    public ApiKeyAuthMiddleware(RequestDelegate next, ApiKeyRegistry registry)
    {
        _next = next;
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
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

        context.Items[CallerItemKey] = new CallerContext { User = user };
        await _next(context).ConfigureAwait(false);
    }

    public static CallerContext GetCaller(HttpContext context) =>
        (CallerContext)context.Items[CallerItemKey]!;

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"unauthorized\"}").ConfigureAwait(false);
    }
}
