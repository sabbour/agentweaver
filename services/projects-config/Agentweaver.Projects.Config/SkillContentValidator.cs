using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Agentweaver.Projects.Config;

/// <summary>Untrusted resource path and bytes included with a Skill.md candidate.</summary>
public sealed record SkillContentResourceInput(string RelativePath, ReadOnlyMemory<byte> Content);

/// <summary>Normalized resource bytes and their individual SHA-256 digest.</summary>
public sealed record ValidatedSkillContentResource(
    string RelativePath,
    ImmutableArray<byte> Content,
    string Sha256);

/// <summary>Parsed skill instructions and canonically ordered resources with a stable combined content digest.</summary>
public sealed record ValidatedSkillContent(
    string Name,
    string Description,
    string Instructions,
    ImmutableArray<ValidatedSkillContentResource> Resources,
    string ContentDigest);

/// <summary>Invalid or unsupported manifest/resource input; endpoint mapping returns this as a client error.</summary>
public sealed class SkillContentValidationException(string message) : Exception(message)
{
    public const string ErrorCode = "invalid_skill_content";
}

/// <summary>
/// Validates Skill.md frontmatter, strict UTF-8 instructions, safe resource paths, and bounded content sizes.
/// </summary>
public static class SkillContentValidator
{
    public const int MaxNameLength = SkillRuntimeContentContract.MaxNameLength;
    public const int MaxDescriptionLength = SkillRuntimeContentContract.MaxDescriptionLength;
    public const int MaxInstructionsBytes = SkillRuntimeContentContract.MaxInstructionsBytes;
    public const int MaxSkillMarkdownBytes = SkillRuntimeContentContract.MaxSkillMarkdownBytes;
    public const int MaxResourceBytes = SkillRuntimeContentContract.MaxResourceBytes;
    public const int MaxTotalResourceBytes = SkillRuntimeContentContract.MaxTotalResourceBytes;
    public const int MaxResourceCount = SkillRuntimeContentContract.MaxResourceCount;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Parses and validates the skill candidate, normalizes resource paths, sorts resources, and computes the content digest.
    /// </summary>
    /// <exception cref="SkillContentValidationException">The manifest, text encoding, resource inventory, or size limits are invalid.</exception>
    public static ValidatedSkillContent Validate(
        ReadOnlyMemory<byte> skillMarkdown,
        IReadOnlyList<SkillContentResourceInput>? resources = null)
    {
        resources ??= [];
        if (skillMarkdown.Length == 0)
            throw Invalid("SKILL.md is empty.");
        if (skillMarkdown.Length > MaxSkillMarkdownBytes)
            throw Invalid($"SKILL.md exceeds {MaxSkillMarkdownBytes / 1024} KB.");

        var markdown = DecodeText(skillMarkdown.Span, "SKILL.md");
        if (markdown.StartsWith('\uFEFF'))
            markdown = markdown[1..];
        if (!TrySplitFrontmatter(markdown, out var frontmatter, out var body))
            throw Invalid("SKILL.md must begin with YAML frontmatter delimited by '---' lines.");

        var (name, description) = ParseFrontmatter(frontmatter);
        if (string.IsNullOrWhiteSpace(name))
            throw Invalid("SKILL.md frontmatter must include a non-empty 'name'.");
        name = name.Trim();
        if (name.Length > MaxNameLength || !IsValidName(name))
            throw Invalid($"Skill name must be a lowercase slug of at most {MaxNameLength} characters.");

        if (string.IsNullOrWhiteSpace(description))
            throw Invalid("SKILL.md frontmatter must include a non-empty 'description'.");
        description = description.Trim();
        if (description.Length > MaxDescriptionLength)
            throw Invalid($"Skill description exceeds {MaxDescriptionLength} characters.");

        var instructions = body.Trim();
        if (instructions.Length == 0)
            throw Invalid("SKILL.md has no instruction body after the frontmatter.");
        if (instructions.Contains('\0'))
            throw Invalid("SKILL.md contains a NUL byte.");
        if (StrictUtf8.GetByteCount(instructions) > MaxInstructionsBytes)
            throw Invalid($"SKILL.md instructions exceed {MaxInstructionsBytes / 1024} KB.");

        if (resources.Count > MaxResourceCount)
            throw Invalid($"Skill has more than {MaxResourceCount} bundled resources.");

        var validatedResources = ImmutableArray.CreateBuilder<ValidatedSkillContentResource>(resources.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var resource in resources)
        {
            if (resource is null)
                throw Invalid("Skill resources cannot contain null entries.");
            var path = NormalizeResourcePath(resource.RelativePath);
            if (path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw Invalid($"Resource path '{resource.RelativePath}' conflicts with a skill instruction file.");
            if (!seenPaths.Add(path))
                throw Invalid($"Skill contains duplicate resource path '{path}'.");

            var bytes = resource.Content.Span;
            if (bytes.Length > MaxResourceBytes)
                throw Invalid($"Bundled resource '{path}' exceeds {MaxResourceBytes / 1024} KB.");
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > MaxTotalResourceBytes)
                throw Invalid($"Bundled resources exceed {MaxTotalResourceBytes / 1024} KB in total.");
            if (bytes.Contains((byte)0))
                throw Invalid($"Bundled resource '{path}' contains binary data.");
            _ = DecodeText(bytes, $"Bundled resource '{path}'");

            var content = ImmutableArray.CreateRange(bytes.ToArray());
            validatedResources.Add(new ValidatedSkillContentResource(
                path,
                content,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        var orderedResources = validatedResources
            .OrderBy(resource => resource.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        var immutableResources = orderedResources
            .Select(resource => new SkillRuntimeContentResourceV1(
                resource.RelativePath,
                resource.Content,
                resource.Sha256))
            .ToImmutableArray();
        return new ValidatedSkillContent(
            name,
            description,
            instructions,
            orderedResources,
            SkillRuntimeContentContract.ComputeContentDigest(
                name, description, instructions, immutableResources));
    }

    /// <summary>Reads a single YAML mapping and accepts only scalar name and description fields.</summary>
    private static (string? Name, string? Description) ParseFrontmatter(string text)
    {
        try
        {
            using var reader = new StringReader(text);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
                throw Invalid("SKILL.md frontmatter must be one YAML mapping.");

            string? name = null;
            string? description = null;
            var sawName = false;
            var sawDescription = false;
            foreach (var (keyNode, valueNode) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: not null } key)
                    throw Invalid("SKILL.md frontmatter keys must be scalar strings.");
                if (key.Value is "name" or "description")
                {
                    if (valueNode is not YamlScalarNode { Value: not null } value)
                        throw Invalid($"SKILL.md frontmatter '{key.Value}' must be a string scalar.");
                    if (key.Value == "name")
                    {
                        if (sawName)
                            throw Invalid("SKILL.md frontmatter contains duplicate 'name' keys.");
                        name = value.Value;
                        sawName = true;
                    }
                    else
                    {
                        if (sawDescription)
                            throw Invalid("SKILL.md frontmatter contains duplicate 'description' keys.");
                        description = value.Value;
                        sawDescription = true;
                    }
                }
            }

            return (name, description);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw Invalid($"SKILL.md frontmatter is not valid YAML: {exception.Message}");
        }
    }

    /// <summary>Finds the opening and closing YAML delimiters without treating later document markers as body content.</summary>
    private static bool TrySplitFrontmatter(string text, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = string.Empty;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var firstLineEnd = normalized.IndexOf('\n');
        if (firstLineEnd < 0 || !normalized.AsSpan(0, firstLineEnd).SequenceEqual("---"))
            return false;

        var contentStart = firstLineEnd + 1;
        var lineStart = contentStart;
        while (lineStart <= normalized.Length)
        {
            var lineEnd = normalized.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = normalized.Length;
            if (normalized.AsSpan(lineStart, lineEnd - lineStart).SequenceEqual("---"))
            {
                frontmatter = normalized[contentStart..lineStart];
                body = lineEnd == normalized.Length ? string.Empty : normalized[(lineEnd + 1)..];
                return true;
            }
            if (lineEnd == normalized.Length)
                break;
            lineStart = lineEnd + 1;
        }

        return false;
    }

    /// <summary>Converts path-contract failures into the validator's public input error.</summary>
    private static string NormalizeResourcePath(string? raw)
    {
        try
        {
            return SkillRuntimeContentContract.NormalizeResourcePath(raw);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(exception.Message);
        }
    }

    private static bool IsValidName(string name)
    {
        if (name[0] == '-' || name[^1] == '-')
            return false;
        var previousWasHyphen = false;
        foreach (var character in name)
        {
            var isHyphen = character == '-';
            if (!isHyphen && !((character >= 'a' && character <= 'z') ||
                               (character >= '0' && character <= '9')))
                return false;
            if (isHyphen && previousWasHyphen)
                return false;
            previousWasHyphen = isHyphen;
        }
        return true;
    }

    /// <summary>Decodes strict UTF-8 and reports which manifest or resource failed decoding.</summary>
    private static string DecodeText(ReadOnlySpan<byte> bytes, string source)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid($"{source} is not valid UTF-8 text.");
        }
    }

    private static SkillContentValidationException Invalid(string message) => new(message);
}
