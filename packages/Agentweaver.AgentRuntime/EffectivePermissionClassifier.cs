using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

internal static class EffectivePermissionClassifier
{
    private static readonly IReadOnlySet<string> WorkspaceReadTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file",
        "list_directory",
    };

    private static readonly IReadOnlySet<string> WorkspaceSearchTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "grep_search",
        "file_search",
        "glob",
    };

    private static readonly IReadOnlySet<string> WorkspaceWriteTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "write_file",
        "create_file",
        "str_replace_editor",
        "apply_patch",
    };

    private static readonly IReadOnlySet<string> AgentweaverReadTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_inbox",
        "list_decisions",
        "get_decision_history",
        "get_memory",
        "get_memory_history",
        "project_get",
        "project_list_runs",
        "backlog_get_board",
        "backlog_get_task",
        "run_status",
        "run_show_artifacts",
        "coordinator_work_plan_get",
        "coordinator_children_get",
        "orchestration_topology",
    };

    private static readonly IReadOnlySet<string> AgentweaverWriteTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "submit_decision",
        "record_memory",
        "update_session",
        "submit_inbox_entry",
        "merge_inbox_entry",
        "export_memory",
        "backlog_capture_task",
    };

    private static readonly IReadOnlySet<string> PreviewTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_preview_process",
        "stop_preview_process",
        "observe_bound_port",
        "health_check",
        "start_preview",
    };

    internal static string? Classify(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return null;
        if (toolName is "report_intent" or "report_outcome")
            return EffectivePermissionOperations.Observe;
        if (toolName == "ask_question")
            return EffectivePermissionOperations.HumanInteraction;
        if (WorkspaceReadTools.Contains(toolName))
            return EffectivePermissionOperations.WorkspaceRead;
        if (WorkspaceSearchTools.Contains(toolName))
            return EffectivePermissionOperations.WorkspaceSearch;
        if (WorkspaceWriteTools.Contains(toolName))
            return EffectivePermissionOperations.WorkspaceWrite;
        if (toolName == "run_command")
            return EffectivePermissionOperations.ShellExecute;
        if (toolName == "web_fetch")
            return EffectivePermissionOperations.NetworkAccess;
        if (AgentweaverReadTools.Contains(toolName))
            return EffectivePermissionOperations.AgentweaverRead;
        if (AgentweaverWriteTools.Contains(toolName))
            return EffectivePermissionOperations.AgentweaverWrite;
        if (PreviewTools.Contains(toolName))
            return EffectivePermissionOperations.PreviewManage;
        return OperatorToolApprovalPolicy.ClassifyEffectivePermission(toolName);
    }

    internal static (bool Allowed, string Reason, string? Operation) Evaluate(
        EffectivePermissionBinding? binding,
        string? toolName)
    {
        if (binding is null)
            return (false, "Operation denied: no effective permission binding is active.", null);

        var operation = Classify(toolName);
        if (operation is null)
        {
            return (
                false,
                $"Operation denied by effective permission binding {binding.BindingId} " +
                $"({binding.Version}, source={binding.Source}): tool '{toolName ?? "(null)"}' is unclassified.",
                null);
        }

        return binding.Allows(operation)
            ? (true, string.Empty, operation)
            : (
                false,
                $"Operation denied by effective permission binding {binding.BindingId} " +
                $"({binding.Version}, source={binding.Source}): '{operation}' is not allowed.",
                operation);
    }
}
