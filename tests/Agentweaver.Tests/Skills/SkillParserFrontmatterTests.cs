using Agentweaver.Api.Skills;
using FluentAssertions;

namespace Agentweaver.Tests.Skills;

/// <summary>
/// The curated marketplaces express the skill <c>description</c> in two different YAML shapes:
/// awesome-copilot uses a single-line quoted scalar, while microsoft/skills uses a YAML block
/// scalar (<c>description: |</c>). Both must yield a non-empty description so the browse index shows
/// a definition for every candidate.
/// </summary>
public sealed class SkillParserFrontmatterTests
{
    private readonly SkillParser _parser = new();

    [Fact]
    public void Parse_reads_single_line_quoted_description()
    {
        const string md = "---\n"
            + "name: acquire-codebase-knowledge\n"
            + "description: 'Use this skill when the user asks to map or document a codebase.'\n"
            + "license: MIT\n"
            + "---\n"
            + "# Body\n\nDo the thing thoroughly.\n";

        var result = _parser.Parse(md);

        result.Name.Should().Be("acquire-codebase-knowledge");
        result.Description.Should().Be("Use this skill when the user asks to map or document a codebase.");
    }

    [Fact]
    public void Parse_reads_yaml_block_scalar_description()
    {
        const string md = "---\n"
            + "name: azure-ai-openai-dotnet\n"
            + "description: |\n"
            + "  Azure OpenAI SDK for .NET. Use for chat completions, embeddings, and audio.\n"
            + "license: MIT\n"
            + "metadata:\n"
            + "  author: Microsoft\n"
            + "  version: \"1.0.0\"\n"
            + "---\n"
            + "# Azure.AI.OpenAI (.NET)\n\nClient library for Azure OpenAI.\n";

        var result = _parser.Parse(md);

        result.Name.Should().Be("azure-ai-openai-dotnet");
        result.Description.Should().NotBeNullOrWhiteSpace();
        result.Description.Should().Contain("Azure OpenAI SDK for .NET");
    }

    [Fact]
    public void Parse_reads_yaml_folded_scalar_description()
    {
        const string md = "---\n"
            + "name: folded-skill\n"
            + "description: >\n"
            + "  This description is folded\n"
            + "  across multiple lines.\n"
            + "---\n"
            + "# Body\n\nInstructions here.\n";

        var result = _parser.Parse(md);

        result.Name.Should().Be("folded-skill");
        result.Description.Should().Contain("This description is folded across multiple lines");
    }
}
