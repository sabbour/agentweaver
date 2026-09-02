namespace Agentweaver.Api.Security;

using System.Security.Claims;
using Agentweaver.AgentRuntime;
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
    public string? AuthenticationScheme { get; init; }
    public bool IsOAuthJwt =>
        string.Equals(AuthenticationScheme, AgentweaverAuthenticationSchemes.McpOAuth, StringComparison.Ordinal);
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
    private readonly Dictionary<string, TestCaller> _testApiKeyMap;

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
                _testApiKeyMap[singleKey] = new TestCaller(
                    singleUser,
                    ReadTestPlatformRoles(configuration["Auth:PlatformRoles"], [PlatformRoles.PlatformAdmin]));

            foreach (var entry in configuration.GetSection("Auth:Keys").GetChildren())
            {
                var token = entry["Token"];
                var user = entry["User"];
                if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user))
                    _testApiKeyMap[token] = new TestCaller(
                        user,
                        ReadTestPlatformRoles(entry["PlatformRoles"], [PlatformRoles.PlatformAdmin]));
            }
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpointAuthorization = context.GetEndpoint()?.Metadata.GetMetadata<EndpointAuthorizationMetadata>();
        if (endpointAuthorization is { RequiresBearerAuthentication: false }
            || (endpointAuthorization is null && !context.Request.Path.StartsWithSegments("/api")))
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
            var testCaller = _testApiKeyMap.TryGetValue(token, out var configuredCaller)
                ? configuredCaller
                : new TestCaller(token, []);
            var bypassCaller = new CallerContext
            {
                User = testCaller.User,
                EntraObjectId = testCaller.User,
                PlatformRoles = testCaller.PlatformRoles,
                PrimaryPlatformRole = testCaller.PlatformRoles.FirstOrDefault(),
            };
            SetCaller(context, bypassCaller, CallerContextClaimsAdapter.ToPrincipal(
                bypassCaller,
                AgentweaverAuthenticationSchemes.TestBypass));
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (IsRunCapabilityPolicyRead(context, token))
        {
            var capabilityCaller = new CallerContext
            {
                User = ProjectAuthorization.InternalServiceUser,
                PlatformRoles = [],
            };
            SetCaller(context, capabilityCaller, CallerContextClaimsAdapter.ToPrincipal(
                capabilityCaller,
                AgentweaverAuthenticationSchemes.RunCapability,
                isInternalService: true));
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
            SetCaller(context, internalCaller, CallerContextClaimsAdapter.ToPrincipal(
                internalCaller,
                AgentweaverAuthenticationSchemes.InternalServiceKey,
                isInternalService: true));
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
        SetCaller(context, caller, CallerContextClaimsAdapter.ToPrincipal(
            caller,
            AgentweaverAuthenticationSchemes.Entra));
        await _next(context).ConfigureAwait(false);
    }

    public static CallerContext GetCaller(HttpContext context) =>
        CallerContextClaimsAdapter.FromPrincipal(context.User);

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

    private static IReadOnlyList<string> ReadTestPlatformRoles(
        string? configuredRoles,
        IReadOnlyList<string> defaultRoles) =>
        string.IsNullOrWhiteSpace(configuredRoles)
            ? defaultRoles
            : configuredRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsRunCapabilityPolicyRead(HttpContext context, string bearerToken)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            string.IsNullOrWhiteSpace(bearerToken) ||
            !string.Equals(
                bearerToken,
                context.Request.Headers[RunAuthorshipHeaders.RunToken].ToString(),
                StringComparison.Ordinal))
        {
            return false;
        }

        var segments = context.Request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments is ["api", "runs", _, "tool-approval-policies", _];
    }

    private sealed record TestCaller(string User, IReadOnlyList<string> PlatformRoles);
}

public static class ApiKeyAuthMiddleware
{
    public static CallerContext GetCaller(HttpContext context) =>
        GitHubTokenAuthMiddleware.GetCaller(context);
}
