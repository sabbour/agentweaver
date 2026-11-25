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
        Assert.Equal(snapshot.SavedPlanFingerprint.Version,
            document.RootElement.GetProperty("savedPlanFingerprint").GetProperty("version").GetInt32());
        Assert.Equal(snapshot.SavedPlanFingerprint.Sha256,
            document.RootElement.GetProperty("savedPlanFingerprint").GetProperty("sha256").GetString());
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
            CoordinationOwnerStore.ComputeSavedPlanFingerprint(plan.Value!),
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecutionPlanResponseWritesOnlyVerifiedReceiptLinks(bool linked)
    {
        var messageId = Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d");
        var receiptHash = new string('a', 64);
        var child = new CoordinationExecutionChildSnapshot(
            "child", "session", "item", "item", "completed", 1, false, messageId,
            new string('b', 64), new string('c', 64), new string('d', 64), receiptHash)
        {
            CompletedReceiptLink = linked ? new(messageId, receiptHash) : null
        };
        var snapshot = CreateSnapshot(overflow: false) with { Children = [child] };

        using var document = JsonDocument.Parse(CoordinationEndpoints.SerializeExecutionPlanResponse(snapshot));
        var serializedChild = document.RootElement.GetProperty("children")[0];
        Assert.Equal(receiptHash, serializedChild.GetProperty("turnReceiptHash").GetString());
        Assert.Equal(linked, serializedChild.TryGetProperty("completedReceiptLink", out var link));
        if (linked)
        {
            Assert.Equal(messageId.ToString("D"), link.GetProperty("messageId").GetString());
            Assert.Equal(receiptHash, link.GetProperty("receiptHash").GetString());
            Assert.Equal(2, link.EnumerateObject().Count());
        }
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

    [Fact]
    public void SavedPlanFingerprintIsStableAndCapabilitySetOrderIndependent()
    {
        var first = ValidPlan(
            WorkflowTestData.PinnedSandboxBinding(
                "sandbox.test", "sandbox.workspace.write", "sandbox.capability.z"));
        var reordered = ValidPlan(
            WorkflowTestData.PinnedSandboxBinding(
                "sandbox.test", "sandbox.capability.z", "sandbox.workspace.write"));

        var fingerprint = CoordinationOwnerStore.ComputeSavedPlanFingerprint(first);
        Assert.Equal(fingerprint, CoordinationOwnerStore.ComputeSavedPlanFingerprint(first));
        Assert.Equal(fingerprint, CoordinationOwnerStore.ComputeSavedPlanFingerprint(reordered));
        Assert.Equal(1, fingerprint.Version);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint.Sha256);
    }

    [Fact]
    public void SavedPlanFingerprintChangesForPlanMutations()
    {
        var baseline = ValidPlan(WorkflowTestData.PinnedSandboxBinding());
        var expected = CoordinationOwnerStore.ComputeSavedPlanFingerprint(baseline).Sha256;
        var workflowEdge = baseline.Workflow.Definition with
        {
            Steps = baseline.Workflow.Definition.Steps.SetItem(
                2,
                baseline.Workflow.Definition.Steps[2] with { DependsOn = ["setup"] })
        };
        var workItemEdge = baseline.Plan with
        {
            Items = baseline.Plan.Items.SetItem(
                1, baseline.Plan.Items[1] with { DependsOn = ["item-a"] })
        };
        var changedProvider = ValidPlan(
            WorkflowTestData.PinnedSandboxBindingAtGeneration(2));
        var changedUrl = baseline.Plan.Items[1] with
        {
            Task = "Read https://service.example/resource?revision=1#details."
        };
        var urlBaseline = ValidPlan(
            baseline.Workflow.Definition,
            baseline.Plan with { Items = baseline.Plan.Items.SetItem(1, changedUrl) },
            baseline.IsolationProviderBinding!);
        var urlHash = CoordinationOwnerStore.ComputeSavedPlanFingerprint(urlBaseline).Sha256;
        foreach (var task in new[]
        {
            "Read https://service.example/resource?revision=2#details.",
            "Read https://service.example/resource?revision=1#summary.",
        })
        {
            var changed = ValidPlan(
                baseline.Workflow.Definition,
                urlBaseline.Plan with
                {
                    Items = urlBaseline.Plan.Items.SetItem(
                        1, urlBaseline.Plan.Items[1] with { Task = task })
                },
                baseline.IsolationProviderBinding!);
            Assert.NotEqual(urlHash, CoordinationOwnerStore.ComputeSavedPlanFingerprint(changed).Sha256);
        }
        var mutations = new[]
        {
            ValidPlan(workflowEdge, baseline.Plan, baseline.IsolationProviderBinding!),
            ValidPlan(baseline.Workflow.Definition, workItemEdge, baseline.IsolationProviderBinding!),
            changedProvider,
            urlBaseline,
        };

        Assert.All(mutations, plan =>
            Assert.NotEqual(expected, CoordinationOwnerStore.ComputeSavedPlanFingerprint(plan).Sha256));
    }

    private static WorkPlanSnapshot ValidPlan(PinnedProviderBinding binding)
        => ValidPlan(WorkflowTestData.Definition(), WorkflowTestData.Plan(
                WorkflowTestData.Item("item-a"),
                WorkflowTestData.Item("item-b")), binding);

    private static WorkPlanSnapshot ValidPlan(
        WorkflowDefinition definition,
        WorkPlan workPlan,
        PinnedProviderBinding binding)
    {
        var result = WorkPlanValidator.ValidateAndSnapshot(
            WorkflowTestData.Snapshot(definition),
            workPlan,
            WorkflowTestData.SelectionContext(binding));
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
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
            CoordinationOwnerStore.ComputeSavedPlanFingerprint(validated.Value!),
            null,
            ImmutableArray<CoordinationExecutionChildSnapshot>.Empty,
            null);
    }
}
