using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Regression for bug #168 (revised RCA). The default review policy composes an injected RAI gate
/// whose "revise" edge loops back to the workflow's START/producer node. The policy-gate wiring used
/// to match the literal id "agent" — the canonical start id of the DEFAULT workflow only — so composing
/// the policy onto a CATALOG workflow whose start node has a different id (e.g. bug-fix's "triage")
/// threw <see cref="WorkflowBindException"/> at build time. A real bug-fix run therefore failed to bind
/// its effective (policy-composed) definition and fell back to a plan that silently DROPPED the
/// downstream "build-test" peer-review node (and never started the preview). The binder now resolves the
/// loop-back target's per-node executor, so every catalog workflow binds under the default policy and the
/// full pipeline — including build-test — survives into the execution graph.
/// </summary>
public sealed class ReviewPolicyCompositionBindingTests
{
    [Theory]
    [InlineData("bug-fix")]
    [InlineData("content-authoring")]
    [InlineData("incident-response")]
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_BindsUnderDefaultReviewPolicy_WithoutThrowing(string workflowId)
    {
        using var appFactory = new WorkflowWebApplicationFactory();
        var factory = appFactory.Services.GetRequiredService<RunWorkflowFactory>();

        var effective = ComposeWithDefaultPolicy(LoadCatalogWorkflow(workflowId));

        var act = () => factory.BuildWorkflowForTest(isChild: false, effective);

        act.Should().NotThrow(
            because: $"catalog workflow '{workflowId}' must bind onto the real executors after the default " +
                     "review policy is composed (bug #168): the injected RAI gate's revise loop targets the " +
                     "workflow's own start node, not the canonical 'agent' id");
    }

    [Fact]
    public void BugFix_UnderDefaultPolicy_KeepsBuildTestNodeInDescriptor()
    {
        using var appFactory = new WorkflowWebApplicationFactory();
        var factory = appFactory.Services.GetRequiredService<RunWorkflowFactory>();

        var effective = ComposeWithDefaultPolicy(LoadCatalogWorkflow("bug-fix"));

        var (_, descriptor) = factory.BuildWorkflowForTest(isChild: false, effective);

        descriptor.Nodes.Should().Contain(n => n.Id == "build-test",
            because: "the bug-fix Build & Test peer-review step must survive policy composition into the execution graph (#168)");
        descriptor.Nodes.Should().Contain(n => n.Id == "verify",
            because: "the verify peer-review step precedes build-test and must also be present");
        descriptor.Nodes.Should().Contain(n => n.Id == "policy-rai",
            because: "the default review policy injects an RAI gate before merge");
    }

    private static WorkflowDefinition ComposeWithDefaultPolicy(WorkflowDefinition definition) =>
        ReviewPolicyComposer.ComposeForRuntime(definition, BuiltInReviewPolicies.Default.Policy!).Effective;

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
