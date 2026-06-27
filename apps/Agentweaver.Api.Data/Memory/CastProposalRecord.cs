namespace Agentweaver.Api.Memory;

public sealed class CastProposalRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string ProposalJson { get; set; } = "";
}
