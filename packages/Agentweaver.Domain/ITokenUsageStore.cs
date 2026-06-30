namespace Agentweaver.Domain;

public sealed record TokenUsageRecord
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public string? WorkflowRunId { get; init; }
    public string? ProjectId { get; init; }
    public required string ModelId { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    /// <summary>AI Credits in nano-AIU units (1 AIC = 1_000_000_000 nano-AIUs).</summary>
    public required long TotalNanoAiu { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record TokenUsageSummary
{
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long TotalTokens { get; init; }
    /// <summary>Total AI Credits in nano-AIU units.</summary>
    public required long TotalNanoAiu { get; init; }
    public required IReadOnlyList<TokenUsageByModel> ByModel { get; init; }
}

public sealed record TokenUsageByModel
{
    public required string ModelId { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long TotalNanoAiu { get; init; }
}

public sealed record TokenUsageByProject
{
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required long TotalTokens { get; init; }
    public required long TotalNanoAiu { get; init; }
    public required IReadOnlyList<TokenUsageByModel> ByModel { get; init; }
}

public interface ITokenUsageStore
{
    Task RecordAsync(TokenUsageRecord record, CancellationToken ct = default);
    Task<TokenUsageSummary> GetRunUsageAsync(string runId, CancellationToken ct = default);
    Task<TokenUsageSummary> GetWorkflowRunUsageAsync(string workflowRunId, CancellationToken ct = default);
    Task<TokenUsageSummary> GetProjectUsageAsync(string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<TokenUsageByProject>> GetAppUsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
