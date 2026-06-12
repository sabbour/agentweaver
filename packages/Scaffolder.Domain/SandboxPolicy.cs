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
    /// Run shell commands directly without any sandbox isolation layer (bwrap/mxc).
    /// Use only when the deployment environment itself provides isolation (e.g. a
    /// container). When true, <c>run_command</c> executes via the host shell directly.
    /// Default: false.
    /// </summary>
    public bool Direct { get; init; } = false;

    /// <summary>
    /// Allow outbound network access inside the sandbox.
    /// Default: false (blocked — safer default for scaffolders vs Copilot CLI which defaults to true).
    /// When true, passes <c>NetworkPolicy { AllowOutbound = true }</c> to the sandbox engine.
    /// </summary>
    public bool NetworkEnabled { get; init; } = false;

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
        // File deletion
        "rm -rf", "rm -fr", "rm -r /", "shred ", "wipe ",
        "find / -delete", "find / -exec rm",
        "truncate --size 0",
        // Disk and filesystem
        "dd if=", "mkfs", "fdisk", "parted ", "wipefs",
        "> /dev/sd", "> /dev/hd", "> /dev/nvme",
        // Windows CMD destructive
        "del /s", "rd /s /q", "format ", "cipher /w",
        // Privilege escalation and system accounts
        "chmod -R 777", "chmod -R 0777", "chown -R root",
        "sudo rm", "sudo mkfs", "sudo dd",
        "passwd ", "visudo",
        // Process and system control
        "kill -9", "pkill -9", "killall ",
        "shutdown ", "reboot", "halt", "poweroff", "init 0", "init 6",
        "systemctl stop", "systemctl disable", "service stop",
        // Remote code execution from internet
        "curl | sh", "curl | bash", "wget | sh", "wget | bash",
        "bash <(curl", "sh <(curl", "eval $(curl", "eval $(wget",
        "| bash", "| sh",
        // Git destructive
        "git push --force", "git push -f",
        "git reset --hard",
        "git push origin --delete", "git push --delete",
        "git branch -D",
        "git clean -fd", "git clean -fxd",
        // PowerShell destructive
        "Remove-Item -Recurse", "Remove-Item -Force", "ri -r", "ri -Recurse",
        "Format-Volume", "Clear-Disk",
        "Stop-Process -Force",
        "Set-ExecutionPolicy Unrestricted", "Set-ExecutionPolicy Bypass",
        "Invoke-Expression", "iex ",
        "[System.IO.File]::Delete",
        "Get-ChildItem | Remove-Item",
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
