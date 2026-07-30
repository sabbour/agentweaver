namespace Agentweaver.Domain;

public enum ProjectRoleAssignmentStoreMutationStatus
{
    Ok,
    NotFound,
    LastOwnerConflict,
}

public sealed record ProjectRoleAssignmentStoreMutationResult(
    ProjectRoleAssignmentStoreMutationStatus Status,
    ProjectRoleAssignment? Assignment = null);

public interface IProjectRoleAssignmentStore
{
    Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default);
    Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment assignment, CancellationToken ct = default);
    Task<ProjectRoleAssignment?> GetAsync(ProjectId projectId, string principalId, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default);
    Task<bool> DeleteAsync(ProjectId projectId, string principalId, CancellationToken ct = default);
    Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId projectId, string principalId, CancellationToken ct = default);
}
