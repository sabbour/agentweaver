using System.Collections.Immutable;
using System.Security.Claims;

namespace Agentweaver.Projects.Config;

public sealed record ProjectCaller(string ActorId, string TenantId, ImmutableHashSet<string> Roles)
{
    public const string PlatformAdminRole = "platform_admin";
    public const string OrchestratorRole = "orchestrator";

    public static ProjectCaller FromPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
            throw ProjectConfigException.Forbidden();

        var subjects = principal.FindAll("sub").Select(claim => claim.Value).ToArray();
        var tenants = principal.FindAll("tenant_id").Select(claim => claim.Value).ToArray();
        if (subjects.Length != 1 || tenants.Length != 1 ||
            !IsOpaqueId(subjects[0]) || !IsOpaqueId(tenants[0]))
            throw ProjectConfigException.Forbidden();

        var roles = principal.FindAll("role")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToImmutableHashSet(StringComparer.Ordinal);
        return new ProjectCaller(subjects[0], tenants[0], roles);
    }

    private static bool IsOpaqueId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
