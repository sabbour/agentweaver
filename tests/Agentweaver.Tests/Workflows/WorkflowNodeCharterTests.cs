using FluentAssertions;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Unit tests for the inline bespoke <c>charter</c> field on a workflow prompt node: it must
/// round-trip through <see cref="WorkflowDefinitionLoader"/> so a generated workflow can mint a
/// domain-specific agent role whose persona is carried inline rather than via the catalog.
/// </summary>
public sealed class WorkflowNodeCharterTests
{
    [Fact]
    public void Load_PromptNodeWithCharter_ParsesCharter()
    {
        var yaml = """
            id: trip
            name: Trip
            trigger:
              type: manual
            start: research
            nodes:
              - id: research
                type: prompt
                label: Research
                role: travel-researcher
                charter: "You research destinations. You weigh climate, cost, and logistics."
                prompt: "Research the destination."
            edges: []
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "trip.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        var node = result.Definition!.Nodes.Single(n => n.Id == "research");
        node.Charter.Should().Contain("research destinations");
    }

    [Fact]
    public void Load_PromptNodeWithoutCharter_LeavesCharterNull()
    {
        var yaml = """
            id: trip
            name: Trip
            trigger:
              type: manual
            start: research
            nodes:
              - id: research
                type: prompt
                label: Research
                role: backend-engineer
                prompt: "Do the work."
            edges: []
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "trip.yaml");

        result.IsValid.Should().BeTrue(because: result.Error);
        result.Definition!.Nodes.Single(n => n.Id == "research").Charter.Should().BeNull();
    }
}
