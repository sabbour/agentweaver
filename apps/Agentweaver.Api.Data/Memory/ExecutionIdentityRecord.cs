namespace Agentweaver.Api.Memory;

public sealed class ExecutionIdentityRecord
{
    public string DescriptorId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string RunId { get; set; } = "";
    public int Attempt { get; set; }
    public string? ProjectId { get; set; }
    public string InitiatingPrincipalId { get; set; } = "";
    public string ExecutingServiceId { get; set; } = "";
    public string AgentAssignmentId { get; set; } = "";
    public string? AgentRole { get; set; }
    public string? AgentDisplayName { get; set; }
    public string? ParentRunId { get; set; }
    public string? ParentDescriptorId { get; set; }
    public string? RetryOfRunId { get; set; }
    public string? RetryOfDescriptorId { get; set; }
    public string? WorkflowRunId { get; set; }
    public string? SubtaskId { get; set; }
    public string? ApprovalPolicySnapshotId { get; set; }
    public string? ExecutableWorkflowContentDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
