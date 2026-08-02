using Agentweaver.Domain;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Auth;

public interface IProjectRoleAuthorizationService
{
    bool IsPlatformAdmin(CallerContext caller);
    Task<ProjectRole?> GetEffectiveRoleAsync(CallerContext caller, ProjectId projectId, CancellationToken ct = default);
    Task<bool> HasRoleAsync(CallerContext caller, ProjectId projectId, ProjectRole minimumRole, CancellationToken ct = default);
    Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(CallerContext caller, CancellationToken ct = default);
}

public sealed class ProjectRoleAuthorizationService(
    IProjectRoleAssignmentStore assignments,
    IProjectStore projects,
    ILegacyProjectRoleBackfillService legacyBackfill) : IProjectRoleAuthorizationService
{
    public bool IsPlatformAdmin(CallerContext caller) =>
        caller.PlatformRoles.Contains(PlatformRoles.PlatformAdmin, StringComparer.Ordinal);

    public async Task<ProjectRole?> GetEffectiveRoleAsync(CallerContext caller, ProjectId projectId, CancellationToken ct = default)
    {
        if (IsPlatformAdmin(caller))
            return ProjectRole.Owner;

        var principalId = ResolvePrincipalId(caller);
        if (string.IsNullOrWhiteSpace(principalId))
            return null;

        var assignment = await assignments.GetAsync(projectId, principalId, ct).ConfigureAwait(false);
        if (assignment is not null)
            return assignment.Role;

        var project = await projects.GetAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
            return null;

        return await legacyBackfill.TryBackfillOwnerAsync(caller, project, ct).ConfigureAwait(false)
            ? ProjectRole.Owner
            : null;
    }

    public async Task<bool> HasRoleAsync(CallerContext caller, ProjectId projectId, ProjectRole minimumRole, CancellationToken ct = default)
    {
        var effectiveRole = await GetEffectiveRoleAsync(caller, projectId, ct).ConfigureAwait(false);
        return effectiveRole is not null && effectiveRole.Value.Satisfies(minimumRole);
    }

    public async Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(CallerContext caller, CancellationToken ct = default)
    {
        var principalId = ResolvePrincipalId(caller);
        if (string.IsNullOrWhiteSpace(principalId))
            return new Dictionary<ProjectId, ProjectRole>();

        var resolved = await assignments.ListByPrincipalAsync(principalId, ct).ConfigureAwait(false);
        var explicitRoles = resolved.ToDictionary(x => x.ProjectId, x => x.Role);
        var projectsForBackfill = await projects.ListAsync(ct).ConfigureAwait(false);
        return await legacyBackfill
            .BackfillVisibleOwnerRolesAsync(caller, explicitRoles, projectsForBackfill, ct)
            .ConfigureAwait(false);
    }

    private static string? ResolvePrincipalId(CallerContext caller) =>
        !string.IsNullOrWhiteSpace(caller.EntraObjectId) ? caller.EntraObjectId : caller.User;
}

internal sealed class NullProjectRoleAuthorizationService : IProjectRoleAuthorizationService
{
    public bool IsPlatformAdmin(CallerContext caller) => false;

    public Task<ProjectRole?> GetEffectiveRoleAsync(CallerContext caller, ProjectId projectId, CancellationToken ct = default) =>
        Task.FromResult<ProjectRole?>(null);

    public Task<bool> HasRoleAsync(CallerContext caller, ProjectId projectId, ProjectRole minimumRole, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(CallerContext caller, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<ProjectId, ProjectRole>>(new Dictionary<ProjectId, ProjectRole>());
}
