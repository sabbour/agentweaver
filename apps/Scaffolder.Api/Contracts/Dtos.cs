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

    /// <summary>
    /// JSON array of conflicting file paths when the run is in merge_failed status due to a conflict.
    /// Null for all other statuses.
    /// </summary>
    [JsonPropertyName("merge_conflicts")]
    public string? MergeConflicts { get; init; }

    [JsonPropertyName("sandbox")]
    public SandboxStatusDto? Sandbox { get; init; }

    /// <summary>
    /// Worktree branch name (e.g. "scaffolder-run-{runId}").
    /// Null for runs that have no worktree or have been cleaned up.
    /// </summary>
    [JsonPropertyName("worktree_branch")]
    public string? WorktreeBranch { get; init; }

    /// <summary>
    /// Whether the agent self-assessed the task as achieved (from report_outcome tool call).
    /// Null when the agent never called report_outcome (older runs or no-change runs).
    /// </summary>
    [JsonPropertyName("outcome_achieved")]
    public bool? OutcomeAchieved { get; init; }

    /// <summary>
    /// One-sentence explanation of the outcome from the agent's self-assessment.
    /// Null when OutcomeAchieved is null.
    /// </summary>
    [JsonPropertyName("outcome_reason")]
    public string? OutcomeReason { get; init; }
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

/// <summary>Request body for POST /api/runs/{id}/tool-approvals and tool-denials.</summary>
public sealed record ToolApprovalRequest
{
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>
    /// How broadly the approval applies.
    /// <c>"once"</c> (default) — this request only.
    /// <c>"run"</c> — auto-approve the same tool+URL for the remainder of this run.
    /// <c>"always"</c> — permanently allow the same tool+URL across all runs.
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "once";
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

    [JsonPropertyName("added_lines")]
    public int AddedLines { get; init; }

    [JsonPropertyName("removed_lines")]
    public int RemovedLines { get; init; }
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

    /// <summary>
    /// Merge result string (e.g. "merged:&lt;sha&gt;", "conflict:&lt;reason&gt;").
    /// Null when the merge could not complete and the run has been reverted.
    /// </summary>
    [JsonPropertyName("merge_result")]
    public string? MergeResult { get; init; }

    /// <summary>
    /// JSON array of conflicting file paths, populated when Status is "merge_failed".
    /// </summary>
    [JsonPropertyName("conflicting_files")]
    public IReadOnlyList<string>? ConflictingFiles { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/request-changes.</summary>
public sealed record RequestChangesRequest
{
    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/request-changes.</summary>
public sealed record RequestChangesResponse
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

    [JsonPropertyName("added_lines")]
    public int AddedLines { get; init; }

    [JsonPropertyName("removed_lines")]
    public int RemovedLines { get; init; }
}

/// <summary>Response body for GET /api/runs/{id}/files/{path}/content.</summary>
public sealed record WorkspaceFileContent
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }    // null if binary or deleted

    [JsonPropertyName("is_binary")]
    public bool IsBinary { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }   // language hint for syntax highlighting
}

// -----------------------------------------------------------------------
// Projects
// -----------------------------------------------------------------------

public sealed record CreateProjectRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }                   // "blank" | "github"
    [JsonPropertyName("source_repository")] public string? SourceRepository { get; init; }
    [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("default_provider")] public string? DefaultProvider { get; init; }
    [JsonPropertyName("default_model_github_copilot")] public string? DefaultModelGitHubCopilot { get; init; }
    [JsonPropertyName("default_model_microsoft_foundry")] public string? DefaultModelMicrosoftFoundry { get; init; }
}

public sealed record UpdateProjectNameRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed record UpdateProjectProviderSettingsRequest
{
    [JsonPropertyName("default_provider")] public string? DefaultProvider { get; init; }
    [JsonPropertyName("default_model_github_copilot")] public string? DefaultModelGitHubCopilot { get; init; }
    [JsonPropertyName("default_model_microsoft_foundry")] public string? DefaultModelMicrosoftFoundry { get; init; }
}

public sealed record RelinkProjectRequest
{
    [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; init; }
}

public sealed record ProjectResponse
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("origin")] public required string Origin { get; init; }
    [JsonPropertyName("source_repository")] public string? SourceRepository { get; init; }
    [JsonPropertyName("working_directory")] public required string WorkingDirectory { get; init; }
    [JsonPropertyName("default_branch")] public required string DefaultBranch { get; init; }
    [JsonPropertyName("owner")] public required string Owner { get; init; }
    [JsonPropertyName("default_provider")] public required string DefaultProvider { get; init; }
    [JsonPropertyName("default_model_github_copilot")] public string? DefaultModelGitHubCopilot { get; init; }
    [JsonPropertyName("default_model_microsoft_foundry")] public string? DefaultModelMicrosoftFoundry { get; init; }
    [JsonPropertyName("available")] public required bool Available { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CreateProjectRunRequest
{
    [JsonPropertyName("task")] public string? Task { get; init; }
    [JsonPropertyName("model_source")] public string? ModelSource { get; init; }
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
    [JsonPropertyName("base_branch")] public string? BaseBranch { get; init; }
}

// -----------------------------------------------------------------------
// GitHub auth
// -----------------------------------------------------------------------

public sealed record GitHubDeviceFlowResponse
{
    [JsonPropertyName("user_code")] public required string UserCode { get; init; }
    [JsonPropertyName("verification_uri")] public required string VerificationUri { get; init; }
    [JsonPropertyName("expires_in")] public required int ExpiresIn { get; init; }
    [JsonPropertyName("interval")] public required int Interval { get; init; }
}

public sealed record GitHubPollResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }   // "pending" | "success" | "expired" | "denied"
    [JsonPropertyName("login")] public string? Login { get; init; }
}

public sealed record GitHubAuthStatusResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }   // "signed_in" | "signed_out" | "never_signed_in"
    [JsonPropertyName("login")] public string? Login { get; init; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; init; }
}
