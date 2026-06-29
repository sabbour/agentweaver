namespace Agentweaver.Api.Security;

using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Api.Auth.OAuth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Authenticated caller resolved from the bearer GitHub token. Set on the request by
/// <see cref="GitHubTokenAuthMiddleware"/> and read by the run endpoints to enforce
/// per-run ownership.
/// </summary>
public sealed class CallerContext
{
    public required string User { get; init; }

    /// <summary>
    /// The signed-in GitHub login for this caller. A run is owned by the caller when its
    /// <c>SubmittingUser</c> matches EITHER <see cref="User"/> OR this GitHub login.
    /// </summary>
    public string? GitHubLogin { get; init; }

    /// <summary>
    /// True when this caller was authenticated from an Agentweaver-minted OAuth access token (T7),
    /// rather than a raw GitHub token or static API key. For these callers org membership was already
    /// enforced by the Authorization Server at token issuance (and re-checked on refresh), so the
    /// org-authorization middleware trusts <see cref="Org"/> instead of making a GitHub org call.
    /// </summary>
    public bool IsOAuthJwt { get; init; }

    /// <summary>The org claim carried by an Agentweaver OAuth access token (T7). Null for other callers.</summary>
    public string? Org { get; init; }

    /// <summary>
    /// True when this caller owns a resource attributed to <paramref name="ownerUser"/>: it matches
    /// the principal or the signed-in GitHub login (Ordinal, null-safe).
    /// </summary>
    public bool Owns(string? ownerUser) =>
        ownerUser is not null &&
        (string.Equals(User, ownerUser, StringComparison.Ordinal) ||
         (GitHubLogin is not null && string.Equals(GitHubLogin, ownerUser, StringComparison.Ordinal)));
}

/// <summary>
/// Validates a GitHub OAuth Bearer token on every API request and attaches the resolved
/// caller identity. The GitHub /user endpoint is called once and the result is cached for 5 minutes
/// (keyed by SHA-256 of the token). Non-API routes and health/ping paths are exempt.
///
/// Setting <c>Testing:BypassGitHubTokenAuth=true</c> in configuration skips the GitHub call and
/// maps the bearer token directly to a caller using the <c>Auth:ApiKey/User</c> + <c>Auth:Keys</c>
/// config (same shape as the API's Auth:Keys registry). For test harnesses only.
///
/// SECURITY (F1): the bypass is honored ONLY when <see cref="IHostEnvironment.IsDevelopment"/> is
/// true, so a stray <c>Testing__BypassGitHubTokenAuth=true</c> env var cannot disable GitHub token
/// validation in a Production deployment. A complementary startup guard
/// (<see cref="TestingBypassGuard"/>) fails the process fast if any bypass flag is set under
/// Production, so the flag can never silently be on in a shared/hosted environment.
/// </summary>
public sealed class GitHubTokenAuthMiddleware
{
    internal const string CallerItemKey = "agentweaver.caller";
    private const string SchemePrefixStr = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly McpTokenService _tokenService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitHubTokenAuthMiddleware> _logger;
    private readonly bool _bypassForTests;
    private readonly Dictionary<string, string> _testApiKeyMap;

    public GitHubTokenAuthMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        McpTokenService tokenService,
        ILogger<GitHubTokenAuthMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _configuration = configuration;
        _logger = logger;

        // F1: only honor the test bypass in Development. In any non-Development environment the flag
        // is ignored regardless of how it was injected (config file, env var, secret). The startup
        // guard (TestingBypassGuard) additionally hard-fails the process under Production.
        var bypassConfigured = configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth");
        _bypassForTests = environment.IsDevelopment() && bypassConfigured;

        if (_bypassForTests)
        {
            _logger.LogCritical(
                "GitHub token authentication BYPASS is ACTIVE (Testing:BypassGitHubTokenAuth=true, " +
                "environment={Environment}). All bearer tokens on /api/* are accepted without GitHub " +
                "validation. This is for local development/test ONLY and must never be enabled in a " +
                "shared or production deployment.",
                environment.EnvironmentName);
        }
        else if (bypassConfigured)
        {
            _logger.LogCritical(
                "Testing:BypassGitHubTokenAuth=true was configured but IGNORED because the environment " +
                "is '{Environment}' (not Development). GitHub token validation remains enforced.",
                environment.EnvironmentName);
        }

        _testApiKeyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_bypassForTests)
        {
            var singleKey  = configuration["Auth:ApiKey"];
            var singleUser = configuration["Auth:User"];
            if (!string.IsNullOrWhiteSpace(singleKey) && !string.IsNullOrWhiteSpace(singleUser))
                _testApiKeyMap[singleKey] = singleUser;

            foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
            {
                var token = entry["Token"];
                var user  = entry["User"];
                if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user))
                    _testApiKeyMap[token] = user;
            }
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            // Web sign-in bootstrap: the one-time code redemption is itself the credential
            // (endpoint is AllowAnonymous). It MUST be reachable without a Bearer token —
            // it is the call that EXCHANGES the code FOR the token. Without this exemption the
            // token middleware 401s it before the anonymous endpoint runs → sign-in loop.
            || context.Request.Path.Equals("/api/auth/session/exchange", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(SchemePrefixStr, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        // Test-only bypass: resolve caller from static config key map (no GitHub call).
        if (_bypassForTests)
        {
            var bypassToken = header[SchemePrefixStr.Length..].Trim();
            var resolvedUser = _testApiKeyMap.TryGetValue(bypassToken, out var u) ? u : bypassToken;
            context.Items[CallerItemKey] = new CallerContext { User = resolvedUser, GitHubLogin = resolvedUser };
            await _next(context).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefixStr.Length..].Trim();

        // T7: Agentweaver-minted OAuth access token (JWT). Validate offline against the signing key
        // (iss/aud/exp/RS256) so MCP→API calls carry the real per-user identity instead of collapsing
        // onto the shared service key (fixes the confused-deputy limitation). A revoked jti is rejected.
        var issuer = OAuthServerConfig.ResolveIssuer(context, _configuration);
        var audience = OAuthServerConfig.ResolveAudience(issuer, _configuration);
        if (_tokenService.TryValidateAccessToken(token, issuer, audience, out var claims) && claims is not null)
        {
            var refreshStore = context.RequestServices.GetRequiredService<McpRefreshTokenStore>();
            if (await refreshStore.IsJtiDeniedAsync(claims.Jti, context.RequestAborted).ConfigureAwait(false))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            context.Items[CallerItemKey] = new CallerContext
            {
                User = claims.Subject,
                GitHubLogin = claims.GitHubLogin,
                IsOAuthJwt = true,
                Org = claims.Org,
            };
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Internal service-to-service: agent loopback calls (RunWorkflowFactory, CoordinatorWorkflowFactory,
        // BacklogDecomposeService) use the shared API key (Auth:ApiKey = mcp-api-key from Key Vault) as a
        // Bearer token. The raw hex key is not a JWT and is not a GitHub token, so it must be validated
        // here before the GitHub /user call — which would always return 401 for a non-GitHub credential.
        // This path is production-safe: the key is a 32-byte random secret injected via CSI/Key Vault,
        // not a human credential. Callers authenticated this way are attributed as "agentweaver-internal".
        // IsOAuthJwt=true + Org=allowedOrg lets GitHubOrgAuthorizationMiddleware skip the GitHub org
        // membership call (which would always fail for a non-GitHub credential).
        var internalKey = _configuration["Auth:ApiKey"];
        if (!string.IsNullOrEmpty(internalKey) && token == internalKey)
        {
            var allowedOrg = _configuration["Auth:GitHub:AllowedOrg"]?.Trim();
            context.Items[CallerItemKey] = new CallerContext
            {
                User = "agentweaver-internal",
                GitHubLogin = "agentweaver-internal",
                IsOAuthJwt = true,
                Org = allowedOrg,
            };
            await _next(context).ConfigureAwait(false);
            return;
        }

        var cacheKey = ComputeTokenHash(token);

        if (!_cache.TryGetValue(cacheKey, out string? login))
        {
            login = await ValidateGitHubTokenAsync(token, context.RequestAborted).ConfigureAwait(false);
            _cache.Set(
                cacheKey,
                login,
                login is not null ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
        }

        if (login is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        context.Items[CallerItemKey] = new CallerContext { User = login, GitHubLogin = login };
        await _next(context).ConfigureAwait(false);
    }

    public static CallerContext GetCaller(HttpContext context) =>
        (CallerContext)context.Items[CallerItemKey]!;

    private async Task<string?> ValidateGitHubTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("github");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("login", out var loginProp) ? loginProp.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GitHub token validation failed");
            return null;
        }
    }

    private static string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"gh-token:{Convert.ToHexString(bytes)}";
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"unauthorized\"}").ConfigureAwait(false);
    }
}

/// <summary>
/// Backward-compatibility shim: exposes <see cref="GetCaller"/> so existing endpoint code that
/// references <c>ApiKeyAuthMiddleware.GetCaller(context)</c> continues to compile without changes.
/// </summary>
public static class ApiKeyAuthMiddleware
{
    public static CallerContext GetCaller(HttpContext context) =>
        GitHubTokenAuthMiddleware.GetCaller(context);
}
