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

    [Fact]
    public void BuildWorkflowHint_TellsCoordinatorTheWorkflowIsNotACapOnThePlan()
    {
        var workflow = LoadCatalogWorkflow("software-delivery");

        var hint = CoordinatorOrchestratorExecutor.BuildWorkflowHint(workflow);

        // GitHub #225 item#1: the workflow hint must not anchor the decomposition to the
        // delivery-only pipeline. It should invite the coordinator to add earlier lifecycle
        // stages the outcome implies but the workflow topology does not model.
        hint.Should().Contain("not a cap on the plan");
        hint.Should().Contain("if the desired outcome implies earlier");
        hint.Should().Contain("ADD subtasks for them");

        // The old minimality/anchoring language must be gone.
        hint.Should().NotContain("fit this intended pipeline");
        hint.Should().NotContain("SHAPE of the decomposition");

        // The platform-node exclusion guidance is untouched by the #225 fix.
        hint.Should().Contain("coordinator collective assembly supplies those exactly once");
    }

    [Theory]
    [InlineData("software-delivery", "Code Review")]
    [InlineData("bug-fix", "Verify")]
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
        hint.Should().NotContain("Build & Test");
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
