using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scaffolder.Cli;

/// <summary>Request body for POST /api/runs.</summary>
public sealed record SubmitRunRequest
{
    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("originating_branch")]
    public required string OriginatingBranch { get; init; }

    [JsonPropertyName("task")]
    public required string Task { get; init; }

    [JsonPropertyName("model_source")]
    public required string ModelSource { get; init; }
}

/// <summary>Response body for POST /api/runs.</summary>
public sealed record SubmitRunResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>Response body for GET /api/runs/{id}.</summary>
public sealed record RunDetail
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("model_source")]
    public required string ModelSource { get; init; }

    [JsonPropertyName("started_at")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public string? EndedAt { get; init; }

    [JsonPropertyName("step_count")]
    public required int StepCount { get; init; }

    [JsonPropertyName("diff")]
    public string? Diff { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewSubmitRequest
{
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewSubmitResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("merge_result")]
    public string? MergeResult { get; init; }
}

/// <summary>Run event envelope as served by the API stream and events log.</summary>
public sealed record RunEvent
{
    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    [JsonPropertyName("callId")]
    public string? CallId { get; init; }
}

/// <summary>Error body returned with HTTP 409 for a retriable review failure.</summary>
public sealed record RetriableReviewErrorBody
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>Sandbox policy for a repository.</summary>
public sealed record SandboxPolicy
{
    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("shell_enabled")]
    public required bool ShellEnabled { get; init; }
}

/// <summary>Request body for PUT /api/sandbox-policy.</summary>
public sealed record SetSandboxPolicyRequest
{
    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("shell_enabled")]
    public required bool ShellEnabled { get; init; }
}

/// <summary>An entry in the workspace file list for a run.</summary>
public sealed record WorkspaceFileEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}

/// <summary>Per-file diff returned by GET /api/runs/{id}/files/{path}.</summary>
public sealed record WorkspaceFileDiff
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("diff")]
    public string? Diff { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("is_binary")]
    public required bool IsBinary { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/request-changes.</summary>
public sealed record RequestChangesRequest
{
    [JsonPropertyName("comment")]
    public required string Comment { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/request-changes.</summary>
public sealed record RequestChangesResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>Shared serialization settings for the CLI.</summary>
public static class JsonConfig
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
