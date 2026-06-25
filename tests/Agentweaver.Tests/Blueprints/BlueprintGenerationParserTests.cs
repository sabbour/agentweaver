using FluentAssertions;
using Agentweaver.Api.Blueprints;

namespace Agentweaver.Tests.Blueprints;

/// <summary>
/// Unit tests for <see cref="BlueprintGenerationParser"/>: a pure string-to-blueprint parser exercised
/// directly without the model. Covers empty input, prose without JSON, malformed JSON, JSON embedded in
/// prose, and a well-formed blueprint with catalog role ids.
/// </summary>
public sealed class BlueprintGenerationParserTests
{
    [Fact]
    public void Parse_EmptyResponse_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("   ");
        result.Succeeded.Should().BeFalse();
        result.Blueprint.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_NoJsonObject_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("I cannot help with that request.");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_FailsWithError()
    {
        var result = BlueprintGenerationParser.Parse("{ \"id\": \"x\", roster: [ }");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_JsonEmbeddedInProse_ExtractsBlueprint()
    {
        var raw = """
            Here is the blueprint you asked for:
            {
              "id": "blueprint-data",
              "name": "Data Team",
              "description": "Builds data products.",
              "roster": ["backend-engineer", "docs-writer"],
              "workflow": "default",
              "review_policy": "default",
              "sandbox_profile": "restricted"
            }
            Let me know if you want changes.
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.Id.Should().Be("blueprint-data");
        result.Blueprint.Roster.Should().Contain(new[] { "backend-engineer", "docs-writer" });
        result.Blueprint.SandboxProfile.Should().Be("restricted");
    }

    [Fact]
    public void Parse_BespokeRoles_AreExtracted()
    {
        var raw = """
            {
              "id": "travel-planner",
              "name": "Travel Planner",
              "description": "Plans trips.",
              "roster": ["travel-researcher", "docs-writer"],
              "bespoke_roles": [
                { "id": "travel-researcher", "title": "Travel Researcher",
                  "charter": "You research destinations. You weigh climate and logistics." }
              ],
              "workflows": ["default"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.BespokeRoles.Should().ContainSingle();
        var bespoke = result.Blueprint.BespokeRoles[0];
        bespoke.Id.Should().Be("travel-researcher");
        bespoke.Title.Should().Be("Travel Researcher");
        bespoke.Charter.Should().Contain("research destinations");
    }

    [Fact]
    public void Parse_BespokeRoleMissingCharter_IsSkipped()
    {
        var raw = """
            {
              "id": "x", "name": "X", "description": "d",
              "roster": ["a"],
              "bespoke_roles": [ { "id": "a", "title": "A" } ],
              "review_policy": "default", "sandbox_profile": "default"
            }
            """;

        var result = BlueprintGenerationParser.Parse(raw);
        result.Succeeded.Should().BeTrue();
        result.Blueprint!.BespokeRoles.Should().BeEmpty();
    }
}
