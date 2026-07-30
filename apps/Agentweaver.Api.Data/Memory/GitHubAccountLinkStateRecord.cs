namespace Agentweaver.Api.Memory;

public sealed class GitHubAccountLinkStateRecord
{
    public string State { get; set; } = "";
    public string EntraUserId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
