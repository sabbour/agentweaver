using System.Collections.Immutable;
using System.Security.Claims;

namespace Agentweaver.Identity;

public sealed class IdentityAuthorizationContext
{
    public const string TenantIdClaimType = "tenant_id";
    public const string RoleClaimType = "role";
    public const string ExternalTenantIdClaimType = "tid";
    public const string MappedExternalTenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";
    public const string ExternalRolesClaimType = "roles";
    public const string PlatformAdminRole = "platform_admin";
    public const string OrchestratorRole = "orchestrator";

    private IdentityAuthorizationContext(string tenantId, ImmutableArray<string> roles)
    {
        TenantId = tenantId;
        Roles = roles;
    }

    public string TenantId { get; }
    public ImmutableArray<string> Roles { get; }

    public static IdentityAuthorizationContext? FromValidatedExternalPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        var tenantClaims = principal.FindAll(ExternalTenantIdClaimType)
            .Concat(principal.FindAll(MappedExternalTenantIdClaimType))
            .Take(2)
            .ToArray();
        if (tenantClaims.Length != 1 || !IsValidIdentifier(tenantClaims[0].Value))
            return null;

        var roles = ReadRecognizedRoles(
            principal.FindAll(ExternalRolesClaimType)
                .Concat(principal.FindAll("role"))
                .Concat(principal.FindAll(ClaimTypes.Role)));
        return new IdentityAuthorizationContext(tenantClaims[0].Value, roles);
    }

    public static IdentityAuthorizationContext? FromIssuedPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tenantClaims = principal.FindAll(TenantIdClaimType).Take(2).ToArray();
        if (tenantClaims.Length != 1 || !IsValidIdentifier(tenantClaims[0].Value))
            return null;

        var roles = ReadRecognizedRoles(
            principal.FindAll(RoleClaimType).Concat(principal.FindAll(ClaimTypes.Role)));
        return new IdentityAuthorizationContext(tenantClaims[0].Value, roles);
    }

    public ImmutableArray<Claim> ToClaims() =>
    [
        new Claim(TenantIdClaimType, TenantId),
        .. Roles.Select(role => new Claim(RoleClaimType, role)),
    ];

    private static ImmutableArray<string> ReadRecognizedRoles(IEnumerable<Claim> claims) =>
        claims.Select(claim => claim.Value)
            .Where(role => string.Equals(role, PlatformAdminRole, StringComparison.Ordinal) ||
                string.Equals(role, OrchestratorRole, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool IsValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
