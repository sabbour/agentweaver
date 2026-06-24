using System.Text.Json.Serialization;

namespace Agentweaver.Api.Contracts;

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

    [JsonPropertyName("agent_name")]
    public string? AgentName { get; init; }

    /// <summary>
    /// When true, allow-with-approval tool requests (e.g. <c>web_fetch</c>) are auto-granted at the
    /// HITL gate for this run instead of stalling for an operator. Policy-denied tools stay denied.
    /// Defaults to false. (Feature 008)
    /// </summary>
    [JsonPropertyName("auto_approve_tools")]
    public bool AutoApproveTools { get; init; }
}

/// <summary>Response body for POST /api/runs.</summary>
public sealed record CreateRunResponse
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }  // execution_id

    [JsonPropertyName("workflow_run_id")]
    public required string WorkflowRunId { get; init; }

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
    /// Worktree branch name (e.g. "agentweaver-run-{runId}").
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

    [JsonPropertyName("agent_name")]
    public string? AgentName { get; init; }

    [JsonPropertyName("reviewed_by")]
    public string? ReviewedBy { get; init; }

    [JsonPropertyName("workflow_run_id")]
    public string? WorkflowRunId { get; init; }

    /// <summary>
    /// Parent coordinator run id when this run is a coordinator CHILD (Feature 008).
    /// Null for standalone runs and for the coordinator run itself. The web run-detail
    /// page uses this to render the TRIMMED child pipeline (agent → RAI → assemble-ready)
    /// instead of the full 5-stage graph.
    /// </summary>
    [JsonPropertyName("parent_run_id")]
    public string? ParentRunId { get; init; }

    /// <summary>
    /// Identifier of the coordinator subtask this child run executes (Feature 008).
    /// Null for standalone runs and for the coordinator run itself.
    /// </summary>
    [JsonPropertyName("subtask_id")]
    public string? SubtaskId { get; init; }

    /// <summary>
    /// The run_id of the FAILED run this run was retriggered from (POST /api/runs/{id}/retry).
    /// Null for runs not produced by a retry. Lets the UI/MCP surface retry provenance.
    /// </summary>
    [JsonPropertyName("retried_from")]
    public string? RetriedFrom { get; init; }

    /// <summary>
    /// Orchestration status of a COORDINATOR run, sourced from its work plan
    /// (planned | dispatching | awaiting_assembly | assembling | in_review | complete |
    /// assembly_blocked | assembly_failed | assembly_declined). Null for standalone runs, child
    /// runs, and coordinator runs with no work plan yet. The web UI shows this (plus
    /// <see cref="Result"/> for the failure detail) instead of the bare run status so an
    /// awaiting-assembly run is not mislabeled "Failed". (Feature 008)
    /// </summary>
    [JsonPropertyName("coordinator_status")]
    public string? CoordinatorStatus { get; init; }

    /// <summary>
    /// Human-readable detail for a COORDINATOR run's terminal/failure state, sourced from
    /// <see cref="Result"/> (e.g. "assembly_blocked: &lt;reason&gt;", "interrupted: ..."). Scoped to
    /// coordinator runs so the web UI can render "Failed: &lt;reason&gt;" without overloading the
    /// generic run result. Null for standalone runs, child runs, and non-terminal coordinator
    /// runs with no result yet. (Feature 008)
    /// </summary>
    [JsonPropertyName("coordinator_status_reason")]
    public string? CoordinatorStatusReason { get; init; }

    /// <summary>
    /// Current value of the per-run <c>auto-approve-tools</c> option (allow-with-approval tool
    /// requests are auto-granted at the HITL gate; policy denies are unaffected). Reflects launch
    /// value and any live toggle. (Feature 008)
    /// </summary>
    [JsonPropertyName("auto_approve_tools")]
    public bool AutoApproveTools { get; init; }

    /// <summary>
    /// Current value of the coordinator-run Autopilot option (clarifying questions are auto-answered
    /// by the coordinator model; permissions are NOT auto-granted). Reflects launch value and any
    /// live toggle. (Feature 008)
    /// </summary>
    [JsonPropertyName("autopilot")]
    public bool Autopilot { get; init; }

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>Summary of a workflow run returned by GET /api/projects/{id}/runs.</summary>
public sealed record WorkflowRunSummary
{
    [JsonPropertyName("workflow_run_id")]
    public required string WorkflowRunId { get; init; }

    /// <summary>The current active execution's run_id.</summary>
    [JsonPropertyName("execution_id")]
    public required string ExecutionId { get; init; }

    [JsonPropertyName("task")]
    public required string Task { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("agent_name")]
    public string? AgentName { get; init; }

    [JsonPropertyName("reviewed_by")]
    public string? ReviewedBy { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("model_id")]
    public string? ModelId { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>
    /// Orchestration status of a COORDINATOR run, sourced from its work plan (planned | dispatching |
    /// awaiting_assembly | assembling | in_review | complete | assembly_blocked | assembly_failed |
    /// assembly_declined). Null for standalone and child runs. The web runs list shows this (plus
    /// <see cref="Result"/> for the failure detail) so a long-running assembly is not mislabeled.
    /// (Feature 008)
    /// </summary>
    [JsonPropertyName("coordinator_status")]
    public string? CoordinatorStatus { get; init; }

    /// <summary>
    /// Human-readable detail for a COORDINATOR run's terminal/failure state, sourced from
    /// <see cref="Result"/>. Scoped to coordinator runs so the runs list can render
    /// "Failed: &lt;reason&gt;". Null for standalone and child runs. (Feature 008)
    /// </summary>
    [JsonPropertyName("coordinator_status_reason")]
    public string? CoordinatorStatusReason { get; init; }
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

/// <summary>
/// Request body for PUT /api/sandbox-policy. PATCH/preserve semantics: every field except
/// <see cref="RepositoryPath"/> is optional (nullable). An OMITTED/null field preserves the existing
/// stored value; an explicitly provided value (including an empty array, which clears) is applied. This
/// makes partial saves — e.g. the MCP <c>sandbox_policy_set</c> sending only repository_path +
/// shell_enabled — correct-by-construction instead of wiping unspecified fields.
/// </summary>
public sealed record SandboxPolicyUpdateRequest
{
    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("shell_enabled")]
    public bool? ShellEnabled { get; init; }

    [JsonPropertyName("direct")]
    public bool? Direct { get; init; }

    [JsonPropertyName("network_enabled")]
    public bool? NetworkEnabled { get; init; }

    [JsonPropertyName("allowed_repository_roots")]
    public IReadOnlyList<string>? AllowedRepositoryRoots { get; init; }

    [JsonPropertyName("destructive_command_patterns")]
    public IReadOnlyList<string>? DestructiveCommandPatterns { get; init; }

    [JsonPropertyName("require_approval_for_all_shell")]
    public bool? RequireApprovalForAllShell { get; init; }

    [JsonPropertyName("redact_pii")]
    public bool? RedactPii { get; init; }

    [JsonPropertyName("max_output_bytes")]
    public int? MaxOutputBytes { get; init; }
}
public sealed record GitHubRepoResponse(
    string FullName,
    string? Description,
    bool Private,
    string DefaultBranch
);

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

/// <summary>Request body for POST /api/runs/{id}/questions/{requestId}/answer.</summary>
public sealed record AnswerQuestionRequest
{
    /// <summary>The free-text answer supplied to a pending <c>ask_question</c> request.</summary>
    [JsonPropertyName("answer")]
    public string? Answer { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/review.</summary>
public sealed record ReviewRequest
{
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    /// <summary>
    /// When true the reviewer wants the agent to revise rather than hard-declining.
    /// Mutually exclusive with <see cref="Approved"/> = true.
    /// </summary>
    [JsonPropertyName("request_changes")]
    public bool RequestChanges { get; init; }

    /// <summary>Feedback text sent back to the agent for its next iteration.</summary>
    [JsonPropertyName("feedback")]
    public string? Feedback { get; init; }
}

/// <summary>
/// Request body for POST /api/runs/{coordinatorRunId}/assembly/review — the ONE collective human
/// review gate (Feature 008 Phase 3, D5). Mirrors <see cref="ReviewRequest"/> and adds the optional
/// explicit <c>target_files</c> list (D6 step a) used, alongside path tokens parsed from
/// <see cref="Feedback"/>, to infer which children to re-dispatch on request_changes.
/// </summary>
public sealed record AssemblyReviewRequest
{
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    /// <summary>When true the reviewer wants the affected children re-dispatched rather than declining.</summary>
    [JsonPropertyName("request_changes")]
    public bool RequestChanges { get; init; }

    /// <summary>Free-text reviewer feedback; path-like tokens are parsed for rejection inference.</summary>
    [JsonPropertyName("feedback")]
    public string? Feedback { get; init; }

    /// <summary>Optional explicit list of files the changes should target (augments parsed tokens).</summary>
    [JsonPropertyName("target_files")]
    public IReadOnlyList<string>? TargetFiles { get; init; }
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

    /// <summary>Optional predefined blueprint id to apply on creation (Feature 012).</summary>
    [JsonPropertyName("blueprint_id")] public string? BlueprintId { get; init; }

    /// <summary>Optional inline blueprint (file/generated, catalog roles only) to apply on creation (Feature 012).</summary>
    [JsonPropertyName("blueprint")] public Agentweaver.Api.Blueprints.BlueprintDto? Blueprint { get; init; }
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
    [JsonPropertyName("agent_name")] public string? AgentName { get; init; }
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

// -----------------------------------------------------------------------
// Casting
// -----------------------------------------------------------------------

public sealed record CreateProposalRequest
{
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("template_id")] public string? TemplateId { get; init; }
    [JsonPropertyName("goal")] public string? Goal { get; init; }
    [JsonPropertyName("universe")] public string? Universe { get; init; }
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
    [JsonPropertyName("team_size")] public int? TeamSize { get; init; }
    // mode: "scenario" | "free_text" | "analysis" | "manual"
    [JsonPropertyName("role_ids")] public IReadOnlyList<string>? RoleIds { get; init; }
}

public sealed record AmendProposalRequest
{
    [JsonPropertyName("members")] public IReadOnlyList<ProposedMemberDto>? Members { get; init; }
    [JsonPropertyName("universe")] public string? Universe { get; init; }
}

public sealed record ConfirmProposalRequest
{
    [JsonPropertyName("intent")] public string? Intent { get; init; }
}

public sealed record RoleDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
    [JsonPropertyName("default_model")] public required string DefaultModel { get; init; }
}

public sealed record ProposedMemberDto
{
    [JsonPropertyName("proposed_name")] public required string ProposedName { get; init; }
    [JsonPropertyName("role")] public required RoleDto Role { get; init; }
    [JsonPropertyName("charter_markdown")] public required string CharterMarkdown { get; init; }
    [JsonPropertyName("is_named")] public required bool IsNamed { get; init; }
    [JsonPropertyName("default_model")] public required string DefaultModel { get; init; }
    [JsonPropertyName("justification")] public string? Justification { get; init; }
}

public sealed record TeamTemplateDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("roles")] public required IReadOnlyList<RoleDto> Roles { get; init; }
}

public sealed record CastProposalDto
{
    [JsonPropertyName("proposal_id")] public required string ProposalId { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("universe")] public required string Universe { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<ProposedMemberDto> Members { get; init; }
    [JsonPropertyName("existing_team_present")] public required bool ExistingTeamPresent { get; init; }
    [JsonPropertyName("run_id")] public string? RunId { get; init; }
    [JsonPropertyName("warnings")] public required IReadOnlyList<string> Warnings { get; init; }
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed record TeamMemberDto
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("role_title")] public required string RoleTitle { get; init; }
    [JsonPropertyName("charter_path")] public required string CharterPath { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("default_model")] public required string DefaultModel { get; init; }
    [JsonPropertyName("is_named")] public required bool IsNamed { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("charter_created_at")] public DateTimeOffset? CharterCreatedAt { get; init; }
    [JsonPropertyName("charter_updated_at")] public DateTimeOffset? CharterUpdatedAt { get; init; }
}

public sealed record TeamDto
{
    [JsonPropertyName("project_name")] public required string ProjectName { get; init; }
    [JsonPropertyName("universe")] public required string Universe { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<TeamMemberDto> Members { get; init; }
    [JsonPropertyName("layout")] public required string Layout { get; init; }
    [JsonPropertyName("migration_available")] public required bool MigrationAvailable { get; init; }
}

public sealed record CharterDto
{
    [JsonPropertyName("member_name")] public required string MemberName { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

public sealed record HistoryDto
{
    [JsonPropertyName("member_name")] public required string MemberName { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

public sealed record AddMemberRequest
{
    [JsonPropertyName("role_id")] public required string RoleId { get; init; }
    [JsonPropertyName("custom_role_title")] public string? CustomRoleTitle { get; init; }
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
}

public sealed record ReroleRequest
{
    [JsonPropertyName("new_role_id")] public required string NewRoleId { get; init; }
    [JsonPropertyName("custom_role_title")] public string? CustomRoleTitle { get; init; }
}

public sealed record UpdateCharterRequest
{
    [JsonPropertyName("content")] public required string Content { get; init; }
}

// -----------------------------------------------------------------------
// Sync
// -----------------------------------------------------------------------

public sealed record SyncChangeDto
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

public sealed record SyncStatusResponse
{
    [JsonPropertyName("changes")] public required IReadOnlyList<SyncChangeDto> Changes { get; init; }
    [JsonPropertyName("change_set_hash")] public required string ChangeSetHash { get; init; }
    [JsonPropertyName("nothing_to_sync")] public required bool NothingToSync { get; init; }
}

public sealed record SyncCommitRequest
{
    [JsonPropertyName("expected_change_set_hash")] public string? ExpectedChangeSetHash { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

// -----------------------------------------------------------------------
// Memory / Decision Inbox
// -----------------------------------------------------------------------

public sealed record SubmitDecisionInboxRequest
{
    [JsonPropertyName("agent_name")] public string? AgentName { get; init; }
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed record CreateDecisionRequest
{
    [JsonPropertyName("agent_name")] public string? AgentName { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
    [JsonPropertyName("tags")] public string? Tags { get; init; }
}

public sealed record UpdateDecisionRequest
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed record RecordMemoryRequest
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("importance")] public string? Importance { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("tags")] public string? Tags { get; init; }
}

public sealed record StartSessionRequest
{
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("focus_area")] public string? FocusArea { get; init; }
    [JsonPropertyName("active_issues")] public string? ActiveIssues { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
}

public sealed record UpdateSessionRequest
{
    [JsonPropertyName("focus_area")] public string? FocusArea { get; init; }
    [JsonPropertyName("active_issues")] public string? ActiveIssues { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("end")] public bool? End { get; init; }
}

// -----------------------------------------------------------------------
// Coordinator (Feature 008 Phase 1) — outcome-spec flow.
// These contracts use camelCase JSON to match the web client (apps/web/src/api/types.ts).
// -----------------------------------------------------------------------

/// <summary>Request body for POST /api/projects/{id}/orchestrations.</summary>
public sealed record StartOrchestrationRequest
{
    [JsonPropertyName("goal")] public string? Goal { get; init; }
    [JsonPropertyName("modelId")] public string? ModelId { get; init; }

    /// <summary>When true, the coordinator run and its children auto-grant allow-with-approval tool
    /// requests at the HITL gate (policy denies still apply). Defaults to false. (Feature 008)</summary>
    [JsonPropertyName("autoApproveTools")] public bool AutoApproveTools { get; init; }

    /// <summary>When true, the coordinator auto-answers clarifying questions (its own and bubbled
    /// child questions) using the coordinator model. Permissions are NOT auto-granted. Cascades to
    /// children. Defaults to false. (Feature 008)</summary>
    [JsonPropertyName("autopilot")] public bool Autopilot { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/auto-approve and POST /api/runs/{id}/autopilot.</summary>
public sealed record EnableFlagRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
}

/// <summary>Response body for POST /api/projects/{id}/orchestrations.</summary>
public sealed record StartOrchestrationResponse
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
}

/// <summary>Request body for POST /api/runs/{id}/outcome-spec/revise.</summary>
public sealed record ReviseOutcomeSpecRequest
{
    [JsonPropertyName("feedback")] public string? Feedback { get; init; }
}

/// <summary>
/// Response body for GET /api/runs/{id}/outcome-spec. Field names mirror the web client's
/// <c>OutcomeSpec</c> interface. Server state is rendered as-is (Principle III).
/// </summary>
public sealed record OutcomeSpecResponse
{
    [JsonPropertyName("goal")] public required string Goal { get; init; }
    [JsonPropertyName("desiredOutcome")] public required string DesiredOutcome { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("assumptions")] public required string Assumptions { get; init; }

    [JsonPropertyName("clarifyingQuestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClarifyingQuestions { get; init; }

    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("confirmedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmedBy { get; init; }
}

// -----------------------------------------------------------------------
// Coordinator (Feature 008 Phase 2) — work plan, children, and steering.
// These contracts use camelCase JSON to match the web client and MCP tools,
// mirroring the Phase 1 OutcomeSpecResponse style. The endpoints are thin
// projections of the service views (CoordinatorWorkPlanView / CoordinatorChildView /
// SteeringDirectiveView); server state is rendered as-is (Principle III).
// -----------------------------------------------------------------------

/// <summary>Response body for GET /api/runs/{coordinatorRunId}/work-plan.</summary>
public sealed record WorkPlanResponse
{
    [JsonPropertyName("workPlanId")] public required int WorkPlanId { get; init; }
    [JsonPropertyName("coordinatorRunId")] public required string CoordinatorRunId { get; init; }
    [JsonPropertyName("outcomeSpecId")] public required int OutcomeSpecId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>
    /// Human-readable reason for a terminal/blocked assembly status (assembly_blocked / assembly_failed
    /// / assembly_declined), sourced from the coordinator run's result. Null while the plan is in a
    /// non-failed state. The web UI polls this to render "Failed: &lt;reason&gt;". (Feature 008)
    /// </summary>
    [JsonPropertyName("statusReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReason { get; init; }

    [JsonPropertyName("isolationSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IsolationSummary { get; init; }

    [JsonPropertyName("subtasks")] public required IReadOnlyList<WorkPlanSubtaskResponse> Subtasks { get; init; }
    [JsonPropertyName("dependencies")] public required IReadOnlyList<WorkPlanDependencyResponse> Dependencies { get; init; }
}

/// <summary>A subtask row in <see cref="WorkPlanResponse"/>.</summary>
public sealed record WorkPlanSubtaskResponse
{
    [JsonPropertyName("subtaskId")] public required int SubtaskId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("scope")] public required string Scope { get; init; }
    [JsonPropertyName("assignedAgent")] public required string AssignedAgent { get; init; }
    [JsonPropertyName("selectedModelId")] public required string SelectedModelId { get; init; }
    [JsonPropertyName("phase")] public required string Phase { get; init; }
    [JsonPropertyName("isolation")] public required string Isolation { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("childRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChildRunId { get; init; }
}

/// <summary>A dependency edge in <see cref="WorkPlanResponse"/>: subtaskId depends on dependsOnSubtaskId.</summary>
public sealed record WorkPlanDependencyResponse
{
    [JsonPropertyName("subtaskId")] public required int SubtaskId { get; init; }
    [JsonPropertyName("dependsOnSubtaskId")] public required int DependsOnSubtaskId { get; init; }
}

/// <summary>An element of the GET /api/runs/{coordinatorRunId}/children response array.</summary>
public sealed record CoordinatorChildResponse
{
    [JsonPropertyName("subtaskId")] public required int SubtaskId { get; init; }
    [JsonPropertyName("childRunId")] public required string ChildRunId { get; init; }
    [JsonPropertyName("subtaskStatus")] public required string SubtaskStatus { get; init; }
    [JsonPropertyName("assignedAgent")] public required string AssignedAgent { get; init; }
    [JsonPropertyName("selectedModelId")] public required string SelectedModelId { get; init; }

    [JsonPropertyName("childRunStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChildRunStatus { get; init; }

    [JsonPropertyName("worktreeBranch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorktreeBranch { get; init; }

    [JsonPropertyName("treeHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TreeHash { get; init; }

    [JsonPropertyName("stepCount")] public required int StepCount { get; init; }
}

/// <summary>Request body for POST /api/runs/{coordinatorRunId}/steer.</summary>
public sealed record SteerRequest
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("target_child_run_id")] public string? TargetChildRunId { get; init; }
    [JsonPropertyName("instruction")] public string? Instruction { get; init; }
}

/// <summary>
/// Response body for POST /api/runs/{coordinatorRunId}/steer. Mirrors the persisted steering
/// directive (SteeringDirectiveView). Server state is rendered as-is (Principle III).
/// </summary>
public sealed record SteeringDirectiveResponse
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("coordinatorRunId")] public required string CoordinatorRunId { get; init; }

    [JsonPropertyName("targetChildRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetChildRunId { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("instruction")] public required string Instruction { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("createdBy")] public required string CreatedBy { get; init; }
    [JsonPropertyName("createdAt")] public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("relayedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RelayedAt { get; init; }
}

// -----------------------------------------------------------------------
// Backlog & Workflow Kanban board (Feature 009). snake_case JSON, mirroring
// the project/run endpoint conventions.
// -----------------------------------------------------------------------

/// <summary>Request body for POST /api/projects/{projectId}/backlog/tasks.</summary>
public sealed record CaptureBacklogTaskRequest
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>Request body for PATCH /api/projects/{projectId}/backlog/tasks/{taskId}.</summary>
public sealed record EditBacklogTaskRequest
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>Request body for the move-to-ready / move-to-backlog endpoints.</summary>
public sealed record MoveBacklogTaskRequest
{
    [JsonPropertyName("target_index")] public int? TargetIndex { get; init; }
}

/// <summary>Request body for POST /api/projects/{projectId}/backlog/tasks/{taskId}/reorder.</summary>
public sealed record ReorderBacklogTaskRequest
{
    [JsonPropertyName("target_index")] public int TargetIndex { get; init; }
}

/// <summary>Full backlog-task projection returned by capture/edit/move/reorder.</summary>
public sealed record BacklogTaskDto
{
    [JsonPropertyName("task_id")] public required string TaskId { get; init; }
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("order_key")] public required string OrderKey { get; init; }
    [JsonPropertyName("captured_by")] public required string CapturedBy { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("committed_at")] public DateTimeOffset? CommittedAt { get; init; }
    [JsonPropertyName("claimed_at")] public DateTimeOffset? ClaimedAt { get; init; }
    [JsonPropertyName("run_id")] public string? RunId { get; init; }
    [JsonPropertyName("archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>Per-project pickup settings (FR-008a + unattended seeding).</summary>
public sealed record BacklogSettingsDto
{
    [JsonPropertyName("max_ready_per_heartbeat")] public required int MaxReadyPerHeartbeat { get; init; }
    [JsonPropertyName("pickup_autopilot")] public required bool PickupAutopilot { get; init; }
    [JsonPropertyName("pickup_auto_approve_tools")] public required bool PickupAutoApproveTools { get; init; }
}

/// <summary>A single workflow-stage column descriptor.</summary>
public sealed record WorkflowStageDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
}

/// <summary>Response body for GET /api/projects/{projectId}/workflow-stages.</summary>
public sealed record WorkflowStagesResponse
{
    [JsonPropertyName("available")] public required bool Available { get; init; }
    [JsonPropertyName("stages")] public required IReadOnlyList<WorkflowStageDto> Stages { get; init; }
}

/// <summary>A Backlog/Ready intake card.</summary>
public sealed record TaskCardDto
{
    [JsonPropertyName("kind")] public string Kind => "task";
    [JsonPropertyName("task_id")] public required string TaskId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("order_key")] public required string OrderKey { get; init; }
    [JsonPropertyName("captured_by")] public required string CapturedBy { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("committed_at")] public DateTimeOffset? CommittedAt { get; init; }
    [JsonPropertyName("archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>A coordinator-run card placed in a workflow column.</summary>
public sealed record RunCardDto
{
    [JsonPropertyName("kind")] public string Kind => "run";
    [JsonPropertyName("run_id")] public required string RunId { get; init; }
    [JsonPropertyName("workflow_run_id")] public string? WorkflowRunId { get; init; }
    [JsonPropertyName("backlog_task_id")] public string? BacklogTaskId { get; init; }
    [JsonPropertyName("task")] public required string Task { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("work_plan_status")] public string? WorkPlanStatus { get; init; }
    [JsonPropertyName("assembly_stage")] public string? AssemblyStage { get; init; }
    [JsonPropertyName("stage_id")] public required string StageId { get; init; }
    [JsonPropertyName("agent_name")] public string? AgentName { get; init; }
    [JsonPropertyName("retried_from")] public string? RetriedFrom { get; init; }
    [JsonPropertyName("started_at")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>A board column with its cards (TaskCardDto for intake, RunCardDto for workflow).</summary>
public sealed record BoardColumnDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }   // "intake" | "workflow"
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("cards")] public required IReadOnlyList<object> Cards { get; init; }

    [JsonPropertyName("collapsed_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CollapsedCount { get; init; }
}

/// <summary>Response body for GET /api/projects/{projectId}/board.</summary>
public sealed record BoardDto
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("workflow_stages_available")] public required bool WorkflowStagesAvailable { get; init; }
    [JsonPropertyName("columns")] public required IReadOnlyList<BoardColumnDto> Columns { get; init; }

    /// <summary>
    /// Phase 2 project-aggregate per-agent work-queue rollup feeding the homepage "Agents" rail.
    /// One entry per distinct subtask <c>assignedAgent</c> persona aggregated across the active
    /// (non-terminal) coordinator runs backing the board. Empty (never null) when there is no
    /// in-flight subtask work.
    /// </summary>
    [JsonPropertyName("agent_queues")] public required IReadOnlyList<AgentQueueDto> AgentQueues { get; init; }
}

/// <summary>
/// Per-agent work-queue rollup for the project homepage "Agents" rail. Aggregates a persona's
/// subtasks across the active coordinator runs into load buckets. Status -&gt; bucket mapping mirrors
/// the web Phase 1 mapping (apps/web/src/api/agentQueues.ts) exactly.
/// </summary>
public sealed record AgentQueueDto
{
    [JsonPropertyName("agent_name")] public required string AgentName { get; init; }
    [JsonPropertyName("active")] public required int Active { get; init; }     // dispatched | running | in_progress
    [JsonPropertyName("queued")] public required int Queued { get; init; }     // pending + any unrecognized status
    [JsonPropertyName("blocked")] public required int Blocked { get; init; }   // failed | rai_flagged
    [JsonPropertyName("done")] public required int Done { get; init; }         // completed | assemble_ready | merged
    [JsonPropertyName("run_ids")] public required IReadOnlyList<string> RunIds { get; init; }
    [JsonPropertyName("sample_titles")] public required IReadOnlyList<string> SampleTitles { get; init; }

    /// <summary>
    /// The same per-agent rollup grouped by orchestration (coordinator run): one entry per active
    /// coordinator run this agent has subtask work in. Each entry's buckets sum to the agent-level
    /// totals above. Ordered most-loaded orchestration first. Empty (never null) when the agent has
    /// no in-flight work.
    /// </summary>
    [JsonPropertyName("orchestrations")] public required IReadOnlyList<AgentOrchestrationQueueDto> Orchestrations { get; init; }
}

/// <summary>
/// One agent's subtask rollup within a single orchestration (coordinator run), so the Flow page can
/// attribute queued work to the orchestration it belongs to. Buckets use the same status mapping as
/// <see cref="AgentQueueDto"/>; summed across an agent's orchestrations they equal the agent totals.
/// </summary>
public sealed record AgentOrchestrationQueueDto
{
    [JsonPropertyName("run_id")] public required string RunId { get; init; }

    /// <summary>Human orchestration name (the coordinator run's objective). Null when no objective is
    /// available for the run; the web falls back to a short run id.</summary>
    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("active")] public required int Active { get; init; }
    [JsonPropertyName("queued")] public required int Queued { get; init; }
    [JsonPropertyName("blocked")] public required int Blocked { get; init; }
    [JsonPropertyName("done")] public required int Done { get; init; }

    /// <summary>This orchestration's top-3 in-flight (queued + active) subtask titles, deduped.</summary>
    [JsonPropertyName("sample_titles")] public required IReadOnlyList<string> SampleTitles { get; init; }
}

/// <summary>Response body for POST /api/projects/{projectId}/backlog/ready-all (bulk promote).</summary>
public sealed record ReadyAllResponse
{
    [JsonPropertyName("moved")] public required int Moved { get; init; }
}

/// <summary>Response body for POST /api/runs/{id}/retry — the freshly created retry run.</summary>
public sealed record RetryRunResponse
{
    [JsonPropertyName("run_id")] public required string RunId { get; init; }
    [JsonPropertyName("retried_from")] public required string RetriedFrom { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
}
