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
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_LoadsAndIsBindableForCoordinatorSelection(string workflowId)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);

        errors.Should().BeEmpty(because: $"catalog workflow '{workflowId}' must be selectable by the coordinator");
    }

    [Theory]
    [InlineData("software-delivery", new[] { "rai", "rubberduck", "human-review" })]
    [InlineData("bug-fix", new[] { "rai", "human-review" })]
    [InlineData("content-authoring", new[] { "rai", "human-review" })]
    [InlineData("incident-response", new[] { "human-review" })]
    [InlineData("pm-discovery", new[] { "human-review" })]
    [InlineData("agent-evaluation", new[] { "rai" })]
    public void CatalogWorkflow_DeclaresExpectedAuthorableGates_WithoutMergeOrScribe(
        string workflowId,
        string[] expectedGates)
    {
        var definition = LoadCatalogWorkflow(workflowId);

        var gates = definition.Nodes
            .Where(n => n.Type == WorkflowNodeType.Check)
            .Select(NodeClassifier.NormalizeGateKind)
            .Where(g => g is not null)
            .ToArray();

        gates.Should().Equal(expectedGates);
        definition.Nodes.Should().NotContain(n =>
            n.Type == WorkflowNodeType.Merge || n.Type == WorkflowNodeType.Scribe);
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
