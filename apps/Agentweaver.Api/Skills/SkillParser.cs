using System.Security.Cryptography;
using System.Text;
using Agentweaver.Domain.Skills;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentweaver.Api.Skills;

/// <summary>A raw candidate skill discovered in a directory before validation.</summary>
public sealed record RawSkill(
    string RelativeLocation,
    string SkillMarkdown,
    IReadOnlyList<SkillResource> Resources);

/// <summary>Outcome of parsing/validating a <c>SKILL.md</c> module.</summary>
public sealed record SkillParseResult
{
    public bool IsValid => Errors.Count == 0;
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string Instructions { get; init; } = "";
    public IReadOnlyList<SkillResource> Resources { get; init; } = Array.Empty<SkillResource>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static SkillParseResult Invalid(params string[] errors) => new() { Errors = errors };
}

/// <summary>
/// Parses and validates a standards-compatible <c>SKILL.md</c> module: reads the YAML frontmatter
/// (<c>name</c> + <c>description</c> required), extracts the instruction body, validates bundled
/// resources, and computes a stable content hash used for idempotent re-import / re-sync.
/// </summary>
public sealed class SkillParser
{
    /// <summary>Recognized skill directories in a repository (one level deep, SKILL.md per skill dir).</summary>
    public static readonly IReadOnlyList<string> RecognizedSkillDirectories = new[]
    {
        ".github/skills",
        ".copilot/skills",
        ".claude/skills",
        ".agents/skills",
    };

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MaxInstructionsBytes = 256 * 1024;      // 256 KB
    public const int MaxResourceBytes = 256 * 1024;          // 256 KB per resource
    public const int MaxTotalResourceBytes = 1 * 1024 * 1024; // 1 MB across all resources
    public const int MaxResourceCount = 64;

    // camelCase maps frontmatter keys (name, description) to the model; unmatched keys (license,
    // version, allowed-tools, …) are ignored so real-world SKILL.md files parse without error.
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Frontmatter model. Extra keys are ignored so real-world SKILL.md files (with license, version,
    /// allowed-tools, etc.) parse without error.
    /// </summary>
    private sealed class Frontmatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public SkillParseResult Parse(string skillMarkdown, IReadOnlyList<SkillResource>? resources = null)
    {
        var errors = new List<string>();
        resources ??= Array.Empty<SkillResource>();

        if (string.IsNullOrWhiteSpace(skillMarkdown))
            return SkillParseResult.Invalid("SKILL.md is empty.");

        if (!TrySplitFrontmatter(skillMarkdown, out var frontYaml, out var body))
            return SkillParseResult.Invalid(
                "SKILL.md must begin with a YAML frontmatter block delimited by '---' lines.");

        Frontmatter? front;
        try
        {
            front = Yaml.Deserialize<Frontmatter>(frontYaml) ?? new Frontmatter();
        }
        catch (Exception ex)
        {
            return SkillParseResult.Invalid($"SKILL.md frontmatter is not valid YAML: {ex.Message}");
        }

        var name = front.Name?.Trim();
        var description = front.Description?.Trim();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("SKILL.md frontmatter must include a non-empty 'name'.");
        else if (name.Length > MaxNameLength)
            errors.Add($"Skill name exceeds {MaxNameLength} characters.");

        if (string.IsNullOrWhiteSpace(description))
            errors.Add("SKILL.md frontmatter must include a non-empty 'description'.");
        else if (description.Length > MaxDescriptionLength)
            errors.Add($"Skill description exceeds {MaxDescriptionLength} characters.");

        var instructions = body.Trim();
        if (string.IsNullOrWhiteSpace(instructions))
            errors.Add("SKILL.md has no instruction body after the frontmatter.");
        else if (Encoding.UTF8.GetByteCount(instructions) > MaxInstructionsBytes)
            errors.Add($"SKILL.md instructions exceed {MaxInstructionsBytes / 1024} KB.");

        if (resources.Count > MaxResourceCount)
            errors.Add($"Skill has more than {MaxResourceCount} bundled resources.");

        long total = 0;
        foreach (var r in resources)
        {
            var bytes = Encoding.UTF8.GetByteCount(r.Content);
            total += bytes;
            if (bytes > MaxResourceBytes)
                errors.Add($"Bundled resource '{r.RelativePath}' exceeds {MaxResourceBytes / 1024} KB.");
            if (LooksBinary(r.Content))
                errors.Add($"Bundled resource '{r.RelativePath}' appears to be binary; only text resources are allowed.");
        }
        if (total > MaxTotalResourceBytes)
            errors.Add($"Bundled resources exceed {MaxTotalResourceBytes / 1024} KB in total.");

        if (errors.Count > 0)
            return new SkillParseResult { Name = name, Description = description, Errors = errors };

        return new SkillParseResult
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            Resources = resources,
        };
    }

    /// <summary>Stable content hash over name + description + instructions + sorted resources.</summary>
    public static string ComputeContentHash(string name, string description, string instructions, IReadOnlyList<SkillResource> resources)
    {
        var sb = new StringBuilder();
        sb.Append(name.Trim()).Append('\u0000');
        sb.Append(description.Trim()).Append('\u0000');
        sb.Append(instructions.Trim()).Append('\u0000');
        foreach (var r in resources.OrderBy(r => r.RelativePath, StringComparer.Ordinal))
            sb.Append(r.RelativePath).Append('\u0001').Append(r.Content).Append('\u0000');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool TrySplitFrontmatter(string text, out string frontYaml, out string body)
    {
        frontYaml = "";
        body = "";
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) && normalized != "---")
            return false;

        var rest = normalized.Substring(4);
        var end = rest.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
            return false;

        frontYaml = rest.Substring(0, end);
        var afterMarker = end + "\n---".Length;
        // Skip to end of the closing marker line.
        var newlineAfter = rest.IndexOf('\n', afterMarker);
        body = newlineAfter < 0 ? "" : rest.Substring(newlineAfter + 1);
        return true;
    }

    private static bool LooksBinary(string content)
    {
        // Heuristic: presence of a NUL byte in the first 8 KB indicates binary content.
        var probe = content.Length > 8192 ? content.Substring(0, 8192) : content;
        return probe.Contains('\0');
    }
}
