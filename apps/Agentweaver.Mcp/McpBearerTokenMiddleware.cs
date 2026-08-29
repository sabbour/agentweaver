namespace Agentweaver.Mcp;

/// <summary>
/// Protects the hosted MCP endpoint with Microsoft Entra bearer-token authentication.
/// The validated bearer is forwarded to the API, which remains the authorization authority.
/// </summary>
public sealed class McpBearerTokenMiddleware
{
    private const string SchemePrefix = "Bearer ";
    private const string UserItemKey = "mcp.user";
    private readonly RequestDelegate _next;
    private readonly McpEntraAccessTokenValidator _entraTokenValidator;

    public McpBearerTokenMiddleware(
        RequestDelegate next,
        McpEntraAccessTokenValidator entraTokenValidator)
    {
        _next = next;
        _entraTokenValidator = entraTokenValidator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/healthz"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context, includeError: false).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefix.Length..].Trim();
        var entraIdentity = await _entraTokenValidator.ValidateAsync(token, context.RequestAborted)
            .ConfigureAwait(false);
        if (entraIdentity is null)
        {
            await WriteUnauthorizedAsync(context, includeError: true).ConfigureAwait(false);
            return;
        }

        context.Items[UserItemKey] = entraIdentity.ObjectId;
        context.Items["mcp.bearer_token"] = token;
        await _next(context).ConfigureAwait(false);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, bool includeError)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = includeError
            ? "Bearer error=\"invalid_token\""
            : "Bearer";
        context.Response.ContentType = "application/json";
        await context.Response
            .WriteAsync("{\"error\":\"invalid_token\",\"error_description\":\"Microsoft Entra access token required\"}")
            .ConfigureAwait(false);
    }
}
