using System.Text.Json.Serialization;

namespace Scaffolder.Cli.Api;

/// <summary>
/// T053: API models matching contracts/run-api.yaml.
/// </summary>

public sealed class CreateRunRequest
{
    [JsonPropertyName("originatingBranch")]
    public required string OriginatingBranch { get; init; }

    [JsonPropertyName("modelSource")]
    public required string ModelSource { get; init; }

    [JsonPropertyName("taskPrompt")]
    public required string TaskPrompt { get; init; }

    [JsonPropertyName("maxSteps")]
    public int? MaxSteps { get; init; }

    [JsonPropertyName("maxDurationSeconds")]
    public int? MaxDurationSeconds { get; init; }
}

public sealed class RunResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("originatingBranch")]
    public string OriginatingBranch { get; init; } = string.Empty;

    [JsonPropertyName("modelSource")]
    public string ModelSource { get; init; } = string.Empty;

    [JsonPropertyName("taskPrompt")]
    public string TaskPrompt { get; init; } = string.Empty;

    [JsonPropertyName("submittedBy")]
    public string SubmittedBy { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("maxSteps")]
    public int MaxSteps { get; init; }

    [JsonPropertyName("maxDurationSeconds")]
    public int MaxDurationSeconds { get; init; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; init; }

    [JsonPropertyName("diffSummary")]
    public string? DiffSummary { get; init; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }
}

public sealed class ReviewDecisionRequest
{
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("reviewer")]
    public required string Reviewer { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}
