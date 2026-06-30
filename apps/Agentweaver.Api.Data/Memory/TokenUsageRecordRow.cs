namespace Agentweaver.Api.Memory;

/// <summary>EF entity for the token_usage_records table (Feature 019: AI Credit and token monitoring).</summary>
public sealed class TokenUsageRecordRow
{
    public string Id { get; set; } = "";
    public string RunId { get; set; } = "";
    public string? WorkflowRunId { get; set; }
    public string? ProjectId { get; set; }
    public string ModelId { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalNanoAiu { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
