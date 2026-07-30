namespace Agentweaver.Api.Memory;

public sealed class ProjectGitHubIdentityOverrideRecord
{
    public string ProjectId { get; set; } = "";
    public string EntraUserId { get; set; } = "";
    public string GitHubLogin { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
