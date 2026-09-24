namespace Agentweaver.Api.Memory;

public sealed class ScribeOperationAttempt
{
    public long Id { get; set; }
    public string OperationKey { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string RunId { get; set; } = "";
    public int LifecycleGeneration { get; set; }
    public string OperationType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
