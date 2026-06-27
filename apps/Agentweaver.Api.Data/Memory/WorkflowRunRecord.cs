namespace Agentweaver.Api.Memory;

public sealed class WorkflowRunRecord
{
    public string WorkflowRunId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Task { get; set; } = "";
    public string SubmittingUser { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string? OrchestrationWorktreePath { get; set; }
}
