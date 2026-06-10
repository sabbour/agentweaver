namespace Scaffolder.AgentTools;

/// <summary>
/// Tool-relevant subset of sandbox options. Populated from SandboxOptions in AgentRuntime.
/// </summary>
public sealed record SandboxToolOptions(
    bool ShellEnabled,
    int DefaultTimeoutMs = 30_000)
{
    /// <summary>
    /// Allowed repository roots accessible as read-only inside the sandbox.
    /// If empty, only the working directory is accessible.
    /// </summary>
    public string[] AllowedRepositoryRoots { get; init; } = [];
}
