using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("workPlan").GetProperty("workflow")
                .GetProperty("definition").GetProperty("steps")[0]
                .GetProperty("buildTestCommand").ValueKind);
    }

    [Fact]
    public void ExecutionPlanResponseRoundTripsTypedWorkspaceSource()
    {
        var source = new SandboxBuildTestWorkspaceSource(
            2,
            new("project", "run", "session"),
            "tenant",
            "issuer",
            "actor",
            "source-pin",
            new string('a', 64),
            new(ProviderSeam.SourceControl, "github", "repository", 1),
            "workspace",
            Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            "branch",
            new string('a', 40),
            new string('c', 40),
            "seed",
            new string('a', 64),
            1024,
            new string('c', 64),
            512,
            new string('d', 64),
            128);
        var command = new WorkflowBuildTestCommand(
            "profile-1",
            "/usr/bin/dotnet",
            ["test", "--no-restore"],
            ".",
            [new("results", "artifacts/results.xml", true, 1024)])
        {
            WorkspaceSource = source
        };
        var definition = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Platform("build-test", 0, [], WorkflowPlatformGate.BuildTest) with
            {
                BuildTestCommand = command
            });
        var workflow = WorkflowTestData.Snapshot(definition);
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(),
            new([], WorkflowTestData.PinnedSandboxBinding(
                "sandbox.test", SandboxCapabilities.BuildTestCommandPod)));
        Assert.True(plan.IsValid, string.Join("; ", plan.Issues.Select(issue => issue.Message)));
        var snapshot = new CoordinationExecutionPlanSnapshot(
            new SessionIdentity("project", "run", "session"),
            new string('a', 64),
            1,
            1,
            plan.Value!,
            null,
            ImmutableArray<CoordinationExecutionChildSnapshot>.Empty,
            null);

        var bytes = CoordinationEndpoints.SerializeExecutionPlanResponse(snapshot);
        using var document = JsonDocument.Parse(bytes);
        var workspaceSource = document.RootElement.GetProperty("workPlan").GetProperty("workflow")
            .GetProperty("definition").GetProperty("steps")[0]
            .GetProperty("buildTestCommand").GetProperty("workspaceSource");
        Assert.Equal("project", workspaceSource.GetProperty("identity").GetProperty("projectId").GetString());
        Assert.Equal(
            "repository",
            workspaceSource.GetProperty("sourceControlResource").GetProperty("resourceId").GetString());
        Assert.False(workspaceSource.TryGetProperty("preparedRepositoryRelativePath", out _));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var restoredDefinition = JsonSerializer.Deserialize<WorkflowDefinition>(
            document.RootElement.GetProperty("workPlan").GetProperty("workflow")
                .GetProperty("definition").GetRawText(),
            options);
        Assert.Equal(
            source,
            restoredDefinition!.Steps[0].BuildTestCommand!.WorkspaceSource);
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
