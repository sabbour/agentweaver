using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;
using FluentAssertions;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorWorkflowHintTests
{
    [Fact]
    public void BuildWorkflowHint_OmitsPlatformOwnedStages_ForCoordinatorDecomposition()
    {
        var workflow = LoadCatalogWorkflow("content-authoring");

        var hint = CoordinatorOrchestratorExecutor.BuildWorkflowHint(workflow);

        hint.Should().Contain("Editorial Review");
        hint.Should().Contain("Publish");
        hint.Should().NotContain("RAI Check");
        hint.Should().NotContain("Scribe");
        hint.Should().Contain("coordinator collective assembly supplies those exactly once");
    }

    [Theory]
    [InlineData("software-delivery", "Code Review")]
    [InlineData("bug-fix", "Build & Test")]
    [InlineData("incident-response", "Postmortem")]
    [InlineData("pm-discovery", "Stakeholder Review")]
    [InlineData("agent-evaluation", "Evaluation Report")]
    public void BuildWorkflowHint_KeepsDomainStages_WhenFilteringPlatformStages(
        string workflowId,
        string expectedDomainStage)
    {
        var workflow = LoadCatalogWorkflow(workflowId);

        var hint = CoordinatorOrchestratorExecutor.BuildWorkflowHint(workflow);

        hint.Should().Contain(expectedDomainStage);
        hint.Should().NotContain("Scribe");
        hint.Should().NotContain("RAI Check");
        hint.Should().NotContain("Safety Gate");
        hint.Should().NotContain("Review Gate");
        hint.Should().NotContain("Human Review");
        hint.Should().NotContain("Merge (role:");
    }

    private static WorkflowDefinition LoadCatalogWorkflow(string workflowId)
    {
        var reader = new CatalogReader();
        foreach (var (yaml, source) in reader.LoadAllWorkflowYamls())
        {
            var result = WorkflowDefinitionLoader.Load(yaml, source, isBuiltIn: true);
            if (result.IsValid
                && result.Definition is not null
                && string.Equals(result.Definition.Id, workflowId, StringComparison.Ordinal))
                return result.Definition;
        }

        throw new InvalidOperationException($"Catalog workflow '{workflowId}' was not found.");
    }
}
