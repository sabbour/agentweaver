using System.Text.Json.Serialization;

namespace Agentweaver.Mcp.Contracts;

// ── Workspace refs ──────────────────────────────────────────────────────────

/// <summary>One browsable git ref: the project base branch or an active run worktree.</summary>
public sealed record WorkspaceRef
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }                           // "base" | "worktree"
    [JsonPropertyName("branch")] public required string Branch { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("run_id")] public string? RunId { get; init; }
    [JsonPropertyName("run_status")] public string? RunStatus { get; init; }
    [JsonPropertyName("originating_branch")] public string? OriginatingBranch { get; init; }
}

/// <summary>Response for GET /api/projects/{id}/workspace/refs.</summary>
public sealed record WorkspaceRefsResponse
{
    [JsonPropertyName("current_branch")] public required string CurrentBranch { get; init; }
    [JsonPropertyName("refs")] public required IReadOnlyList<WorkspaceRef> Refs { get; init; }
}

// ── File listing ────────────────────────────────────────────────────────────

/// <summary>One entry in the flat file tree returned by GET /api/projects/{id}/workspace.</summary>
public sealed record WorkspaceNode
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("is_folder")] public bool IsFolder { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }       // "added" | "modified" | "deleted" | null
    [JsonPropertyName("added_lines")] public int AddedLines { get; init; }
    [JsonPropertyName("removed_lines")] public int RemovedLines { get; init; }
}

// ── File content ────────────────────────────────────────────────────────────

/// <summary>
/// Response for GET /api/projects/{id}/workspace/files/{**path}/content.
/// <c>content</c> is null when the file is binary or deleted; <c>language</c> is "too_large" when the
/// file exceeds the 1 MB server cap.
/// </summary>
public sealed record WorkspaceFileContent
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("is_binary")] public bool IsBinary { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
}
