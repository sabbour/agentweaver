namespace Agentweaver.Mcp;

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

/// <summary>
/// ASP.NET Core middleware that protects the hosted MCP endpoint with bearer token auth.
/// Two token types are accepted:
///   1. Agentweaver API key  — validated in-memory via <see cref="McpApiKeyRegistry"/> (fast path).
///   2. GitHub OAuth token   — validated by calling GET https://api.github.com/user and caching
///                             the result in <see cref="IMemoryCache"/> for five minutes.
///
/// Requests without a valid token receive 401 with {"error":"Bearer token required"}.
/// The resolved user identity is stored in HttpContext.Items["mcp.user"].
/// </summary>
public sealed class McpBearerTokenMiddleware
{
    private const string SchemePrefix = "Bearer ";
    private const string UserItemKey = "mcp.user";

    private readonly RequestDelegate _next;
    private readonly McpApiKeyRegistry _registry;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpBearerTokenMiddleware> _logger;

    public McpBearerTokenMiddleware(
        RequestDelegate next,
        McpApiKeyRegistry registry,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<McpBearerTokenMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health probe — no authentication required.
        if (context.Request.Path.StartsWithSegments("/healthz"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefix.Length..].Trim();

        // Fast path: Agentweaver API key (in-memory lookup, O(1)).
        // Store both the resolved user and the raw token so AgentweaverApiClient can propagate it.
        if (_registry.TryResolveUser(token, out var apiUser))
        {
            context.Items[UserItemKey] = apiUser;
            context.Items["mcp.bearer_token"] = token;
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Slow path: GitHub OAuth token, cached for 5 minutes.
        var cacheKey = $"gh:{token}";
        if (!_cache.TryGetValue(cacheKey, out string? gitHubLogin))
        {
            gitHubLogin = await ValidateGitHubTokenAsync(token, context.RequestAborted)
                .ConfigureAwait(false);

            // Cache valid logins for 5 min; cache negative results briefly to limit GitHub API hammering.
            _cache.Set(
                cacheKey,
                gitHubLogin,
                gitHubLogin is not null ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
        }

        if (gitHubLogin is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        context.Items[UserItemKey] = gitHubLogin;
        // Store the raw token so AgentweaverApiClient forwards it to the backend as the caller's identity.
        context.Items["mcp.bearer_token"] = token;
        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Returns the resolved GitHub login, or null when the token is invalid/expired.</summary>
    private async Task<string?> ValidateGitHubTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("github");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("Agentweaver-MCP/1.0");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            return doc.RootElement.TryGetProperty("login", out var loginProp)
                ? loginProp.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub token validation failed");
            return null;
        }
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"Bearer token required\"}")
            .ConfigureAwait(false);
    }
}
