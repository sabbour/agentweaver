using System.Text;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class SkillContentValidatorTests
{
    private static readonly byte[] ValidMarkdown = Encoding.UTF8.GetBytes(
        """
        ---
        name: azure-ai-openai-dotnet
        description: |
          Azure OpenAI SDK for .NET. Use for chat completions and embeddings.
        license: MIT
        metadata:
          author: Microsoft
          version: "1.0.0"
        ---
        Use the documented client APIs and validate response data.
        """);

    [Fact]
    public void Validate_AcceptsStandardsCompatibleFrontmatterAndPreservesResources()
    {
        var resourceBytes = Encoding.UTF8.GetBytes("## Local reference\n");
        var validated = SkillContentValidator.Validate(
            ValidMarkdown,
            [new SkillContentResourceInput(@"docs\reference.md", resourceBytes)]);

        Assert.Equal("azure-ai-openai-dotnet", validated.Name);
        Assert.Contains("Azure OpenAI SDK for .NET", validated.Description);
        Assert.Equal(
            "Use the documented client APIs and validate response data.",
            validated.Instructions);
        var resource = Assert.Single(validated.Resources);
        Assert.Equal("docs/reference.md", resource.RelativePath);
        Assert.Equal(resourceBytes, resource.Content.ToArray());
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resourceBytes)).ToLowerInvariant(),
            resource.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", validated.ContentDigest);
    }

    [Fact]
    public void Validate_ProducesOrderIndependentDigestForResources()
    {
        var first = new SkillContentResourceInput("docs/a.md", Encoding.UTF8.GetBytes("A"));
        var second = new SkillContentResourceInput("docs/b.md", Encoding.UTF8.GetBytes("B"));

        var forward = SkillContentValidator.Validate(ValidMarkdown, [first, second]);
        var reverse = SkillContentValidator.Validate(ValidMarkdown, [second, first]);

        Assert.Equal(forward.ContentDigest, reverse.ContentDigest);
        Assert.Equal(
            new[] { "docs/a.md", "docs/b.md" },
            forward.Resources.Select(resource => resource.RelativePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Missing frontmatter")]
    [InlineData("---\nname: [\n---\nInstructions")]
    [InlineData("---\nname: one\nname: two\ndescription: desc\n---\nInstructions")]
    [InlineData("---\nname: skill\n---\nInstructions")]
    [InlineData("---\nname: skill\ndescription: desc\n---\n")]
    [InlineData("---\nname: skill\ndescription: desc\n---suffix\nInstructions")]
    public void Validate_RejectsInvalidSkillMarkdown(string markdown)
    {
        Assert.Throws<SkillContentValidationException>(
            () => SkillContentValidator.Validate(Encoding.UTF8.GetBytes(markdown)));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("docs//guide.md")]
    [InlineData("C:/outside.md")]
    [InlineData("//server/share.md")]
    [InlineData("docs/CON.txt")]
    [InlineData("docs/trailing.")]
    [InlineData("docs/SKILL.md")]
    public void Validate_RejectsUnsafeOrReservedResourcePaths(string path)
    {
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(
                ValidMarkdown,
                [new SkillContentResourceInput(path, Encoding.UTF8.GetBytes("data"))]));
    }

    [Fact]
    public void Validate_RejectsNormalizedCaseInsensitivePathCollisions()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(
                ValidMarkdown,
                [
                    new SkillContentResourceInput("docs/guide.md", Encoding.UTF8.GetBytes("A")),
                    new SkillContentResourceInput(@"DOCS\GUIDE.md", Encoding.UTF8.GetBytes("B")),
                ]));
    }

    [Fact]
    public void Validate_RejectsInvalidUtf8AndBinaryResources()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(
                ValidMarkdown,
                [new SkillContentResourceInput("invalid.md", new byte[] { 0xC3, 0x28 })]));
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(
                ValidMarkdown,
                [new SkillContentResourceInput("binary.txt", new byte[] { 0x41, 0x00, 0x42 })]));
    }

    [Fact]
    public void Validate_RejectsFilesAndInventoriesBeyondPriorSkillLimits()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(
                ValidMarkdown,
                [new SkillContentResourceInput(
                    "large.md",
                    new byte[SkillContentValidator.MaxResourceBytes + 1])]));

        var tooMany = Enumerable.Range(0, SkillContentValidator.MaxResourceCount + 1)
            .Select(index => new SkillContentResourceInput(
                $"resource-{index}.md",
                Encoding.UTF8.GetBytes("data")))
            .ToArray();
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentValidator.Validate(ValidMarkdown, tooMany));
    }
}
