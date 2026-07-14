using FluentAssertions;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Catalog workflows are coordinator-authored topologies: special review gates are first-class workflow
/// nodes, while merge and scribe are platform-appended by coordinator assembly.
/// </summary>
public sealed class CatalogWorkflowBindingTests
{
    [Theory]
    [InlineData("bug-fix")]
    [InlineData("content-authoring")]
    [InlineData("incident-response")]
    [InlineData("infra-ops")]
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_LoadsAndIsBindableForCoordinatorSelection(string workflowId)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);

        errors.Should().BeEmpty(because: $"catalog workflow '{workflowId}' must be selectable by the coordinator");
    }

    [Theory]
    [InlineData("software-delivery", new[] { "rai", "rubberduck", "build-test", "human-review" })]
    [InlineData("bug-fix", new[] { "rai", "build-test", "human-review" })]
    [InlineData("content-authoring", new[] { "rai", "human-review" })]
    [InlineData("incident-response", new[] { "human-review" })]
    [InlineData("infra-ops", new[] { "rai", "human-review" })]
    [InlineData("pm-discovery", new[] { "human-review" })]
    [InlineData("agent-evaluation", new[] { "rai" })]
    public void CatalogWorkflow_DeclaresExpectedAuthorableGates_WithoutMergeOrScribe(
        string workflowId,
        string[] expectedGates)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var gates = definition.Nodes
            .Where(n => n.Type == WorkflowNodeType.Check || n.Type == WorkflowNodeType.BuildTest)
            .Select(n => n.Type == WorkflowNodeType.BuildTest ? "build-test" : NodeClassifier.NormalizeGateKind(n))
            .Where(g => g is not null)
            .ToArray();

        gates.Should().Equal(expectedGates);
        definition.Nodes.Should().NotContain(n =>
            n.Type == WorkflowNodeType.Merge || n.Type == WorkflowNodeType.Scribe);
    }

    [Theory]
    [InlineData("software-delivery")]
    [InlineData("bug-fix")]
    public void CatalogWorkflow_UsesPlatformBuildTestGateWithoutInlinePrompt(string workflowId)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var buildTest = definition.Nodes.Single(n => n.Id == "build-test");

        buildTest.Type.Should().Be(WorkflowNodeType.BuildTest);
        buildTest.Label.Should().Be("Build & Test");
        buildTest.Agent.Should().Be("qa-engineer");
        buildTest.Prompt.Should().BeNull();
    }

    private static WorkflowDefinition LoadCatalogWorkflow(string workflowId)
    {
        var reader = new CatalogReader();
        foreach (var (yaml, source) in reader.LoadAllWorkflowYamls())
        {
            var result = WorkflowDefinitionLoader.Load(yaml, source, isBuiltIn: false);
            if (result.IsValid && result.Definition is not null &&
                string.Equals(result.Definition.Id, workflowId, StringComparison.Ordinal))
            {
                return result.Definition;
            }
        }

        throw new InvalidOperationException(
            $"Catalog workflow '{workflowId}' was not found among the embedded workflow resources.");
    }
}
