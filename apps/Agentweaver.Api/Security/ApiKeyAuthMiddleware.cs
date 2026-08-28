namespace Agentweaver.Api.Security;

using System.Security.Claims;
using Agentweaver.Api.Auth;

/// <summary>Authenticated caller attached to the request after Microsoft Entra token validation.</summary>
public sealed class CallerContext
{
    public required string User { get; init; }
    public string? EntraObjectId { get; init; }
    public string? EntraTenantId { get; init; }
    public IReadOnlyList<string> PlatformRoles { get; init; } = [];
    public IReadOnlyList<string> RawPlatformRoles { get; init; } = [];
    public string? PrimaryPlatformRole { get; init; }
    public string? GitHubLogin { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsOAuthJwt { get; init; }
    public string? Org { get; init; }

    public bool Owns(string? ownerUser) =>
        ownerUser is not null &&
        (string.Equals(User, ownerUser, StringComparison.Ordinal) ||
         (GitHubLogin is not null && string.Equals(GitHubLogin, ownerUser, StringComparison.Ordinal)));
}

/// <summary>
/// Deployment-mode-aware request authentication middleware.
/// The post-cutover API accepts Microsoft Entra bearer tokens only, apart from the explicit
/// internal service key and Development test bypass.
/// </summary>
public sealed class GitHubTokenAuthMiddleware
{
    internal const string CallerItemKey = "agentweaver.caller";
    private const string SchemePrefix = "Bearer ";
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly EntraAccessTokenValidator _entraTokenValidator;
    private readonly ILogger<GitHubTokenAuthMiddleware> _logger;
    private readonly bool _bypassForTests;
    private readonly Dictionary<string, string> _testApiKeyMap;

    public GitHubTokenAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IHostEnvironment environment,
        EntraAccessTokenValidator entraTokenValidator,
        ILogger<GitHubTokenAuthMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _entraTokenValidator = entraTokenValidator;
        _logger = logger;

        var bypassConfigured = configuration.GetValue<bool>("Testing:BypassGitHubTokenAuth");
        _bypassForTests = environment.IsDevelopment() && bypassConfigured;
        if (_bypassForTests)
            _logger.LogCritical("Test authentication bypass is active in {Environment}.", environment.EnvironmentName);
        else if (bypassConfigured)
            _logger.LogCritical("Testing:BypassGitHubTokenAuth is ignored outside Development.");

        _testApiKeyMap = new(StringComparer.Ordinal);
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
        if (!context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/version", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/auth/session/exchange", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/auth/config", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/server/info", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/github/webhooks/repo-app", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var token = header[SchemePrefix.Length..].Trim();
        if (_bypassForTests)
        {
            var resolvedUser = _testApiKeyMap.TryGetValue(token, out var user) ? user : token;
            var bypassCaller = new CallerContext
            {
                User = resolvedUser,
                EntraObjectId = resolvedUser,
                PlatformRoles = [PlatformRoles.PlatformAdmin],
                PrimaryPlatformRole = PlatformRoles.PlatformAdmin,
            };
            SetCaller(context, bypassCaller, BuildClaimsPrincipal(bypassCaller));
            await _next(context).ConfigureAwait(false);
            return;
        }

        var internalKey = _configuration["Auth:ApiKey"];
        if (!string.IsNullOrEmpty(internalKey) && token == internalKey)
        {
            var internalCaller = new CallerContext
            {
                User = "agentweaver-internal",
                GitHubLogin = "agentweaver-internal",
                PlatformRoles = [PlatformRoles.PlatformAdmin],
                PrimaryPlatformRole = PlatformRoles.PlatformAdmin,
            };
            SetCaller(context, internalCaller, BuildClaimsPrincipal(internalCaller, isInternal: true));
            await _next(context).ConfigureAwait(false);
            return;
        }

        var claims = await _entraTokenValidator.ValidateAsync(token, context.RequestAborted).ConfigureAwait(false);
        if (claims is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var caller = new CallerContext
        {
            User = claims.ObjectId,
            EntraObjectId = claims.ObjectId,
            EntraTenantId = claims.TenantId,
            PlatformRoles = claims.RecognizedRoles,
            RawPlatformRoles = claims.RawRoles,
            PrimaryPlatformRole = claims.PrimaryRole,
            DisplayName = claims.DisplayName,
            Email = claims.Email,
        };
        SetCaller(context, caller, BuildClaimsPrincipal(caller, displayName: claims.DisplayName));
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, caller.User), new("auth_mode", "Entra") };
        if (!string.IsNullOrWhiteSpace(displayName))
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        if (!string.IsNullOrWhiteSpace(caller.EntraObjectId))
            claims.Add(new Claim("oid", caller.EntraObjectId));
        if (!string.IsNullOrWhiteSpace(caller.EntraTenantId))
            claims.Add(new Claim("tid", caller.EntraTenantId));
        foreach (var role in caller.PlatformRoles)
            claims.Add(new Claim(ClaimTypes.Role, role));
        foreach (var rawRole in caller.RawPlatformRoles)
            claims.Add(new Claim("raw_role", rawRole));
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
