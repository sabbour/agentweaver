using Agentweaver.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// GitOps-backed <see cref="ISandboxPolicyStore"/>. Reads and writes the
/// <c>sandbox:</c> section of <c>.agentweaver/settings.yml</c> in the project
/// repository root. Other sections in the file are preserved on write.
/// When the file does not exist or has no sandbox section, the default policy is returned.
/// <see cref="SetPolicyAsync"/> writes the file; the operator commits it.
/// </summary>
public sealed class YamlSandboxPolicyStore : ISandboxPolicyStore
{
    private readonly IProjectStore _projectStore;

    public YamlSandboxPolicyStore(IProjectStore projectStore)
    {
        _projectStore = projectStore;
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static string SettingsFilePath(string repositoryPath) =>
        Path.Combine(repositoryPath, ".agentweaver", "settings.yml");

    public Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        var filePath = SettingsFilePath(repositoryPath);
        if (!File.Exists(filePath))
            return Task.FromResult(SandboxPolicy.Default(repositoryPath));

        try
        {
            var yaml = File.ReadAllText(filePath);
            var root = Deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? new(StringComparer.OrdinalIgnoreCase);
            SandboxPolicyYamlDto dto = new();
            if (TryGetValue(root, "sandbox", out var sandboxNode) && sandboxNode is not null)
                dto = Deserializer.Deserialize<SandboxPolicyYamlDto>(Serializer.Serialize(sandboxNode)) ?? new();
            return Task.FromResult(dto.ToDomain(repositoryPath));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
        {
            // Malformed or unreadable file — return defaults rather than crashing the run.
            return Task.FromResult(SandboxPolicy.Default(repositoryPath));
        }
    }

    public async Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default)
    {
        if (!await IsKnownProjectWorkspaceAsync(policy.RepositoryPath, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Sandbox repository_path must be a known project workspace.");

        var filePath = SettingsFilePath(policy.RepositoryPath);
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        // Preserve existing sections; only update the sandbox section.
        Dictionary<string, object?> root = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(filePath))
        {
            try
            {
                var existing = File.ReadAllText(filePath);
                root = Deserializer.Deserialize<Dictionary<string, object?>>(existing) ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch (YamlDotNet.Core.YamlException) { /* overwrite malformed file */ }
        }

        root["sandbox"] = SandboxPolicyYamlDto.FromDomain(policy);
        var yaml = Serializer.Serialize(root);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, yaml);
        File.Move(tmp, filePath, overwrite: true);
    }

    private async Task<bool> IsKnownProjectWorkspaceAsync(string repositoryPath, CancellationToken ct)
    {
        string canonical;
        try { canonical = NormalizePath(repositoryPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var projects = await _projectStore.ListAsync(ct).ConfigureAwait(false);
        return projects.Any(p => string.Equals(
            NormalizePath(p.WorkingDirectory),
            canonical,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool TryGetValue(Dictionary<string, object?> map, string key, out object? value)
    {
        foreach (var kvp in map)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}

/// <summary>YAML DTO for the sandbox section — snake_case via UnderscoredNamingConvention.</summary>
internal sealed class SandboxPolicyYamlDto
{
    public bool ShellEnabled { get; set; } = true;

    /// <summary>
    /// Run commands directly without sandbox isolation (bwrap/mxc).
    /// Use when the deployment environment itself provides isolation.
    /// Default: false.
    /// </summary>
    public bool Direct { get; set; } = false;

    /// <summary>
    /// Allow outbound network inside the sandbox.
    /// Default: false (more restrictive than Copilot CLI which defaults to true).
    /// </summary>
    public bool NetworkEnabled { get; set; } = false;

    public List<string> AllowedRepositoryRoots { get; set; } = [];
    public List<string> DestructiveCommandPatterns { get; set; } =
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
    public bool RequireApprovalForAllShell { get; set; } = false;
    public bool RedactPii { get; set; } = true;
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    public SandboxPolicy ToDomain(string repositoryPath) => new()
    {
        RepositoryPath = repositoryPath,
        ShellEnabled = ShellEnabled,
        Direct = Direct,
        NetworkEnabled = NetworkEnabled,
        AllowedRepositoryRoots = AllowedRepositoryRoots,
        DestructiveCommandPatterns = DestructiveCommandPatterns,
        RequireApprovalForAllShell = RequireApprovalForAllShell,
        RedactPii = RedactPii,
        MaxOutputBytes = MaxOutputBytes,
    };

    public static SandboxPolicyYamlDto FromDomain(SandboxPolicy p) => new()
    {
        ShellEnabled = p.ShellEnabled,
        Direct = p.Direct,
        NetworkEnabled = p.NetworkEnabled,
        AllowedRepositoryRoots = [.. p.AllowedRepositoryRoots],
        DestructiveCommandPatterns = [.. p.DestructiveCommandPatterns],
        RequireApprovalForAllShell = p.RequireApprovalForAllShell,
        RedactPii = p.RedactPii,
        MaxOutputBytes = p.MaxOutputBytes,
    };
}
