using Scaffolder.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// GitOps-backed <see cref="ISandboxPolicyStore"/>. Reads and writes
/// <c>.scaffolder/sandbox.yml</c> in the project repository root.
/// When the file does not exist, the default policy is returned.
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

    private static string PolicyFilePath(string repositoryPath) =>
        Path.Combine(repositoryPath, ".scaffolder", "sandbox.yml");

    public Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        var filePath = PolicyFilePath(repositoryPath);
        if (!File.Exists(filePath))
            return Task.FromResult(SandboxPolicy.Default(repositoryPath));

        try
        {
            var yaml = File.ReadAllText(filePath);
            var dto = Deserializer.Deserialize<SandboxPolicyYamlDto>(yaml) ?? new();
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
        var filePath = PolicyFilePath(policy.RepositoryPath);
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        var dto = SandboxPolicyYamlDto.FromDomain(policy);
        var yaml = Serializer.Serialize(dto);
        File.WriteAllText(filePath, yaml);
        return Task.CompletedTask;
    }
}

/// <summary>YAML DTO — snake_case field names via UnderscoredNamingConvention.</summary>
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
