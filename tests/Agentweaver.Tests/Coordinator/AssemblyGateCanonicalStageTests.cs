using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Workflows;
using Agentweaver.Squad.Catalog;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for the assembly-gate stuck-run root cause: a workflow-derived human-review gate
/// must persist the CANONICAL <see cref="AssemblyStage.Review"/> ("review") stage id — never the raw
/// workflow node id ("human-review"). If it persists the node id, the work plan's <c>AssemblyStage</c>
/// never equals <see cref="AssemblyStage.Review"/>, so
/// <c>CoordinatorAssemblyReviewPersistence.IsWorkPlanAwaitingReviewAsync</c> returns false, the
/// <c>POST /assembly/review</c> approval 409s with <c>no_assembly_review_pending</c>, and the run
/// parks forever in <c>awaiting_review</c>. Blank/default-workflow projects hit this every time
/// because the default coordinator workflow's human-review node id is "human-review".
/// </summary>
public sealed class AssemblyGateCanonicalStageTests
{
    [Fact]
    public void ResolveAssemblyGates_UsesWorkflowEdgeTraversal_NotNodeDeclarationOrder()
    {
        var workflow = new WorkflowDefinition
        {
            Id = "misdeclared-flow",
            Name = "Misdeclared Flow",
            Start = "start",
            Nodes =
            [
                new() { Id = "start", Type = WorkflowNodeType.Prompt, Label = "Start" },
                new() { Id = "build-test", Type = WorkflowNodeType.BuildTest, Label = "Build & Test" },
                new()
                {
                    Id = "rai-check",
                    Type = WorkflowNodeType.Check,
                    Label = "RAI Check",
                    GateKind = "rai",
                    Branches = ["review"],
                },
                new()
                {
                    Id = "review-gate",
                    Type = WorkflowNodeType.Check,
                    Label = "Review Gate",
                    GateKind = "human-review",
                    Branches = ["approved"],
                },
                new() { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done" },
            ],
            Edges =
            [
                new() { From = "start", To = "build-test", When = "request-changes" },
                new() { From = "start", To = "rai-check" },
                new() { From = "rai-check", To = "done", When = "safety-failed" },
                new() { From = "rai-check", To = "build-test", When = "review" },
                new() { From = "build-test", To = "review-gate", When = "approved" },
                new() { From = "review-gate", To = "done", When = "approved" },
            ],
        };

        var gates = CoordinatorAssemblyService.ResolveAssemblyGates(workflow);

        gates.Select(g => g.GateKind).Should().Equal("rai", "build-test", "human-review");
    }

    [Theory]
    [InlineData("software-delivery")]
    [InlineData("bug-fix")]
    public void ResolveAssemblyGates_SoftwareCatalogWorkflowsOrderRaiBeforeBuildTest(string workflowId)
    {
        var workflow = LoadCatalogWorkflow(workflowId);

        var gates = CoordinatorAssemblyService.ResolveAssemblyGates(workflow);
        var gateKinds = gates.Select(g => g.GateKind).ToList();

        gateKinds.Should().Contain("rai");
        gateKinds.Should().Contain("build-test");
        gateKinds.IndexOf("rai").Should().BeLessThan(gateKinds.IndexOf("build-test"));
    }

    [Fact]
    public void CanonicalStageId_HumanReviewKind_MapsToReviewStage()
    {
        // The workflow node id is arbitrary ("human-review"); the persisted stage MUST be canonical.
        CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review")
            .Should().Be(AssemblyStage.Review);

        // Even if a workflow authors the node id as "review" (gate_kind still normalizes to
        // human-review), the canonical stage is unchanged.
        CoordinatorGraphDescriptor.CanonicalStageId("human-review", "review")
            .Should().Be(AssemblyStage.Review);
    }

    [Fact]
    public void CanonicalStageId_RaiKind_MapsToRaiStage()
    {
        CoordinatorGraphDescriptor.CanonicalStageId("rai", "rai")
            .Should().Be(AssemblyStage.Rai);
        CoordinatorGraphDescriptor.CanonicalStageId("rai", "content-safety")
            .Should().Be(AssemblyStage.Rai);
    }

    [Fact]
    public void CanonicalStageId_KindsWithoutCanonicalStage_FallBackToNodeId()
    {
        CoordinatorGraphDescriptor.CanonicalStageId("build-test", "verify").Should().Be("verify");
        CoordinatorGraphDescriptor.CanonicalStageId("rubberduck", "duck").Should().Be("duck");
    }

    [Fact]
    public void HumanReviewGate_BuiltCanonically_MatchesValidationAndGraphContract()
    {
        // Mirror how ResolveAssemblyGatesAsync now builds a workflow-derived human-review gate.
        var gate = new CoordinatorGraphDescriptor.AssemblyGateNode(
            CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review"),
            "Human Review",
            "human-review");

        // StageId is what gets persisted as WorkPlan.AssemblyStage and validated against
        // AssemblyStage.Review by IsWorkPlanAwaitingReviewAsync.
        gate.StageId.Should().Be(AssemblyStage.Review);

        // GraphNodeId must match the canonical planned node id the frontend renders (same as the
        // default gates), not a divergent "planned:assembly-human-review".
        gate.GraphNodeId.Should().Be(CoordinatorGraphDescriptor.AssemblyReviewNodeId);

        gate.Role.Should().Be("review");
    }

    [Fact]
    public void DefaultAndWorkflowDerivedHumanReviewGates_ProduceIdenticalStageId()
    {
        var defaultHumanGate = CoordinatorGraphDescriptor.DefaultAssemblyGates
            .Single(g => g.GateKind == "human-review");

        var workflowDerived = new CoordinatorGraphDescriptor.AssemblyGateNode(
            CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review"),
            "Human Review",
            "human-review");

        workflowDerived.StageId.Should().Be(defaultHumanGate.StageId);
        workflowDerived.GraphNodeId.Should().Be(defaultHumanGate.GraphNodeId);
    }

    // ── #387: gate applicability from the actual task ────────────────────────────────────────────
    // A non-code-producing work plan (all subtasks are planning-phase deliverables) has nothing to
    // build or test, so the platform build_test gate must be dropped rather than scheduled — otherwise
    // it finds no code, requests changes, and loops forever.

    [Fact]
    public void ResolveAssemblyGates_NonCodeProducingPlan_DropsBuildTestGate()
    {
        var workflow = BuildSoftwareWorkflow();

        var codeGates = CoordinatorAssemblyService.ResolveAssemblyGates(workflow, producesCode: true);
        var nonCodeGates = CoordinatorAssemblyService.ResolveAssemblyGates(workflow, producesCode: false);

        codeGates.Select(g => g.GateKind).Should().Contain("build-test");
        nonCodeGates.Select(g => g.GateKind).Should().NotContain("build-test");
        // Other authored gates (RAI, human-review) are preserved for non-code plans.
        nonCodeGates.Select(g => g.GateKind).Should().Contain(new[] { "rai", "human-review" });
    }

    [Fact]
    public void ProducesCode_AllPlanningSubtasks_IsFalse()
    {
        CoordinatorAssemblyGateResolver.ProducesCode(new[] { "planning", "planning" }).Should().BeFalse();
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("validation")]
    [InlineData("none")]
    [InlineData(null)]
    public void ProducesCode_AnyNonPlanningSubtask_IsTrue(string? phase)
    {
        CoordinatorAssemblyGateResolver.ProducesCode(new[] { "planning", phase }).Should().BeTrue();
    }

    [Fact]
    public void ProducesCode_NoSubtasks_IsTrueByDefault()
    {
        // Pre-decomposition / unknown: keep the gate (conservative — only drop when confident).
        CoordinatorAssemblyGateResolver.ProducesCode(Array.Empty<string?>()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Update the README", "Write a new README section documenting coordinator workflows.", """["README.md"]""")]
    [InlineData("Update documentation", "Document how coordinator workflows are selected.", "[]")]
    public void ProducesCode_SingleExecutionDocumentationSubtask_IsFalse(
        string title,
        string scope,
        string declaredOutputPathsJson)
    {
        var subtask = new CoordinatorAssemblyGateResolver.SubtaskGateMetadata(
            title,
            scope,
            "execution",
            declaredOutputPathsJson);
        var producesCode = CoordinatorAssemblyGateResolver.ProducesCode([subtask]);
        var gates = CoordinatorAssemblyService.ResolveAssemblyGates(
            BuildSoftwareWorkflow(),
            producesCode);

        producesCode.Should().BeFalse();
        gates.Select(g => g.GateKind).Should().NotContain("build-test");
    }

    [Fact]
    public void ProducesCode_SingleExecutionCodeSubtask_IsTrue()
    {
        var subtask = new CoordinatorAssemblyGateResolver.SubtaskGateMetadata(
            "Implement the API",
            "Add the coordinator endpoint and its tests.",
            "execution",
            """["apps/Agentweaver.Api/Endpoints/RunEndpoints.cs"]""");

        CoordinatorAssemblyGateResolver.ProducesCode([subtask]).Should().BeTrue();
    }

    [Fact]
    public void ProducesCode_ExecutionTaskWithMarkdownNamedImplementationOutput_IsTrue()
    {
        var subtask = new CoordinatorAssemblyGateResolver.SubtaskGateMetadata(
            "Update the PRD parser",
            "Update parser.md as part of the parser implementation.",
            "execution",
            """["parser.md"]""");

        CoordinatorAssemblyGateResolver.ProducesCode([subtask]).Should().BeTrue();
    }

    private static WorkflowDefinition BuildSoftwareWorkflow() => new()
    {
        Id = "software-flow",
        Name = "Software Flow",
        Start = "implement",
        Nodes =
        [
            new() { Id = "implement", Type = WorkflowNodeType.Prompt, Label = "Implement" },
            new()
            {
                Id = "rai-check",
                Type = WorkflowNodeType.Check,
                Label = "RAI Check",
                GateKind = "rai",
                Branches = ["review"],
            },
            new() { Id = "build-test", Type = WorkflowNodeType.BuildTest, Label = "Build & Test" },
            new()
            {
                Id = "human-review",
                Type = WorkflowNodeType.Check,
                Label = "Human Review",
                GateKind = "human-review",
                Branches = ["approved"],
            },
            new() { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done" },
        ],
        Edges =
        [
            new() { From = "implement", To = "rai-check" },
            new() { From = "rai-check", To = "build-test", When = "review" },
            new() { From = "build-test", To = "human-review", When = "approved" },
            new() { From = "human-review", To = "done", When = "approved" },
        ],
    };

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
