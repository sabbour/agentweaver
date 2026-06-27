namespace Agentweaver.Api.Memory;

public sealed class RunRevisionRecord
{
    public string RunId { get; set; } = "";
    public int RevisionNumber { get; set; }
    public string ReviewerUser { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string RawComment { get; set; } = "";
    public string SanitizedComment { get; set; } = "";
    public string PreviousTreeHash { get; set; } = "";
}
