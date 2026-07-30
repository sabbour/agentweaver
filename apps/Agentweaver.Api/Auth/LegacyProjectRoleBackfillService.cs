using Agentweaver.Api.Security;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public enum LegacyProjectClaimState
{
    Claimed,
    ClaimableByCaller,
    UnclaimedNeedsAdmin,
}

public interface ILegacyProjectRoleBackfillService
{
    Task<LegacyProjectClaimState> GetClaimStateAsync(CallerContext caller, Project project, CancellationToken ct = default);
    Task<bool> TryBackfillOwnerAsync(CallerContext caller, Project project, CancellationToken ct = default);
    Task<IReadOnlyDictionary<ProjectId, ProjectRole>> BackfillVisibleOwnerRolesAsync(
        CallerContext caller,
        IReadOnlyDictionary<ProjectId, ProjectRole> resolvedRoles,
        IReadOnlyList<Project> projects,
        CancellationToken ct = default);
}

public sealed class LegacyProjectRoleBackfillService(
    IProjectRoleAssignmentStore assignments,
    IGitHubTokenStore tokenStore) : ILegacyProjectRoleBackfillService
{
    public async Task<LegacyProjectClaimState> GetClaimStateAsync(CallerContext caller, Project project, CancellationToken ct = default)
    {
        var existing = await assignments.ListByProjectAsync(project.Id, ct).ConfigureAwait(false);
        if (existing.Count > 0)
            return LegacyProjectClaimState.Claimed;

        if (await CallerMatchesLegacyOwnerAsync(caller, project.Owner, ct).ConfigureAwait(false))
            return LegacyProjectClaimState.ClaimableByCaller;

        return LegacyProjectClaimState.UnclaimedNeedsAdmin;
    }

    public async Task<bool> TryBackfillOwnerAsync(CallerContext caller, Project project, CancellationToken ct = default)
    {
        if (await GetClaimStateAsync(caller, project, ct).ConfigureAwait(false) != LegacyProjectClaimState.ClaimableByCaller)
            return false;

        var principalId = caller.EntraObjectId;
        if (string.IsNullOrWhiteSpace(principalId))
            return false;

        await assignments.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = project.Id,
            PrincipalId = principalId,
            Role = ProjectRole.Owner,
            GrantedBy = principalId,
            GrantedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        return true;
    }

    public async Task<IReadOnlyDictionary<ProjectId, ProjectRole>> BackfillVisibleOwnerRolesAsync(
        CallerContext caller,
        IReadOnlyDictionary<ProjectId, ProjectRole> resolvedRoles,
        IReadOnlyList<Project> projects,
        CancellationToken ct = default)
    {
        var merged = resolvedRoles.ToDictionary(x => x.Key, x => x.Value);
        foreach (var project in projects)
        {
            if (merged.ContainsKey(project.Id))
                continue;

            if (await TryBackfillOwnerAsync(caller, project, ct).ConfigureAwait(false))
                merged[project.Id] = ProjectRole.Owner;
        }

        return merged;
    }

    private async Task<bool> CallerMatchesLegacyOwnerAsync(CallerContext caller, string? legacyOwner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caller.EntraObjectId) || string.IsNullOrWhiteSpace(legacyOwner))
            return false;

        if (tokenStore is not IMultiIdentityGitHubTokenStore multiIdentity)
            return false;

        var linkedAccounts = await multiIdentity.ListLinkedIdentitiesAsync(caller.EntraObjectId, ct).ConfigureAwait(false);
        return linkedAccounts.Any(link =>
            string.Equals(link.GitHubLogin, legacyOwner, StringComparison.OrdinalIgnoreCase));
    }
}
