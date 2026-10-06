using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Identity;

namespace Agentweaver.Projects.Config;

public sealed record ProjectCaller(string ActorId, string TenantId, ImmutableHashSet<string> Roles)
{
    public const string PlatformAdminRole = IdentityAuthorizationContext.PlatformAdminRole;
    public const string OrchestratorRole = IdentityAuthorizationContext.OrchestratorRole;

    public static ProjectCaller FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
            throw ProjectConfigException.Forbidden();

        var subjects = principal.FindAll("sub").Select(claim => claim.Value).ToArray();
        var authorization = IdentityAuthorizationContext.FromIssuedPrincipal(principal);
        if (subjects.Length != 1 || !IsOpaqueId(subjects[0]) || authorization is null)
            throw ProjectConfigException.Forbidden();

        return new ProjectCaller(
            subjects[0],
            authorization.TenantId,
            authorization.Roles.ToImmutableHashSet(StringComparer.Ordinal));
    }

    private static bool IsOpaqueId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
