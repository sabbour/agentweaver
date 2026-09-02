using System.Security.Claims;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Auth;

public static class AgentweaverAuthenticationSchemes
{
    public const string Entra = "Entra";
    public const string GitHubToken = "GitHubToken";
    public const string McpOAuth = "McpOAuth";
    public const string InternalServiceKey = "InternalServiceKey";
    public const string RunCapability = "RunCapability";
    public const string TestBypass = "TestBypass";
}

public static class AgentweaverClaimTypes
{
    public const string PrivatePrefix = "agentweaver_";
    public const string AuthenticationScheme = "agentweaver_auth_scheme";
    public const string PrimaryPlatformRole = "agentweaver_primary_role";
    public const string Organization = "agentweaver_org";
    public const string InternalService = "agentweaver_internal";
    public const string GitHubLogin = "gh_login";
    public const string RawPlatformRole = "raw_role";
    public const string AuthenticationMode = "auth_mode";
    public const string EntraObjectId = "oid";
    public const string EntraTenantId = "tid";
}

public static class CallerContextClaimsAdapter
{
    public static CallerContext FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var user = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("An authenticated caller identity is required.");

        return new CallerContext
        {
            User = user,
            EntraObjectId = principal.FindFirst(AgentweaverClaimTypes.EntraObjectId)?.Value,
            EntraTenantId = principal.FindFirst(AgentweaverClaimTypes.EntraTenantId)?.Value,
            PlatformRoles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            RawPlatformRoles = principal.FindAll(AgentweaverClaimTypes.RawPlatformRole).Select(claim => claim.Value).ToArray(),
            PrimaryPlatformRole = principal.FindFirst(AgentweaverClaimTypes.PrimaryPlatformRole)?.Value,
            GitHubLogin = principal.FindFirst(AgentweaverClaimTypes.GitHubLogin)?.Value,
            DisplayName = principal.FindFirst(ClaimTypes.Name)?.Value,
            Email = principal.FindFirst(ClaimTypes.Email)?.Value,
            AuthenticationScheme = principal.FindFirst(AgentweaverClaimTypes.AuthenticationScheme)?.Value,
            Org = principal.FindFirst(AgentweaverClaimTypes.Organization)?.Value,
        };
    }

    public static ClaimsPrincipal ToPrincipal(
        CallerContext caller,
        string authenticationScheme,
        bool isInternalService = false)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, caller.User),
            new(AgentweaverClaimTypes.AuthenticationMode, "Entra"),
            new(AgentweaverClaimTypes.AuthenticationScheme, authenticationScheme),
        };
        AddIfPresent(claims, ClaimTypes.Name, caller.DisplayName);
        AddIfPresent(claims, ClaimTypes.Email, caller.Email);
        AddIfPresent(claims, AgentweaverClaimTypes.EntraObjectId, caller.EntraObjectId);
        AddIfPresent(claims, AgentweaverClaimTypes.EntraTenantId, caller.EntraTenantId);
        AddIfPresent(claims, AgentweaverClaimTypes.PrimaryPlatformRole, caller.PrimaryPlatformRole);
        AddIfPresent(claims, AgentweaverClaimTypes.GitHubLogin, caller.GitHubLogin);
        AddIfPresent(claims, AgentweaverClaimTypes.Organization, caller.Org);
        claims.AddRange(caller.PlatformRoles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(caller.RawPlatformRoles.Select(role => new Claim(AgentweaverClaimTypes.RawPlatformRole, role)));
        if (isInternalService)
            claims.Add(new Claim(AgentweaverClaimTypes.InternalService, "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Agentweaver"));
    }

    public static IReadOnlyList<Claim> RemovePrivateInboundClaims(IEnumerable<Claim> claims) =>
        claims.Where(claim =>
                !claim.Type.StartsWith(AgentweaverClaimTypes.PrivatePrefix, StringComparison.Ordinal))
            .ToArray();

    private static void AddIfPresent(ICollection<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            claims.Add(new Claim(type, value));
    }
}
