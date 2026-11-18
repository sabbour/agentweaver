using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CoordinationExecutionPlanEndpointTests
{
    [Fact]
    public void ExecutionPlanResponseContainsTheCompleteSavedPlan()
    {
        var snapshot = CreateSnapshot(overflow: false);

        var bytes = CoordinationEndpoints.SerializeExecutionPlanResponse(snapshot);

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(
            snapshot.WorkPlan.Plan.Id,
            document.RootElement.GetProperty("workPlan").GetProperty("plan").GetProperty("id").GetString());
    }

    [Fact]
    public void ExecutionPlanResponseRejectsOverflowWithoutTruncating()
    {
        var snapshot = CreateSnapshot(overflow: true);

        var error = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.SerializeExecutionPlanResponse(snapshot));

        Assert.Equal("execution_plan_unavailable", error.Code);
        Assert.Equal(409, error.StatusCode);
    }

    private static CoordinationExecutionPlanSnapshot CreateSnapshot(bool overflow)
    {
        var steps = overflow
            ? new[]
            {
                WorkflowTestData.Open("implement-a", 0, [], maximum: 128, roles: ["engineer"]),
                WorkflowTestData.Open("implement-b", 1, [], maximum: 128, roles: ["engineer"])
            }
            : [WorkflowTestData.Open("implement-a", 0, [], roles: ["engineer"])];
        var definition = WorkflowTestData.Definition(WorkflowDefinitionOrigin.BuiltIn, steps) with
        {
            MaximumWorkItems = WorkflowDomainLimits.MaximumWorkItems
        };
        var definitionSnapshot = WorkflowTestData.Snapshot(definition);
        var items = Enumerable.Range(0, overflow ? WorkflowDomainLimits.MaximumWorkItems : 1)
            .Select(index =>
            {
                var id = $"item-{index:D3}";
                var outputs = overflow
                    ? Enumerable.Range(0, WorkflowDomainLimits.MaximumOutputsPerItem)
                        .Select(output => $"src/{id}/file-{output:D2}/{new string('a', 1000)}")
                        .ToArray()
                    : null;
                return WorkflowTestData.Item(
                    id,
                    stepId: !overflow || index % 2 == 0 ? "implement-a" : "implement-b",
                    outputs: outputs);
            })
            .ToArray();
        var validated = WorkPlanValidator.ValidateAndSnapshot(
            definitionSnapshot,
            WorkflowTestData.Plan(items),
            WorkflowTestData.SelectionContext());
        Assert.True(validated.IsValid, string.Join("; ", validated.Issues.Select(issue => issue.Message)));

        return new CoordinationExecutionPlanSnapshot(
            new SessionIdentity("project-1", "run-1", "root"),
            new string('a', 64),
            1,
            1,
            validated.Value!,
            null,
            ImmutableArray<CoordinationExecutionChildSnapshot>.Empty,
            null);
    }
}
