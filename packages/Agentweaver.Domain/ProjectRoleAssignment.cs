namespace Agentweaver.Domain;

public sealed record ProjectRoleAssignment
{
    public required ProjectId ProjectId { get; init; }
    public required string PrincipalId { get; init; }
    public required ProjectRole Role { get; init; }
    public required string GrantedBy { get; init; }
    public required DateTimeOffset GrantedAt { get; init; }

    public string Scope => ProjectRoleAssignmentScopes.ForProject(ProjectId);
}

public static class ProjectRoleAssignmentScopes
{
    public static string ForProject(ProjectId projectId) => $"Project:{projectId}";
}
