namespace Scaffolder.AgentRuntime;

/// <summary>
/// Configuration for sandbox execution. Bound from the "Sandbox" section.
/// </summary>
public sealed class SandboxOptions
{
    public const string Section = "Sandbox";

    /// <summary>Whether shell execution via run_command is enabled. Default: true.</summary>
    public bool ShellEnabled { get; set; } = true;

    /// <summary>Max output size in bytes from a sandboxed command. Default: 4 MB.</summary>
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Command patterns that require human approval before execution.
    /// Default patterns: rm -rf, del /s, format, mkfs, dd if=, git push --force, git reset --hard.
    /// </summary>
    public string[] DestructiveCommandPatterns { get; set; } =
    [
        "rm -rf", "del /s", "format ", "mkfs", "dd if=",
        "git push --force", "git reset --hard",
    ];

    /// <summary>When true, ALL shell commands require human approval (not just destructive ones).</summary>
    public bool RequireApprovalForAllShell { get; set; } = false;

    /// <summary>Whether to redact PII (emails, IPs) from command output. Default: true.</summary>
    public bool RedactPii { get; set; } = true;

    /// <summary>
    /// Allowed repository roots accessible as read-only inside the sandbox.
    /// If empty, only the working directory is accessible.
    /// </summary>
    public string[] AllowedRepositoryRoots { get; set; } = [];
}
