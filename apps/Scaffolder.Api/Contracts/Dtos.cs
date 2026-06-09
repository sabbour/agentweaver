using System.Text.Json.Serialization;

namespace Scaffolder.Api.Contracts;

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

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>
    /// Unified diff of the agent's changes versus the originating branch.
    /// Only served when the run is in a review-ready or terminal state
    /// (awaiting_review, merged, merge_failed, declined). Withheld for
    /// failed/in_progress/pending to prevent leaking content from
    /// safety-failed or incomplete runs (FR-026 / SC-009).
    /// </summary>
    [JsonPropertyName("diff")]
    public string? Diff { get; init; }

    [JsonPropertyName("step_count")]
    public int StepCount { get; init; }

    [JsonPropertyName("tree_hash")]
    public string? TreeHash { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewRequest
{
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }
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
