namespace Agentweaver.Api.Memory;

public sealed class BacklogTaskDependencyRecord
{
    public string ProjectId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string DependsOnTaskId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

