using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowScopeDifferTests
{
    [Fact]
    public void DefinitionStepChangesRequireConfirmationAndVersionOnlyChangesDoNot()
    {
        var previous = WorkflowTestData.Snapshot();
        var changedDefinition = WorkflowTestData.Definition() with
        {
            CatalogVersion = "catalog-2",
            Steps = [.. WorkflowTestData.Definition().Steps,
                WorkflowTestData.Open("explore", 3, ["review"], minimum: 0)]
        };
        var next = WorkflowTestData.Snapshot(changedDefinition);
        var versionOnly = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition() with { CatalogVersion = "catalog-2" });

        var diff = WorkflowScopeDiffer.Compare(previous, next);
        var versionDiff = WorkflowScopeDiffer.Compare(previous, versionOnly);

        Assert.True(diff.RequiresConfirmation);
        Assert.Equal(["explore"], diff.AddedStepIds.ToArray());
        Assert.False(versionDiff.RequiresConfirmation);
    }

    [Fact]
    public void ChangingStepModelTaskKindRequiresConfirmation()
    {
        var previous = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, [])));
        var next = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("implement", 0, []) with
                {
                    ModelTaskKind = ModelTaskKind.CodeWriting
                }));

        var diff = WorkflowScopeDiffer.Compare(previous, next);

        Assert.True(diff.RequiresConfirmation);
        Assert.Equal(["implement"], diff.ChangedStepIds.ToArray());
    }

    [Fact]
    public void WorkPlanRevisionReportsAddedAndChangedScope()
    {
        var workflow = WorkflowTestData.Snapshot();
        var context = WorkflowTestData.SelectionContext();
        var nextContext = WorkflowTestData.SelectionContext(
            modelTaskSelections:
            [
                new(ModelTaskKind.Straightforward, null, null, "model:provider/alpha"),
                new(ModelTaskKind.Straightforward, "engineer", "agent-1", "model:provider/beta")
            ]);
        var previous = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            context);
        var next = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(
                WorkflowTestData.Item("first", modelSelection: "model:provider/beta"),
                WorkflowTestData.Item("second", role: "researcher", agent: "agent-2")),
            nextContext);

        Assert.True(previous.IsValid);
        Assert.True(next.IsValid);
        var diff = WorkflowScopeDiffer.Compare(previous.Value!, next.Value!);
        Assert.True(diff.RequiresConfirmation);
        Assert.Equal(["second"], diff.AddedWorkItemIds.ToArray());
        Assert.Equal(["first"], diff.ChangedWorkItemIds.ToArray());
    }

    [Fact]
    public void WorkflowDefinitionAndProviderPinChangesAreIncludedInWorkPlanScopeDiff()
    {
        var previousWorkflow = WorkflowTestData.Snapshot();
        var revisedWorkflow = WorkflowTestData.Snapshot(
            WorkflowTestData.Definition() with
            {
                Steps = [.. WorkflowTestData.Definition().Steps,
                    WorkflowTestData.Open("explore", 3, ["review"], minimum: 0)]
            });
        var previous = WorkPlanValidator.ValidateAndSnapshot(
            previousWorkflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work")),
            WorkflowTestData.SelectionContext());
        var revisedProvider = WorkflowTestData.PinnedSandboxBinding(
            "sandbox.revised", "sandbox.workspace.write");
        var next = WorkPlanValidator.ValidateAndSnapshot(
            revisedWorkflow,
            WorkflowTestData.Plan(WorkflowTestData.Item("work", providerId: "sandbox.revised")),
            WorkflowTestData.SelectionContext(revisedProvider));

        Assert.True(previous.IsValid);
        Assert.True(next.IsValid);
        var diff = WorkflowScopeDiffer.Compare(previous.Value!, next.Value!);
        Assert.True(diff.WorkflowDefinitionChanged);
        Assert.True(diff.IsolationProviderBindingChanged);
        Assert.True(diff.RequiresConfirmation);
    }
}
