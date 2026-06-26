namespace Agentweaver.Mcp;

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
    private readonly McpAccessTokenValidator _tokenValidator;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpBearerTokenMiddleware> _logger;

    public McpBearerTokenMiddleware(
        RequestDelegate next,
        McpApiKeyRegistry registry,
        McpAccessTokenValidator tokenValidator,
        IConfiguration configuration,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<McpBearerTokenMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _tokenValidator = tokenValidator;
        _configuration = configuration;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health probe and the unauthenticated RFC 9728 resource-metadata documents — no auth required.
        if (context.Request.Path.StartsWithSegments("/healthz")
            || context.Request.Path.StartsWithSegments("/.well-known/oauth-protected-resource"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // No token supplied — emit the discovery challenge without an error code (spec-acceptable).
            await WriteUnauthorizedAsync(context, includeError: false).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefix.Length..].Trim();

        // Fast path: Agentweaver API key (in-memory lookup, O(1)). Tried FIRST for CI/automation.
        // Store both the resolved user and the raw token so AgentweaverApiClient can propagate it.
        if (_registry.TryResolveUser(token, out var apiUser))
        {
            context.Items[UserItemKey] = apiUser;
            context.Items["mcp.bearer_token"] = token;
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Second path: Agentweaver-minted OAuth access token (signed JWT). Validated OFFLINE via the
        // AS's cached JWKS (iss/aud/exp/RS256). The validated JWT is forwarded to the API, which
        // performs the authoritative jti-denylist check and per-user org enforcement (T7).
        var oauthIdentity = await _tokenValidator.ValidateAsync(token, context, context.RequestAborted)
            .ConfigureAwait(false);
        if (oauthIdentity is not null)
        {
            context.Items[UserItemKey] = oauthIdentity.GitHubLogin;
            context.Items["mcp.bearer_token"] = token;
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Third path (transitional, gated): raw GitHub OAuth token, cached for 5 minutes. Disabled by
        // setting Auth:Mcp:AllowGitHubPassthrough=false once all clients have migrated to the AS flow.
        if (AllowGitHubPassthrough())
        {
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

            if (gitHubLogin is not null)
            {
                context.Items[UserItemKey] = gitHubLogin;
                // Store the raw token so AgentweaverApiClient forwards it as the caller's identity.
                context.Items["mcp.bearer_token"] = token;
                await _next(context).ConfigureAwait(false);
                return;
            }
        }

        // A token was supplied but failed every path — invalid_token challenge.
        await WriteUnauthorizedAsync(context, includeError: true).ConfigureAwait(false);
    }

    /// <summary>Whether the transitional raw-GitHub-token path is enabled. Defaults to <c>true</c>.</summary>
    private bool AllowGitHubPassthrough()
    {
        var flag = _configuration["Auth:Mcp:AllowGitHubPassthrough"];
        return string.IsNullOrWhiteSpace(flag) || !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);
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

    private async Task WriteUnauthorizedAsync(HttpContext context, bool includeError)
    {
        // RFC 9728 §5.1 — advertise the resource-metadata URL so MCP clients can discover the AS.
        var issuer = ResolveIssuer(context);
        var resourceMetadata = $"{issuer}/.well-known/oauth-protected-resource";
        var challenge = includeError
            ? $"Bearer realm=\"agentweaver-mcp\", error=\"invalid_token\", resource_metadata=\"{resourceMetadata}\""
            : $"Bearer realm=\"agentweaver-mcp\", resource_metadata=\"{resourceMetadata}\"";

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = challenge;
        context.Response.ContentType = "application/json";
        await context.Response
            .WriteAsync("{\"error\":\"invalid_token\",\"error_description\":\"Bearer token required\"}")
            .ConfigureAwait(false);
    }

    /// <summary>Issuer = configured <c>Auth:Mcp:Issuer</c>, else derived from the incoming request host.</summary>
    private string ResolveIssuer(HttpContext context)
    {
        var configured = _configuration["Auth:Mcp:Issuer"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        var request = context.Request;
        return $"{request.Scheme}://{request.Host.Value}";
    }
}
