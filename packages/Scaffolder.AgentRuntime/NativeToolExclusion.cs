using Scaffolder.AgentTools;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Produces the AvailableTools allowlist and ExcludedTools blocklist for the Copilot SDK
/// SessionConfig. These control which tools the model can invoke — AvailableTools is the
/// primary enforcement (server-side allowlist); ExcludedTools is defense-in-depth.
/// </summary>
internal static class NativeToolExclusion
{
    /// <summary>Returns the AvailableTools allowlist. Conditionally includes run_command.</summary>
    public static string[] AvailableToolNames(bool includeShell) =>
        SandboxToolRegistry.GetToolNames(includeShell);

    /// <summary>
    /// Returns native tool names to block as defense-in-depth (ExcludedTools).
    /// AvailableTools is the primary enforcement; ExcludedTools is belt-and-suspenders.
    /// Source: static analysis of Copilot CLI bundle (specs/002-sandboxed-execution/copilot-builtin-tools.md).
    /// </summary>
    public static string[] ExcludedToolNames() =>
    [
        "shell", "bash",                          // native shell (always denied)
        "store_memory", "vote_memory",             // native memory tools (scoped out)
        "update_todo",                             // native todo tool (scoped out)
        "semantic_search",                         // no local embeddings API
        "task",                                    // subagent orchestration (scoped out)
        "notebook",                                // scoped out

        // Override tools: native bundle equivalents blocked as defense-in-depth.
        // Our custom implementations replace these; the native versions must not
        // execute because they use absolute paths and bypass sandbox containment.
        "read_file", "str_replace_editor", "apply_patch",
        "create", "edit", "grep_search", "file_search", "report_intent",

        // Other dangerous native tools not overridden by custom implementations.
        // These appear in the bundle permission map (copilot-builtin-tools.md).
        "view",         // native file-read (str_replace_editor "view" command)
        "glob",         // native filesystem enumeration
        "ls",           // native directory listing
        "grep",         // native text search
        "read",         // alias for view
        "write",        // native file-write
        "exit_plan_mode", // internal SDK orchestration tool

        // Network-access tools (MCP-delivered, appear in native permission map)
        "webfetch", "web_fetch", "websearch", "web_search",
    ];
}
