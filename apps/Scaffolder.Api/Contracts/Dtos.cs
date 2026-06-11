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

    [JsonPropertyName("sandbox")]
    public SandboxStatusDto? Sandbox { get; init; }
}

public sealed record SandboxStatusDto
{
    [JsonPropertyName("backend")]
    public required string Backend { get; init; }

    [JsonPropertyName("is_real_isolation")]
    public required bool IsRealIsolation { get; init; }

    [JsonPropertyName("selection_reason")]
    public string? SelectionReason { get; init; }

    [JsonPropertyName("has_network_warning")]
    public bool HasNetworkWarning { get; init; }
}

public sealed record SandboxPolicyDto
{
    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("shell_enabled")]
    public bool ShellEnabled { get; init; }

    [JsonPropertyName("direct")]
    public bool Direct { get; init; }

    [JsonPropertyName("network_enabled")]
    public bool NetworkEnabled { get; init; }

    [JsonPropertyName("allowed_repository_roots")]
    public IReadOnlyList<string> AllowedRepositoryRoots { get; init; } = [];

    [JsonPropertyName("destructive_command_patterns")]
    public IReadOnlyList<string> DestructiveCommandPatterns { get; init; } = [];

    [JsonPropertyName("require_approval_for_all_shell")]
    public bool RequireApprovalForAllShell { get; init; }

    [JsonPropertyName("redact_pii")]
    public bool RedactPii { get; init; }

    [JsonPropertyName("max_output_bytes")]
    public int MaxOutputBytes { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/shell-approvals.</summary>
public sealed record ShellApprovalRequest
{
    [JsonPropertyName("command_hash")]
    public string? CommandHash { get; init; }
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

/// <summary>One file entry in the changed-file set for a run (FR-034).</summary>
public sealed record WorkspaceFileEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }   // "added" | "modified" | "deleted"

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }    // "committed" | "uncommitted" | "merged"
}

/// <summary>Per-file unified diff for a single file in a run's worktree (FR-035).</summary>
public sealed record WorkspaceFileDiff
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("diff")]
    public string? Diff { get; init; }             // unified diff chunk; null if binary or unavailable

    [JsonPropertyName("status")]
    public required string Status { get; init; }   // "added" | "modified" | "deleted"

    [JsonPropertyName("is_binary")]
    public bool IsBinary { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/commit.</summary>
public sealed record CommitResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>One entry in the flat workspace listing returned by GET /api/runs/{id}/workspace.</summary>
public sealed record WorkspaceNode
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }     // relative path, forward slashes

    [JsonPropertyName("is_folder")]
    public bool IsFolder { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }           // "added" | "modified" | "deleted" | null (unchanged)
}
