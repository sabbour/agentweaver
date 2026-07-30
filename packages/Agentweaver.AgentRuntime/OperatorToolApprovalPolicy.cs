namespace Agentweaver.AgentRuntime;

/// <summary>
/// Classifies which AgentweaverMCP tools the operator assistant must gate behind a human-approval
/// prompt before invoking. The operator assistant drives the platform through the full MCP tool set
/// (~97 tools). This policy is <b>fail-closed</b>: a tool runs without an approval prompt ONLY when
/// it is on the explicit <see cref="UngatedTools"/> allow-list of read/discovery and low-consequence
/// operations. Every other tool — the consequential mutators in <see cref="GatedTools"/> AND any
/// tool name this policy does not recognize (e.g. a newly added MCP tool) — requires an operator
/// decision before it runs.
///
/// <para>
/// SECURITY (XPIA): the previous behaviour approved anything not on the gated deny-list, so a new
/// consequential tool (e.g. <c>sandbox_policy_set</c>, <c>memory_import</c>, <c>skill_import</c>)
/// silently ran without consent — prompt-injected content could weaken the sandbox policy or persist
/// malicious instructions. Failing closed means an unclassified/new tool is held for approval by
/// default; adding it to <see cref="UngatedTools"/> is a deliberate, reviewable act.
/// </para>
///
/// <para>
/// The MCP tool declarations carry no machine-readable approval annotation, so classification is an
/// explicit allow-list (<see cref="UngatedTools"/>) plus deny-list (<see cref="GatedTools"/>). The
/// <c>OperatorToolApprovalPolicyCoverageTests</c> reflect over the MCP tool surface and fail CI if any
/// tool is unclassified, so the two lists cannot silently drift out of sync with the tool set.
/// </para>
/// </summary>
public static class OperatorToolApprovalPolicy
{
    /// <summary>
    /// Consequential tools that ALWAYS require an operator approval before they run: they start
    /// budget-consuming work, delete/archive, stop/steer live work, confirm an outcome, approve or
    /// merge a decision, or mutate security-relevant configuration (sandbox policy, memory/skill
    /// imports, skill assignment, workflow definitions).
    /// </summary>
    private static readonly HashSet<string> GatedTools = new(StringComparer.Ordinal)
    {
        // Start budget-consuming work.
        "coordinator_start",
        "run_submit",
        "run_task",
        "run_retry",
        "start_preview",
        "session_start",

        // Steer / stop live work.
        "coordinator_steer",

        // Confirm an outcome.
        "coordinator_outcome_spec_confirm",

        // Approve / reject review, and merge / decide.
        "run_review",
        "decision_inbox_merge",
        "decision_inbox_reject",
        "squad_decide",

        // Delete / archive.
        "project_delete",
        "backlog_delete_task",
        "backlog_archive_task",
        "send_all_backlog_to_ready",
        "run_archive",
        "skill_delete",
        "team_member_retire",

        // Security-relevant configuration / persistence mutators. Prompt-injected content could use
        // these to weaken the sandbox boundary or persist malicious skills/memory/workflows without
        // consent, so they are gated (findings-agent-runtime Alert 5).
        "sandbox_policy_set",
        "memory_import",
        "skill_import",
        "skill_assign",
        // Registers an EXTERNAL, project-scoped marketplace content source by URL that later browses/
        // imports pull from — an XPIA vector where injected content could point the marketplace at an
        // attacker-controlled repo. Its removal is a symmetric state mutation.
        "skill_marketplace_source_add",
        "skill_marketplace_source_remove",
        "workflow_save",
    };

    /// <summary>
    /// Read/discovery tools and low-consequence writes that run WITHOUT an approval prompt. This is
    /// the fail-closed allow-list: anything not present here (and not in <see cref="GatedTools"/>)
    /// requires approval by default. Keep in sync with the MCP tool surface — the coverage test
    /// enforces that every MCP tool is classified into exactly one of the two lists.
    /// </summary>
    private static readonly HashSet<string> UngatedTools = new(StringComparer.Ordinal)
    {
        // Backlog: reads + in-place, reversible edits/moves.
        "backlog_capture_task",
        "backlog_decompose_spec",
        "backlog_edit_task",
        "backlog_get_board",
        "backlog_get_settings",
        "backlog_get_task",
        "backlog_get_workflow_stages",
        "backlog_move_to_backlog",
        "backlog_move_to_ready",
        "backlog_reorder_task",
        "backlog_set_settings",

        // Blueprints / catalog / topology reads.
        "blueprint_generate",
        "catalog_list_roles",
        "catalog_list_scenarios",
        "list_blueprints",
        "orchestration_topology",
        "validate_blueprint",

        // Coordinator reads + reversible spec drafting.
        "coordinator_children_get",
        "coordinator_outcome_spec_get",
        "coordinator_outcome_spec_revise",
        "coordinator_work_plan_get",

        // Decisions: reads + submit/create/update (draft-level, not merge/reject).
        "decision_create",
        "decision_inbox_list",
        "decision_inbox_submit",
        "decision_list",
        "decision_update",

        // Diagnostics / health.
        "diagnostics_get",
        "heartbeat_status",

        // Project workspace reads.
        "get_project_workspace_file",
        "list_project_workspace",
        "list_project_workspace_refs",

        // GitHub auth status + read-only account/repo listing (session-scoped, no platform state mutation).
        "github_accounts_list",
        "github_repos_list",
        "github_signin",
        "github_signout",
        "github_status",

        // Memory: reads + record/export (import is gated).
        "memory_export",
        "memory_get",
        "memory_list",
        "memory_record",
        "memory_search",

        // Projects: reads + create/rename/configure (delete is gated).
        "project_configure",
        "project_create",
        "project_get",
        "project_list",
        "project_list_runs",
        "project_rename",

        // Runs: reads (submit/task/retry/review/archive are gated).
        "run_get_file",
        "run_show_artifacts",
        "run_status",
        "run_watch",

        // Sandbox policy read (set is gated).
        "sandbox_policy_get",

        // Session reads + in-place update (start is gated).
        "session_current",
        "session_update",

        // Skills: reads + create/generate/preview + reversible assignment lifecycle (import/assign/
        // delete are gated).
        "skill_assignments_list",
        "skill_create",
        "skill_defaults_apply",
        "skill_defaults_preview",
        "skill_generate",
        "skill_get",
        "skill_import_preview",
        "skill_list",
        "skill_marketplace_browse",
        "skill_marketplace_import",
        "skill_marketplace_sources_list",
        "skill_marketplaces_list",
        "skill_sync",
        "skill_unassign",

        // Team: reads + add member (retire is gated).
        "team_cast",
        "team_get",
        "team_member_add",
        "team_member_get_charter",

        // Workflows: reads + generate/sync (save is gated).
        "workflow_generate",
        "workflow_get",
        "workflows_list",
        "workflows_sync",
    };

    /// <summary>
    /// Returns <see langword="true"/> when invoking <paramref name="toolName"/> must be held for an
    /// operator approval decision before it runs. Fail-closed: an empty, null, or unrecognized tool
    /// name (including any new MCP tool not yet added to <see cref="UngatedTools"/>) requires approval.
    /// </summary>
    public static bool RequiresApproval(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return true;

        // Explicit low-consequence allow-list runs without a prompt.
        if (UngatedTools.Contains(toolName))
            return false;

        // Gated tools and everything unrecognized require approval.
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> has an explicit classification
    /// (gated or ungated). Used by coverage tests to ensure every MCP tool is deliberately classified,
    /// so a newly added tool cannot silently rely on the fail-closed default forever.
    /// </summary>
    public static bool IsClassified(string? toolName) =>
        !string.IsNullOrEmpty(toolName)
        && (GatedTools.Contains(toolName) || UngatedTools.Contains(toolName));
}
