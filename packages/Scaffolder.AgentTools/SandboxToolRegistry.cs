using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools;

/// <summary>
/// Assembles the list of AIFunctions to register with the model from all registered ISandboxTool implementations.
/// Both runners call this to get their tool list.
/// </summary>
public static class SandboxToolRegistry
{
    /// <summary>
    /// Builds the list of AIFunctions for the given context.
    /// run_command is included only when executor.IsRealIsolation and options.ShellEnabled.
    /// </summary>
    public static IList<AIFunction> Build(SandboxToolContext context)
    {
        var tools = new List<ISandboxTool>
        {
            new Tools.ReadFileTool(),
            new Tools.StrReplaceEditorTool(),
            new Tools.ApplyPatchTool(),
            new Tools.CreateFileTool(),
            new Tools.EditFileTool(),
            new Tools.GrepSearchTool(),
            new Tools.FileSearchTool(),
            new Tools.ReportIntentTool(),
        };

        if (context.Executor.IsRealIsolation && context.Options.ShellEnabled)
            tools.Insert(0, new Tools.RunCommandTool());

        return tools.Select(t => t.CreateFunction(context)).ToList();
    }

    /// <summary>Returns the canonical tool names for AvailableTools construction.</summary>
    public static string[] GetToolNames(bool includeShell) =>
        includeShell
            ? ["run_command", "read_file", "grep_search", "file_search", "str_replace_editor", "apply_patch", "create", "edit", "report_intent"]
            : ["read_file", "grep_search", "file_search", "str_replace_editor", "apply_patch", "create", "edit", "report_intent"];
}
