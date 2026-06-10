using Scaffolder.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// GitOps-backed <see cref="ISandboxPolicyStore"/>. Reads and writes the
/// <c>sandbox:</c> section of <c>.scaffolder/settings.yml</c> in the project
/// repository root. Other sections in the file are preserved on write.
/// When the file does not exist or has no sandbox section, the default policy is returned.
/// <see cref="SetPolicyAsync"/> writes the file; the operator commits it.
/// </summary>
public sealed class YamlSandboxPolicyStore : ISandboxPolicyStore
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    private static string SettingsFilePath(string repositoryPath) =>
        Path.Combine(repositoryPath, ".scaffolder", "settings.yml");

    public Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        var filePath = SettingsFilePath(repositoryPath);
        if (!File.Exists(filePath))
            return Task.FromResult(SandboxPolicy.Default(repositoryPath));

        try
        {
            var yaml = File.ReadAllText(filePath);
            var root = Deserializer.Deserialize<ScaffolderSettingsDto>(yaml) ?? new();
            var dto = root.Sandbox ?? new();
            return Task.FromResult(dto.ToDomain(repositoryPath));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
        {
            // Malformed or unreadable file — return defaults rather than crashing the run.
            return Task.FromResult(SandboxPolicy.Default(repositoryPath));
        }
    }

    public Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default)
    {
        var filePath = SettingsFilePath(policy.RepositoryPath);
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        // Preserve existing sections; only update the sandbox section.
        ScaffolderSettingsDto root = new();
        if (File.Exists(filePath))
        {
            try
            {
                var existing = File.ReadAllText(filePath);
                root = Deserializer.Deserialize<ScaffolderSettingsDto>(existing) ?? new();
            }
            catch (YamlDotNet.Core.YamlException) { /* overwrite malformed file */ }
        }

        root.Sandbox = SandboxPolicyYamlDto.FromDomain(policy);
        var yaml = Serializer.Serialize(root);
        File.WriteAllText(filePath, yaml);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Root DTO for <c>.scaffolder/settings.yml</c>.
/// Each top-level key is a settings group. Adding a new group here does not
/// affect unrelated groups — existing sections are preserved on write.
/// </summary>
internal sealed class ScaffolderSettingsDto
{
    /// <summary>Sandbox execution policy.</summary>
    public SandboxPolicyYamlDto? Sandbox { get; set; }

    // Future groups:
    // public ReviewSettingsDto? Review { get; set; }
    // public AgentSettingsDto? Agents { get; set; }
}

/// <summary>YAML DTO for the sandbox section — snake_case via UnderscoredNamingConvention.</summary>
internal sealed class SandboxPolicyYamlDto
{
    public bool ShellEnabled { get; set; } = true;
    public List<string> AllowedRepositoryRoots { get; set; } = [];
    public List<string> DestructiveCommandPatterns { get; set; } =
    [
        "rm -rf", "del /s", "format ", "mkfs", "dd if=",
        "git push --force", "git reset --hard",
    ];
    public bool RequireApprovalForAllShell { get; set; } = false;
    public bool RedactPii { get; set; } = true;
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    public SandboxPolicy ToDomain(string repositoryPath) => new()
    {
        RepositoryPath = repositoryPath,
        ShellEnabled = ShellEnabled,
        AllowedRepositoryRoots = AllowedRepositoryRoots,
        DestructiveCommandPatterns = DestructiveCommandPatterns,
        RequireApprovalForAllShell = RequireApprovalForAllShell,
        RedactPii = RedactPii,
        MaxOutputBytes = MaxOutputBytes,
    };

    public static SandboxPolicyYamlDto FromDomain(SandboxPolicy p) => new()
    {
        ShellEnabled = p.ShellEnabled,
        AllowedRepositoryRoots = [..p.AllowedRepositoryRoots],
        DestructiveCommandPatterns = [..p.DestructiveCommandPatterns],
        RequireApprovalForAllShell = p.RequireApprovalForAllShell,
        RedactPii = p.RedactPii,
        MaxOutputBytes = p.MaxOutputBytes,
    };
}
