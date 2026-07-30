using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public enum ProjectRoleAssignmentMutationStatus
{
    Ok,
    NotFound,
    LastOwnerConflict,
}

public sealed record ProjectRoleAssignmentMutationResult(
    ProjectRoleAssignmentMutationStatus Status,
    ProjectRoleAssignment? Assignment = null,
    string? Error = null);

public sealed class ProjectRoleAssignmentService(IProjectRoleAssignmentStore assignments)
{
    public async Task SeedOwnerAsync(ProjectId projectId, string principalId, string grantedBy, CancellationToken ct = default)
    {
        await assignments.UpsertAsync(
            new ProjectRoleAssignment
            {
                ProjectId = projectId,
                PrincipalId = principalId,
                Role = ProjectRole.Owner,
                GrantedBy = grantedBy,
                GrantedAt = DateTimeOffset.UtcNow,
            },
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ProjectRoleAssignment>> ListAsync(ProjectId projectId, CancellationToken ct = default) =>
        assignments.ListByProjectAsync(projectId, ct);

    public async Task<ProjectRoleAssignmentMutationResult> UpsertAsync(
        ProjectId projectId,
        string principalId,
        ProjectRole role,
        string grantedBy,
        CancellationToken ct = default)
    {
        var assignment = new ProjectRoleAssignment
        {
            ProjectId = projectId,
            PrincipalId = principalId,
            Role = role,
            GrantedBy = grantedBy,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        var result = await assignments.UpsertEnsuringOwnerInvariantAsync(assignment, ct).ConfigureAwait(false);
        return result.Status switch
        {
            ProjectRoleAssignmentStoreMutationStatus.Ok =>
                new ProjectRoleAssignmentMutationResult(ProjectRoleAssignmentMutationStatus.Ok, result.Assignment ?? assignment),
            ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict =>
                new ProjectRoleAssignmentMutationResult(
                    ProjectRoleAssignmentMutationStatus.LastOwnerConflict,
                    Error: "Cannot remove the last explicit Owner assignment. Grant Owner to another principal first."),
            _ => new ProjectRoleAssignmentMutationResult(ProjectRoleAssignmentMutationStatus.NotFound),
        };
    }

    public async Task<ProjectRoleAssignmentMutationResult> RemoveAsync(
        ProjectId projectId,
        string principalId,
        CancellationToken ct = default)
    {
        var result = await assignments.DeleteEnsuringOwnerInvariantAsync(projectId, principalId, ct).ConfigureAwait(false);
        return result.Status switch
        {
            ProjectRoleAssignmentStoreMutationStatus.Ok =>
                new ProjectRoleAssignmentMutationResult(ProjectRoleAssignmentMutationStatus.Ok, result.Assignment),
            ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict =>
                new ProjectRoleAssignmentMutationResult(
                    ProjectRoleAssignmentMutationStatus.LastOwnerConflict,
                    Error: "Cannot remove the last explicit Owner assignment. Grant Owner to another principal first."),
            _ => new ProjectRoleAssignmentMutationResult(ProjectRoleAssignmentMutationStatus.NotFound),
        };
    }
}
