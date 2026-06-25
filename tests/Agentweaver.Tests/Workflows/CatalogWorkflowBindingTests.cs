using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Feature 015 US3 — the seven selectable catalog workflows must BIND onto the real run pipeline, not just
/// load. Before the binder learned the generic catalog topologies (Agent→Agent sequential turns, AI
/// peer-review verdict gates, direct Agent→Scribe / Review→Scribe completion, RAI→Merge publish), each of
/// these threw <see cref="WorkflowBindException"/> at runtime. This test drives the REAL
/// <see cref="RunWorkflowFactory"/> for each catalog workflow and asserts the MAF graph builds — MAF's
/// typed <c>Build()</c> validates every edge's input/output contract, so a successful build proves the
/// generic wiring is type-correct end to end.
/// </summary>
public sealed class CatalogWorkflowBindingTests
{
    // bug-fix:           triage → fix (Agent→Agent), fix → verify (Agent→peer-review gate), verify verdict
    //                    routing, merge → verify (blocked re-enter gate).
    // code-review:       starts at a pass-through peer_review, review → feedback (Agent→Agent),
    //                    feedback → scribe (direct completion, no merge).
    // content-authoring: research → draft → edit (sequential), RAI → publish (publish-style direct merge),
    //                    publish → edit (blocked re-enter producer).
    // incident-response: verify → review-gate (Agent→human-review), review-gate → postmortem (approved →
    //                    next turn), postmortem → scribe (direct completion).
    // pm-discovery:      review-gate → scribe (approved → direct completion, no merge).
    // software-delivery: implement → test-gate (Agent→peer-review gate), test-gate → rai (pass → RAI),
    //                    test-gate → implement (fail loop), RAI → code-review (review → next turn).
    [Theory]
    [InlineData("bug-fix")]
    [InlineData("code-review")]
    [InlineData("content-authoring")]
    [InlineData("incident-response")]
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_BindsOntoRealRunPipeline_WithoutThrowing(string workflowId)
    {
        using var appFactory = new WorkflowWebApplicationFactory();
        var factory = appFactory.Services.GetRequiredService<RunWorkflowFactory>();

        var definition = LoadCatalogWorkflow(workflowId);

        var act = () => factory.BuildWorkflowForTest(isChild: false, definition);

        act.Should().NotThrow(
            because: $"catalog workflow '{workflowId}' must bind onto the real executors (Feature 015 US3)");
    }

    [Theory]
    [InlineData("bug-fix")]
    [InlineData("code-review")]
    [InlineData("content-authoring")]
    [InlineData("incident-response")]
    [InlineData("pm-discovery")]
    [InlineData("software-delivery")]
    public void CatalogWorkflow_DescriptorTerminatesAtScribe(string workflowId)
    {
        using var appFactory = new WorkflowWebApplicationFactory();
        var factory = appFactory.Services.GetRequiredService<RunWorkflowFactory>();

        var definition = LoadCatalogWorkflow(workflowId);

        var (_, descriptor) = factory.BuildWorkflowForTest(isChild: false, definition);

        descriptor.Nodes.Should().Contain(n => n.Id == "scribe",
            because: "every catalog workflow records its outcome through the scribe stage");
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
