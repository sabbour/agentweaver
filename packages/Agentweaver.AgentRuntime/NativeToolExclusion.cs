using Agentweaver.AgentTools;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Produces the AvailableTools allowlist and ExcludedTools blocklist for the Copilot SDK
/// SessionConfig. AvailableTools is the single authority — only our registered custom tools
/// are presented to the model. ExcludedTools is defense-in-depth for truly dangerous native
/// tools that should never run regardless of the allowlist.
/// </summary>
internal static class NativeToolExclusion
{
    /// <summary>Returns the AvailableTools allowlist. Conditionally includes run_command.</summary>
    public static string[] AvailableToolNames(bool includeShell) =>
        SandboxToolRegistry.GetToolNames(includeShell);

    /// <summary>
    /// Native-only tools to block as defense-in-depth. These are NOT our custom tools —
    /// our tools are controlled via AvailableTools. This list targets dangerous Copilot CLI
    /// native tools that must never execute even if AvailableTools were misconfigured.
    /// </summary>
    public static string[] ExcludedToolNames() =>
    [
        "shell", "bash",                           // native shell bypass
        "glob", "ls", "grep", "read", "write",     // native filesystem enumeration/access
        "view",                                    // native file-read (str_replace_editor view cmd)
        "exit_plan_mode",                          // internal SDK orchestration
        "store_memory", "vote_memory",             // native memory tools (scoped out)
        "update_todo",                             // native todo tool (scoped out)
        "semantic_search",                         // no local embeddings
        "task",                                    // subagent orchestration (scoped out)
        "notebook",                                // scoped out
        "webfetch", "web_fetch", "websearch", "web_search",  // network tools

        // Block native SDK versions of tools we've overridden with custom implementations.
        // Our custom tools in SessionConfig.Tools override these via overridesBuiltInTool=true.
        // Without this exclusion, the native and custom versions conflict and the SDK may
        // present neither (or the wrong one) to the model.
        "read_file", "write_file", "create_file",
        "str_replace_editor", "apply_patch",
        "grep_search", "file_search", "report_intent",
    ];
}
