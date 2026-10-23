namespace Agentweaver.Projects.Config;

/// <summary>Latest AgentHost-verified hosted reasoning levels for one project, actor, and connection.</summary>
public sealed class HostedModelReasoningCatalogRecord
{
    public string TenantId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public string BindingsRevision { get; set; } = "";
    public string Reference { get; set; } = "";
    public string[] SupportedReasoningEfforts { get; set; } = [];
    public DateTimeOffset VerifiedAt { get; set; }
}
