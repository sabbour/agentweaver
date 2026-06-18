namespace Agentweaver.AgentTools;

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

    /// <summary>
    /// Command patterns that require human approval before execution.
    /// Matched case-insensitively after whitespace normalization.
    /// </summary>
    public string[] DestructiveCommandPatterns { get; init; } = [];

    /// <summary>When true, ALL shell commands require human approval (not just destructive ones).</summary>
    public bool RequireApprovalForAllShell { get; init; } = false;

    /// <summary>
    /// Allow outbound network inside the sandbox. Default: false.
    /// Mirrors <c>SandboxPolicy.NetworkEnabled</c>.
    /// </summary>
    public bool NetworkEnabled { get; init; } = false;
}
