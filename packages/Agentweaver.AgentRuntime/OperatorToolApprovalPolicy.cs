namespace Agentweaver.AgentRuntime;

/// <summary>
/// Classifies which AgentweaverMCP tools the operator assistant must gate behind a human-approval
/// prompt before invoking. The operator assistant drives the platform through the full MCP tool set
/// (~91 tools), the large majority of which are read-only discovery or low-consequence writes that
/// should run without interrupting the conversation. Only the consequential actions the operator
/// runtime addendum (see <see cref="OperatorAssistantAgent"/>) already tells the model are gated are
/// held for approval:
///
///   - start budget-consuming work (dispatch a run, start the coordinator, start a preview);
///   - delete or archive (projects, backlog tasks, runs, skills, team members);
///   - stop / steer live work;
///   - confirm an outcome;
///   - approve / reject a review or a decision, and merge a decision.
///
/// The MCP tool declarations carry no machine-readable approval annotation, so the gated set is an
/// explicit allow-list keyed by tool name. New tools are NOT gated by default, which preserves the
/// existing behavior for the read/discovery majority; extend <see cref="GatedTools"/> when a new
/// consequential tool is added to the MCP surface.
/// </summary>
public static class OperatorToolApprovalPolicy
{
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
    };

    /// <summary>
    /// Returns <see langword="true"/> when invoking <paramref name="toolName"/> must be held for an
    /// operator approval decision before it runs.
    /// </summary>
    public static bool RequiresApproval(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && GatedTools.Contains(toolName);
}
