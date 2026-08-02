namespace Agentweaver.Api.Security;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Authenticated caller attached to the request after bearer-token validation.
/// </summary>
public sealed class CallerContext
{
    public required string User { get; init; }
    public string? EntraObjectId { get; init; }
    public string? EntraTenantId { get; init; }
    public IReadOnlyList<string> PlatformRoles { get; init; } = [];
    public string? PrimaryPlatformRole { get; init; }
    public string? GitHubLogin { get; init; }
    public bool IsOAuthJwt { get; init; }
    public string? Org { get; init; }

    public bool Owns(string? ownerUser) =>
        ownerUser is not null &&
        (string.Equals(User, ownerUser, StringComparison.Ordinal) ||
         (GitHubLogin is not null && string.Equals(GitHubLogin, ownerUser, StringComparison.Ordinal)));
}

/// <summary>
/// Deployment-mode-aware request authentication middleware.
/// - Entra mode: validates Microsoft Entra bearer JWTs on every request.
/// - GitHubLegacy mode: preserves the existing GitHub / MCP bearer-token behavior.
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
    private readonly EntraAccessTokenValidator _entraTokenValidator;
    private readonly AuthModeEpochService _authModeEpochService;
    private readonly ILogger<GitHubTokenAuthMiddleware> _logger;
    private readonly bool _bypassForTests;
    private readonly AuthMode _authMode;
    private readonly Dictionary<string, string> _testApiKeyMap;

    public GitHubTokenAuthMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        EntraAccessTokenValidator entraTokenValidator,
        AuthModeEpochService authModeEpochService,
        McpTokenService tokenService,
        ILogger<GitHubTokenAuthMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _configuration = configuration;
        _entraTokenValidator = entraTokenValidator;
        _authModeEpochService = authModeEpochService;
        _logger = logger;
        _authMode = AuthModeResolver.Resolve(configuration);

        var bypassConfigured = configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth");
        _bypassForTests = environment.IsDevelopment() && bypassConfigured;

        if (_bypassForTests)
        {
            _logger.LogCritical(
                "GitHub token authentication BYPASS is ACTIVE (Testing:BypassGitHubTokenAuth=true, environment={Environment}).",
                environment.EnvironmentName);
        }
        else if (bypassConfigured)
        {
            _logger.LogCritical(
                "Testing:BypassGitHubTokenAuth=true was configured but IGNORED because the environment is '{Environment}'.",
                environment.EnvironmentName);
        }

        _testApiKeyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_bypassForTests)
        {
            var singleKey = configuration["Auth:ApiKey"];
            var singleUser = configuration["Auth:User"];
            if (!string.IsNullOrWhiteSpace(singleKey) && !string.IsNullOrWhiteSpace(singleUser))
                _testApiKeyMap[singleKey] = singleUser;

            foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
            {
                var token = entry["Token"];
                var user = entry["User"];
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
            || context.Request.Path.Equals("/api/version", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/api/auth/session/exchange", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/api/auth/config", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/api/server/info", StringComparison.OrdinalIgnoreCase)
            || (context.Request.Path.StartsWithSegments("/api/projects", StringComparison.OrdinalIgnoreCase)
                && context.Request.Path.Value?.EndsWith("/webhooks/github", StringComparison.OrdinalIgnoreCase) == true))
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

        if (!await _authModeEpochService.IsCurrentInstanceActiveAsync(context.RequestAborted).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Rejecting authenticated request for {Path} because this instance is on a stale auth mode epoch.",
                context.Request.Path);
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        if (_bypassForTests)
        {
            var bypassToken = header[SchemePrefixStr.Length..].Trim();
            var resolvedUser = _testApiKeyMap.TryGetValue(bypassToken, out var user) ? user : bypassToken;
            var githubLogin = await ResolveSignedInGitHubLoginAsync(context, resolvedUser).ConfigureAwait(false);
            var bypassCaller = new CallerContext { User = resolvedUser, GitHubLogin = githubLogin };
            SetCaller(context, bypassCaller, BuildClaimsPrincipal(bypassCaller));
            await _next(context).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefixStr.Length..].Trim();

        var internalKey = _configuration["Auth:ApiKey"];
        if (!string.IsNullOrEmpty(internalKey) && token == internalKey)
        {
            var allowedOrg = GitHubOrgList.ParseEntities(_configuration["Auth:GitHub:AllowedOrg"]).FirstOrDefault()?.RuleString;
            var internalCaller = new CallerContext
            {
                User = "agentweaver-internal",
                GitHubLogin = "agentweaver-internal",
                IsOAuthJwt = _authMode == AuthMode.GitHubLegacy,
                Org = _authMode == AuthMode.GitHubLegacy ? allowedOrg : null,
                PlatformRoles = _authMode == AuthMode.Entra ? [PlatformRoles.PlatformAdmin] : [],
                PrimaryPlatformRole = _authMode == AuthMode.Entra ? PlatformRoles.PlatformAdmin : null,
            };
            SetCaller(context, internalCaller, BuildClaimsPrincipal(internalCaller, isInternal: true));
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_authMode == AuthMode.Entra)
        {
            var entraClaims = await _entraTokenValidator.ValidateAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (entraClaims is null)
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            var caller = new CallerContext
            {
                User = entraClaims.ObjectId,
                EntraObjectId = entraClaims.ObjectId,
                EntraTenantId = entraClaims.TenantId,
                PlatformRoles = entraClaims.RecognizedRoles,
                PrimaryPlatformRole = entraClaims.PrimaryRole,
            };
            SetCaller(context, caller, BuildClaimsPrincipal(caller, displayName: entraClaims.DisplayName));
            await _next(context).ConfigureAwait(false);
            return;
        }

        var issuer = OAuthServerConfig.ResolveIssuer(context, _configuration);
        var audience = OAuthServerConfig.ResolveAudience(issuer, _configuration);
        if (_tokenService.TryValidateAccessToken(token, issuer, audience, out var oauthClaims) && oauthClaims is not null)
        {
            var refreshStore = context.RequestServices.GetRequiredService<McpRefreshTokenStore>();
            if (await refreshStore.IsJtiDeniedAsync(oauthClaims.Jti, context.RequestAborted).ConfigureAwait(false))
            {
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            var oauthCaller = new CallerContext
            {
                User = oauthClaims.Subject,
                GitHubLogin = oauthClaims.GitHubLogin,
                IsOAuthJwt = true,
                Org = oauthClaims.Org,
            };
            SetCaller(context, oauthCaller, BuildClaimsPrincipal(oauthCaller));
            await _next(context).ConfigureAwait(false);
            return;
        }

        var cacheKey = ComputeTokenHash(token);
        if (!_cache.TryGetValue(cacheKey, out string? login))
        {
            login = await ValidateGitHubTokenAsync(token, context.RequestAborted).ConfigureAwait(false);
            _cache.Set(cacheKey, login, login is not null ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
        }

        if (login is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var gitHubCaller = new CallerContext { User = login, GitHubLogin = login };
        SetCaller(context, gitHubCaller, BuildClaimsPrincipal(gitHubCaller));
        await _next(context).ConfigureAwait(false);
    }

    public static CallerContext GetCaller(HttpContext context) =>
        (CallerContext)context.Items[CallerItemKey]!;

    private static async Task<string?> ResolveSignedInGitHubLoginAsync(HttpContext context, string resolvedUser)
    {
        var tokenStore = context.RequestServices.GetService<IGitHubTokenStore>();
        var scopeProvider = context.RequestServices.GetService<IGitHubTokenScopeProvider>();
        if (tokenStore is null || scopeProvider is null)
            return null;

        var scope = scopeProvider.Resolve(resolvedUser);
        var entry = await tokenStore.GetAsync(scope, context.RequestAborted).ConfigureAwait(false);
        if (entry.Status != GitHubTokenStatus.SignedIn)
            return null;

        return (await tokenStore.GetIdentityAsync(scope, context.RequestAborted).ConfigureAwait(false))?.Login;
    }

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

    private static void SetCaller(HttpContext context, CallerContext caller, ClaimsPrincipal principal)
    {
        context.Items[CallerItemKey] = caller;
        context.User = principal;
    }

    private static ClaimsPrincipal BuildClaimsPrincipal(
        CallerContext caller,
        bool isInternal = false,
        string? displayName = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, caller.User),
            new("auth_mode", caller.EntraObjectId is not null ? AuthMode.Entra.ToString() : AuthMode.GitHubLegacy.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(displayName))
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        if (!string.IsNullOrWhiteSpace(caller.GitHubLogin))
            claims.Add(new Claim("gh_login", caller.GitHubLogin));
        if (!string.IsNullOrWhiteSpace(caller.EntraObjectId))
            claims.Add(new Claim("oid", caller.EntraObjectId));
        if (!string.IsNullOrWhiteSpace(caller.EntraTenantId))
            claims.Add(new Claim("tid", caller.EntraTenantId));
        foreach (var role in caller.PlatformRoles)
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (isInternal)
            claims.Add(new Claim("agentweaver_internal", "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Agentweaver"));
    }
}

public static class ApiKeyAuthMiddleware
{
    public static CallerContext GetCaller(HttpContext context) =>
        GitHubTokenAuthMiddleware.GetCaller(context);
}
