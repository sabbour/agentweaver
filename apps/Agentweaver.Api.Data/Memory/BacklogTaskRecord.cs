namespace Agentweaver.Api.Memory;

public sealed class BacklogTaskRecord
{
    public string TaskId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string State { get; set; } = "";
    public string OrderKey { get; set; } = "";
    public string CapturedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? RunId { get; set; }
    public string? WorkflowOverrideId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? SourceFilePath { get; set; }
}
