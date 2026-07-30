namespace Agentweaver.Api.Memory;

public sealed class ProjectRoleAssignmentRecord
{
    public string ProjectId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string Role { get; set; } = "";
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
}
