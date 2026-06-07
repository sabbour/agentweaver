using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scaffolder.Api.Contracts;

/// <summary>Client-facing event envelope (FR-018).</summary>
public sealed record EventEnvelopeDto
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("sequence")]
    public required int Sequence { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    [JsonPropertyName("callId")]
    public string? CallId { get; init; }
}

/// <summary>Request body for POST /api/runs.</summary>
public sealed record CreateRunRequest
{
    [JsonPropertyName("repository_path")]
    public string? RepositoryPath { get; init; }

    [JsonPropertyName("originating_branch")]
    public string? OriginatingBranch { get; init; }

    [JsonPropertyName("task")]
    public string? Task { get; init; }

    [JsonPropertyName("model_source")]
    public string? ModelSource { get; init; }
}

/// <summary>Response body for POST /api/runs.</summary>
public sealed record CreateRunResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>Response body for GET /api/runs/{id}.</summary>
public sealed record RunResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("model_source")]
    public required string ModelSource { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("step_count")]
    public required int StepCount { get; init; }

    [JsonPropertyName("diff")]
    public string? Diff { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewRequest
{
    [JsonPropertyName("approved")]
    public bool? Approved { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("merge_result")]
    public string? MergeResult { get; init; }
}
