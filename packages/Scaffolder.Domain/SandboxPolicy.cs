namespace Scaffolder.Domain;

/// <summary>
/// Per-project sandbox execution policy. Stored in the database keyed by repository path.
/// When no policy exists for a repository, the default values below apply.
/// </summary>
public sealed record SandboxPolicy
{
    /// <summary>The repository path this policy applies to.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Whether shell execution via run_command is allowed. Default: true.</summary>
    public bool ShellEnabled { get; init; } = true;

    /// <summary>
    /// Additional repository roots accessible as read-only inside the sandbox.
    /// If empty, only the working directory is accessible. Default: empty.
    /// </summary>
    public IReadOnlyList<string> AllowedRepositoryRoots { get; init; } = [];

    /// <summary>
    /// Command patterns that trigger human approval before execution.
    /// Default: common destructive shell patterns.
    /// </summary>
    public IReadOnlyList<string> DestructiveCommandPatterns { get; init; } =
    [
        "rm -rf", "del /s", "format ", "mkfs", "dd if=",
        "git push --force", "git reset --hard",
    ];

    /// <summary>When true, ALL shell commands require human approval.</summary>
    public bool RequireApprovalForAllShell { get; init; } = false;

    /// <summary>Whether to redact PII (emails, IPs) from command output. Default: true.</summary>
    public bool RedactPii { get; init; } = true;

    /// <summary>Max output bytes from a sandboxed command. Default: 4 MB.</summary>
    public int MaxOutputBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Returns default policy for the given repository path.</summary>
    public static SandboxPolicy Default(string repositoryPath) => new() { RepositoryPath = repositoryPath };
}
