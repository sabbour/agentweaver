using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Workflows;
using FluentAssertions;

namespace Agentweaver.Tests.Workflows;

public sealed class WorkflowNodeTypeContractTests
{
    [Fact]
    public void SharedContract_MatchesServerYamlAndApiTypes()
    {
        var contractPath = Path.Combine(AppContext.BaseDirectory, "Contracts", "workflowNodeTypes.json");
        var contract = JsonSerializer.Deserialize<List<ContractEntry>>(
            File.ReadAllText(contractPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        contract.Should().NotBeNullOrEmpty();

        var serverTypes = Enum.GetValues<WorkflowNodeType>()
            .Select(type =>
            {
                var yaml = WorkflowDefinitionYamlSerializer.Serialize(DefinitionWith(type));
                var yamlType = Regex.Match(yaml, @"(?m)^\s+type:\s+(?<type>[a-z_]+)\s*$")
                    .Groups["type"].Value;

                var reloaded = WorkflowDefinitionLoader.Load(yaml, "contract");
                reloaded.Error.Should().NotContain("unknown type");

                return new ContractEntry(
                    yamlType,
                    WorkflowDtoMapper.NodeTypeToApi(type),
                    Label: "",
                    Authorable: false);
            })
            .ToArray();

        contract!.Select(entry => (entry.YamlType, entry.ApiType))
            .Should().Equal(serverTypes.Select(entry => (entry.YamlType, entry.ApiType)));
    }

    [Fact]
    public void PublishNode_IsBindableAsAnAgentBackedAction()
    {
        var result = WorkflowDefinitionLoader.Load("""
            id: publish-output
            name: Publish output
            start: publish
            nodes:
              - id: publish
                type: publish
                label: Publish
                agent: content-author
                prompt: Package the approved output.
              - id: scribe
                type: scribe
                label: Scribe
              - id: done
                type: terminal
                label: Done
            edges:
              - from: publish
                to: scribe
              - from: scribe
                to: done
            """, "publish-output");

        result.IsValid.Should().BeTrue(result.Error);
        result.Definition!.Nodes.Single(node => node.Id == "publish").Type
            .Should().Be(WorkflowNodeType.Publish);
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition).Should().BeEmpty();
    }

    private static WorkflowDefinition DefinitionWith(WorkflowNodeType type) => new()
    {
        Id = "contract",
        Name = "Contract",
        Start = "node",
        Nodes =
        [
            new WorkflowNode
            {
                Id = "node",
                Type = type,
                Label = "Node",
            },
        ],
        Edges = [],
    };

    private sealed record ContractEntry(
        string YamlType,
        string ApiType,
        string Label,
        bool Authorable);
}
