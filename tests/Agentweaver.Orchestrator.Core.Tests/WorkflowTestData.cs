using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

internal static class WorkflowTestData
{
    public static WorkflowDefinition Definition(
        WorkflowDefinitionOrigin origin = WorkflowDefinitionOrigin.BuiltIn,
        params WorkflowStepDefinition[] steps) =>
        new(
            "workflow.core",
            "revision-1",
            "catalog-1",
            origin,
            32,
            steps.Length == 0
                ? [Fixed("setup", 0, [], ["docs/plan.md"]),
                    Open("implement", 1, ["setup"]),
                    Platform("review", 2, ["implement"])]
                : [.. steps]);

    public static WorkflowStepDefinition Fixed(
        string id,
        int order,
        string[] dependsOn,
        string[] outputs) =>
        new(
            id,
            $"Prescribed {id} work",
            WorkflowStepMode.Fixed,
            order,
            new WorkflowCardinality(1, 1),
            [.. dependsOn],
            ["planner"],
            ["planning"],
            ["worktree"],
            [],
            new FixedWorkSpecification(
                "Prepare plan",
                "Create the prescribed plan.",
                "planner",
                "planning",
                "worktree",
                [.. outputs]),
            null);

    public static WorkflowStepDefinition Open(
        string id,
        int order,
        string[] dependsOn,
        int minimum = 1,
        int maximum = 8,
        string[]? roles = null,
        string[]? phases = null,
        string[]? isolationChoices = null,
        string[]? requiredCapabilities = null) =>
        new(
            id,
            $"Open work for {id}",
            WorkflowStepMode.Open,
            order,
            new WorkflowCardinality(minimum, maximum),
            [.. dependsOn],
            [.. (roles ?? ["engineer", "researcher"])],
            [.. (phases ?? ["execution", "research", "validation"])],
            [.. (isolationChoices ?? ["worktree", "sandboxed"])],
            [.. (requiredCapabilities ?? ["sandbox.workspace.write"])],
            null,
            null);

    public static WorkflowStepDefinition Platform(
        string id,
        int order,
        string[] dependsOn,
        WorkflowPlatformGate gate = WorkflowPlatformGate.IndependentReview) =>
        new(
            id,
            $"Platform gate {id}",
            WorkflowStepMode.Platform,
            order,
            new WorkflowCardinality(1, 1),
            [.. dependsOn],
            [],
            [],
            [],
            [],
            null,
            gate);

    public static WorkPlanItem Item(
        string id,
        string stepId = "implement",
        string role = "engineer",
        string agent = "agent-1",
        string phase = "execution",
        string modelSelection = "model:provider/alpha",
        string providerId = "sandbox.test",
        string isolationChoice = "worktree",
        string[]? dependsOn = null,
        string[]? outputs = null) =>
        new(
            id,
            stepId,
            $"Work item {id}",
            "Implement the requested change.",
            role,
            agent,
            phase,
            modelSelection,
            providerId,
            isolationChoice,
            [.. (dependsOn ?? [])],
            [.. (outputs ?? [$"src/{id}.cs"])]);

    public static WorkPlan Plan(params WorkPlanItem[] items) =>
        new("plan-1", "workflow.core", "revision-1", "catalog-1", [.. items]);

    public static WorkflowDefinitionSnapshot Snapshot(
        WorkflowDefinition? definition = null)
    {
        var result = WorkflowDefinitionValidator.ValidateAndSnapshot(
            definition ?? Definition());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Value!;
    }

    public static WorkPlanRunSelectionContext SelectionContext(
        PinnedProviderBinding? binding = null,
        string[]? engineerIsolationChoices = null) =>
        new(
        [
            new RoleRunSelection(
                "engineer",
                ["agent-1"],
                ["model:provider/alpha", "model:provider/beta"],
                [.. (engineerIsolationChoices ?? ["worktree", "sandboxed"])]),
            new RoleRunSelection(
                "researcher",
                ["agent-2"],
                ["model:provider/beta"],
                ["worktree", "sandboxed"]),
            new RoleRunSelection(
                "planner",
                [],
                [],
                ["worktree"])
        ],
        binding ?? PinnedSandboxBinding());

    public static PinnedProviderBinding PinnedSandboxBinding(
        string providerId = "sandbox.test",
        params string[] capabilities)
    {
        var capabilitySet = capabilities.Length == 0
            ? ImmutableHashSet.Create(StringComparer.Ordinal, "sandbox.workspace.write")
            : capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                providerId,
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.InProcess,
                capabilitySet),
            true,
            "options-v1",
            1);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, providerId)],
            []);
        Assert.True(catalog.IsSuccess);
        var resolver = new ProviderResolver(catalog.Value!);
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)));
        Assert.True(resolution.IsSuccess);
        var pinned = resolver.Pin(
            "run-1",
            resolution.Value!.Candidate!,
            "sandbox-resource",
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.Sandbox, providerId, "sandbox-resource", 1),
                capabilitySet));
        Assert.True(pinned.IsSuccess);
        return pinned.Value!;
    }
}
