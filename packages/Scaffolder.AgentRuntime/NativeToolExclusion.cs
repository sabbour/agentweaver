namespace Scaffolder.AgentRuntime;

/// <summary>
/// Produces the AvailableTools allowlist and ExcludedTools blocklist for the Copilot SDK
/// SessionConfig. These control which tools the model can invoke — AvailableTools is the
/// primary enforcement (server-side allowlist); ExcludedTools is defense-in-depth.
/// </summary>
internal static class NativeToolExclusion
{
    // The canonical names of our 8 unconditional custom tools
    private static readonly string[] UnconditionalTools =
    [
        "read_file", "grep_search", "file_search",
        "str_replace_editor", "apply_patch", "create", "edit",
        "report_intent",
    ];

    private const string ShellToolName = "run_command";

    /// <summary>Returns the AvailableTools allowlist. Conditionally includes run_command.</summary>
    public static string[] AvailableToolNames(bool includeShell) =>
        includeShell
            ? [ShellToolName, ..UnconditionalTools]
            : UnconditionalTools;

    /// <summary>
    /// Returns native tool names to block as defense-in-depth (ExcludedTools).
    /// AvailableTools is the primary enforcement; ExcludedTools is belt-and-suspenders.
    /// </summary>
    public static string[] ExcludedToolNames() =>
    [
        "shell", "bash",                          // native shell
        "store_memory", "vote_memory",             // native memory
        "update_todo",                             // native todo
        "semantic_search",                         // no local embeddings API
        "task",                                    // scoped out
        "notebook",                                // scoped out
    ];
}
