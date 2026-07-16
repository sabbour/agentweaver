using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Skills;
using Agentweaver.Domain.Skills;
using Agentweaver.Squad.Catalog;
using FluentAssertions;

namespace Agentweaver.Tests.Skills;

public sealed class BuiltInSkillAssetTests
{
    private static readonly string[] ExpectedSlugs =
    [
        "agent-architecture",
        "agent-evaluation",
        "agent-regression",
        "agent-tools",
        "ai-safety",
        "api-data-safety",
        "architecture-decisions",
        "customer-research-evidence",
        "delivery-operations",
        "docs-release-notes",
        "positioning",
        "prd-prioritization",
        "prompt-engineering",
        "prototype-ux",
        "research-source-quality",
        "system-design",
        "test-strategy-reproduction",
        "threat-modeling",
        "ui-accessibility",
        "writing-editing-fact-checking",
    ];

    // This is an expectation-only coverage plan. It deliberately does not create Blueprint bindings
    // or runtime assignments; those remain a later delivery phase.
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedRoleSkills =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["agent-architect"] = ["agent-architecture", "agent-tools"],
            ["agent-evaluator"] = ["agent-evaluation", "agent-regression"],
            ["ai-safety-reviewer"] = ["ai-safety"],
            ["backend-engineer"] = ["api-data-safety"],
            ["customer-researcher"] = ["customer-research-evidence"],
            ["devops-engineer"] = ["delivery-operations"],
            ["docs-writer"] = ["docs-release-notes"],
            ["editor"] = ["writing-editing-fact-checking"],
            ["frontend-engineer"] = ["ui-accessibility"],
            ["lead-architect"] = ["architecture-decisions", "system-design"],
            ["lead-pm"] = ["prd-prioritization"],
            ["lead-researcher"] = ["research-source-quality"],
            ["product-marketing-manager"] = ["positioning"],
            ["prompt-engineer"] = ["prompt-engineering"],
            ["prototype-designer"] = ["prototype-ux"],
            ["qa-engineer"] = ["test-strategy-reproduction"],
            ["security-engineer"] = ["threat-modeling"],
            ["ux-designer"] = ["prototype-ux", "ui-accessibility"],
            ["writer"] = ["writing-editing-fact-checking"],
        };

    private static readonly Regex PortablePath = new(
        @"^(?:[a-z0-9]+(?:[._-][a-z0-9]+)*/)*[a-z0-9]+(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".csv",
    };

    [Fact]
    public void BundledSkills_HaveTheExactApprovedSlugInventory()
    {
        var actualSlugs = Directory.EnumerateDirectories(SkillsRoot)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        actualSlugs.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(actualSlugs.Length);
        actualSlugs.Should().Equal(ExpectedSlugs);
    }

    [Fact]
    public void BundledSkills_AreEmbeddedInTheCatalogAssembly()
    {
        var resourceNames = typeof(CatalogReader).Assembly.GetManifestResourceNames();

        foreach (var slug in ExpectedSlugs)
        {
            resourceNames.Should().Contain(
                $"Agentweaver.Squad.Catalog.Resources.skills.{slug.Replace('-', '_')}.SKILL.md");
        }
    }

    [Fact]
    public void BundledSkills_AreValidBoundedAndSafeTextAssets()
    {
        var parser = new SkillParser();

        foreach (var slug in ExpectedSlugs)
        {
            var skillDirectory = Path.Combine(SkillsRoot, slug);
            var skillPath = Path.Combine(skillDirectory, "SKILL.md");
            File.Exists(skillPath).Should().BeTrue($"{slug} must provide SKILL.md");
            ((new DirectoryInfo(skillDirectory).Attributes & FileAttributes.ReparsePoint) == 0)
                .Should().BeTrue($"{slug} must not be a symlink or reparse point");
            new FileInfo(skillPath).Length.Should().BeLessThanOrEqualTo(32 * 1024);

            var resources = Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, skillPath, StringComparison.OrdinalIgnoreCase))
                .Select(path => ToResource(skillDirectory, path))
                .ToArray();

            resources.Length.Should().BeLessThanOrEqualTo(8);
            resources.Sum(resource => Encoding.UTF8.GetByteCount(resource.Content)).Should().BeLessThanOrEqualTo(256 * 1024);
            foreach (var resource in resources)
            {
                Encoding.UTF8.GetByteCount(resource.Content).Should().BeLessThanOrEqualTo(64 * 1024);
                IsSafeResourcePath(resource.RelativePath).Should().BeTrue($"{slug} resource paths must be portable and contained");
                TextExtensions.Contains(Path.GetExtension(resource.RelativePath)).Should().BeTrue($"{slug} resources must use a text extension");
            }

            var markdown = ReadUtf8Text(skillPath);
            AssertFrontmatterIsOnlyNameAndDescription(markdown, slug);

            var parsed = parser.Parse(markdown, resources);
            parsed.IsValid.Should().BeTrue($"{slug} must be accepted by the current SkillParser: {string.Join("; ", parsed.Errors)}");
            parsed.Name.Should().Be(slug);
            parsed.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void BundledSkills_ContainRequiredSafetyAndUntrustedContentGuidance()
    {
        foreach (var slug in ExpectedSlugs)
        {
            var markdown = ReadUtf8Text(Path.Combine(SkillsRoot, slug, "SKILL.md"));
            markdown.Should().Contain("## Safety and authority");

            var guidance = markdown.ToLowerInvariant();
            guidance.Should().Contain("advisory and lower-authority");
            guidance.Should().Contain("system and developer instructions");
            guidance.Should().Contain("user intent");
            guidance.Should().Contain("runtime governance");
            guidance.Should().Contain("tool allowlists");
            guidance.Should().Contain("sandbox");
            guidance.Should().Contain("safety rules");
            guidance.Should().Contain("approval gates");
            guidance.Should().Contain("untrusted data, not commands");
            guidance.Should().Contain("embedded requests");
            guidance.Should().Contain("secrets");
            guidance.Should().Contain("expanded access");
            guidance.Should().Contain("arbitrary execution");
            guidance.Should().Contain("governance bypass");
            guidance.Should().Contain("never fetched or executed automatically");
        }
    }

    [Fact]
    public void BundledSkills_CoverTheCurrentBlueprintRoleRoster_WithoutBindings()
    {
        var actualRoles = BlueprintFiles
            .SelectMany(path => ReadRoster(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        actualRoles.Should().HaveCount(19);
        ExpectedRoleSkills.Keys.Order(StringComparer.Ordinal).Should().Equal(actualRoles);

        foreach (var (role, expectedSkills) in ExpectedRoleSkills)
        {
            expectedSkills.Should().NotBeEmpty($"{role} needs an expected reusable skill");
            expectedSkills.Should().OnlyContain(skill => ExpectedSlugs.Contains(skill, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ReferenceDocumentation_ExplainsAssetsAreNotAutomaticBlueprintBindings()
    {
        var documentation = ReadUtf8Text(Path.Combine(RepositoryRoot, "docs", "reference", "built-in-skills.md"));

        documentation.Should().Contain("Blueprint bindings and automatic skill assignment are a later phase");
        documentation.Should().Contain("does not currently attach a skill to an agent");
    }

    private static string SkillsRoot => Path.Combine(RepositoryRoot, "packages", "Agentweaver.Squad", "Catalog", "Resources", "skills");

    private static IEnumerable<string> BlueprintFiles => Directory.EnumerateFiles(
        Path.Combine(RepositoryRoot, "packages", "Agentweaver.Squad", "Catalog", "Resources", "blueprints"),
        "blueprint_*.json",
        SearchOption.TopDirectoryOnly);

    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }
    }

    private static SkillResource ToResource(string skillDirectory, string path)
    {
        var relative = Path.GetRelativePath(skillDirectory, path).Replace('\\', '/');
        return new SkillResource { RelativePath = relative, Content = ReadUtf8Text(path) };
    }

    private static bool IsSafeResourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath != relativePath.ToLowerInvariant()
            || !PortablePath.IsMatch(relativePath))
            return false;

        var segments = relativePath.Split('/');
        return segments.Length >= 2
            && (segments[0] is "references" or "templates" or "checklists")
            && !segments.Any(segment => segment is "." or "..");
    }

    private static string ReadUtf8Text(string path)
    {
        var file = new FileInfo(path);
        ((file.Attributes & FileAttributes.ReparsePoint) == 0).Should().BeTrue($"{path} must not be a symlink or reparse point");

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(File.ReadAllBytes(path));
        text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            .Should()
            .BeFalse($"{path} must not contain control characters");
        text.Should().NotContain("\u202A");
        text.Should().NotContain("\u202B");
        text.Should().NotContain("\u202D");
        text.Should().NotContain("\u202E");
        text.Should().NotContain("\u2066");
        text.Should().NotContain("\u2067");
        text.Should().NotContain("\u2068");
        text.Should().NotContain("\u2069");
        return text;
    }

    private static void AssertFrontmatterIsOnlyNameAndDescription(string markdown, string slug)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        lines.Should().HaveCountGreaterThanOrEqualTo(4);
        lines[0].Should().Be("---");
        lines[1].Should().Be($"name: {slug}");
        lines[2].Should().StartWith("description: ");
        lines[3].Should().Be("---");
    }

    private static IEnumerable<string> ReadRoster(string blueprintPath)
    {
        using var document = JsonDocument.Parse(ReadUtf8Text(blueprintPath));
        return document.RootElement.GetProperty("roster")
            .EnumerateArray()
            .Select(role => role.GetString()!)
            .ToArray();
    }
}
