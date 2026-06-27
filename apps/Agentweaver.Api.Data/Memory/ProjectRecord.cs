namespace Agentweaver.Api.Memory;

public sealed class ProjectRecord
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginKind { get; set; } = "";
    public string? SourceRepository { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public string Owner { get; set; } = "";
    public string DefaultProvider { get; set; } = "";
    public string? DefaultModelCopilot { get; set; }
    public string? DefaultModelFoundry { get; set; }
    public string State { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int MaxReadyPerHeartbeat { get; set; } = 3;
    public bool PickupAutopilot { get; set; } = true;
    public bool PickupAutoApproveTools { get; set; }
    public string? DefaultWorkflowId { get; set; }
    public string? ActiveReviewPolicyName { get; set; }
    public string? SandboxProfile { get; set; }
    public string? SourceBlueprintId { get; set; }
    public string? SourceBlueprintType { get; set; }
    public string? AllowedWorkflowIds { get; set; }
}
