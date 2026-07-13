using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;
using FluentAssertions;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorSubtaskTaskTests
{
    [Fact]
    public void DecompositionGuidance_RequiresPlanningPhaseForPlanningDeliverables()
    {
        CoordinatorOrchestratorExecutor.PlanningPhaseGuidance.Should().Contain("MUST");
        CoordinatorOrchestratorExecutor.PlanningPhaseGuidance.Should().Contain("PRD");
        CoordinatorOrchestratorExecutor.PlanningPhaseGuidance.Should().Contain("launch-marketing");
        CoordinatorOrchestratorExecutor.PlanningPhaseGuidance.Should().Contain("\"planning\"");
    }

    [Fact]
    public void DecompositionGuidance_RequiresStructuredOutputPaths()
    {
        CoordinatorOrchestratorExecutor.DeclaredOutputPathsGuidance
            .Should().Contain("\"declared_output_paths\"");
        CoordinatorOrchestratorExecutor.DeclaredOutputPathsGuidance
            .Should().Contain("outputs only");
        CoordinatorOrchestratorExecutor.DeclaredOutputPathsGuidance
            .Should().ContainEquivalentOf("referenced");
    }

    [Fact]
    public void LlmDecomposedPrd_WithOmittedPhase_IsClassifiedAsPlanning_AndStructuredOutputIsCanonicalized()
    {
        var phase = CoordinatorOrchestratorExecutor.InferSubtaskPhase(
            "Write the PRD",
            "Create PRD.md with the agreed requirements.",
            "none");
        var outputs = CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
            ["PRD.md"],
            phase);

        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(
            CreateSubtask(phase, declaredOutputPaths: outputs));

        phase.Should().Be("planning");
        outputs.Should().Equal("docs/planning/PRD.md");
        task.Should().Contain("## Planning deliverable location");
        task.Should().Contain("docs/planning/PRD.md");
    }

    [Theory]
    [InlineData("Produce a product requirements document", "Create PRD.md.")]
    [InlineData("Research the target market", "Write market-research.md.")]
    [InlineData("Create launch marketing plan", "Write launch-marketing.md.")]
    public void FallbackPlanningDeliverable_IsClassifiedAsPlanning_AndDispatchReceivesLocationInstruction(
        string desiredOutcome,
        string scopeText)
    {
        var fallbackScope =
            $"Deliver the confirmed outcome in a single pass. Desired outcome: {desiredOutcome} Scope: {scopeText}";
        var phase = CoordinatorOrchestratorExecutor.InferSubtaskPhase(
            "Deliver the confirmed outcome",
            fallbackScope,
            "none");

        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(
            CreateSubtask(phase, fallbackScope));

        phase.Should().Be("planning");
        task.Should().Contain("## Planning deliverable location");
        task.Should().Contain("docs/planning/");
    }

    [Fact]
    public void PlanningScopes_CanonicalizeBareAndFullPathToSamePersistedOutputConflict()
    {
        var bare = CreateSubtask(
            "planning",
            "Write PRD.md.",
            declaredOutputPaths: CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
                ["PRD.md"],
                "planning"),
            id: 10);
        var full = CreateSubtask(
            "planning",
            "Revise docs/planning/PRD.md.",
            declaredOutputPaths: ["docs/planning/PRD.md"],
            id: 11);

        var collisions = CoordinatorDispatchService.FindDeclaredOutputConflictEdges(
            [bare, full],
            new HashSet<(int, int)>());

        CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(bare.DeclaredOutputPathsJson)
            .Should().Equal("docs/planning/PRD.md");
        collisions.Should().ContainSingle().Which.Should().Be((11, 10));
    }

    [Fact]
    public void ExecutionSubtask_ConsumingPrd_RemainsExecution()
    {
        var phase = CoordinatorOrchestratorExecutor.InferSubtaskPhase(
            "Implement the API",
            "Implement the API described in PRD.md.",
            "execution");

        phase.Should().Be("execution");
    }

    [Fact]
    public void ExecutionSubtask_UpdatingPrdParser_TrustsStructuralPhaseAndPreservesOutputPath()
    {
        var phase = CoordinatorOrchestratorExecutor.InferSubtaskPhase(
            "Update the PRD parser",
            "Update parser.md as part of the parser implementation.",
            "execution");
        var outputs = CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
            ["parser.md"],
            phase);

        phase.Should().Be("execution");
        outputs.Should().Equal("parser.md");
    }

    [Fact]
    public void DeterministicFallback_InfersStructuredProducedFileWithoutIncludingReferences()
    {
        var outputs = CoordinatorOrchestratorExecutor.InferDeterministicDeclaredOutputPaths(
            "Write launch-marketing.md and refer to README.md.",
            "planning");

        outputs.Should().Equal("docs/planning/launch-marketing.md");
    }

    [Fact]
    public void DoSubtasksConflict_EmptyStructuredOutputsConflictWithDeclaredWriter()
    {
        var undeclared = CreateSubtask(
            "execution",
            "Scope prose mentions unrelated.cs but declares no outputs.",
            declaredOutputPaths: []);
        var declared = CreateSubtask(
            "execution",
            "Update declared.cs.",
            declaredOutputPaths: ["declared.cs"]);

        CoordinatorAssemblyService.DoSubtasksConflict(undeclared, declared).Should().BeTrue();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[\"\", \"  \"]")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    public void DoSubtasksConflict_InvalidStructuredOutputsBehaveLikeEmptyList(string json)
    {
        var invalid = CreateSubtask("execution");
        invalid.DeclaredOutputPathsJson = json;
        var declared = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/real.md"]);

        CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(json).Should().BeEmpty();
        CoordinatorAssemblyService.DoSubtasksConflict(invalid, declared).Should().BeTrue();
    }

    [Fact]
    public void DoSubtasksConflict_MixedNullAndValidStructuredOutputsUsesValidEntry()
    {
        var mixed = CreateSubtask("execution");
        mixed.DeclaredOutputPathsJson = "[null, \"docs/real.md\"]";
        var matching = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/real.md"]);
        var unrelated = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/unrelated.md"]);

        CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(mixed.DeclaredOutputPathsJson)
            .Should().Equal("docs/real.md");
        CoordinatorAssemblyService.DoSubtasksConflict(mixed, matching).Should().BeTrue();
        CoordinatorAssemblyService.DoSubtasksConflict(mixed, unrelated).Should().BeFalse();
    }

    [Fact]
    public void PlanningDeclaredOutput_CanonicalizesGenericBareMarkdownFilename()
    {
        var outputs = CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
            ["findings.md"],
            "planning");

        outputs.Should().Equal("docs/planning/findings.md");
    }

    [Fact]
    public void PlanningTask_WriteAndRefer_CanonicalizesOnlyStructuredOutput()
    {
        const string original = "Write launch-marketing.md and refer to README.md.";

        var outputs = CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
            ["launch-marketing.md"],
            "planning");
        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(
            CreateSubtask("planning", original, declaredOutputPaths: outputs));

        outputs.Should().Equal("docs/planning/launch-marketing.md");
        task.Should().Contain(original);
        task.Should().NotContain("docs/planning/README.md");
        task.Should().Contain("- `docs/planning/launch-marketing.md`");
        task.Should().ContainEquivalentOf("input/reference");
    }

    [Theory]
    [InlineData("Deliverable: launch-marketing.md", "launch-marketing.md")]
    [InlineData("Output: PRD.md", "PRD.md")]
    public void PlanningTask_LabelOnlyOutput_IsCanonicalizedFromStructuredMetadata(
        string scope,
        string declaredOutput)
    {
        var outputs = CoordinatorOrchestratorExecutor.CanonicalizeDeclaredOutputPaths(
            [declaredOutput],
            "planning");
        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(
            CreateSubtask("planning", scope, declaredOutputPaths: outputs));

        scope.Should().NotContain("docs/planning/");
        outputs.Should().Equal($"docs/planning/{declaredOutput}");
        task.Should().Contain($"- `docs/planning/{declaredOutput}`");
    }

    [Fact]
    public void PlanningSubtask_DirectsProseDeliverablesToPlanningFolder()
    {
        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(CreateSubtask("planning"));

        task.Should().Contain("docs/planning/");
        task.Should().ContainEquivalentOf("repository root");
        task.Should().Contain("PRD.md", "the requested filename remains part of the task");
        task.Should().ContainEquivalentOf("authoritative");
    }

    [Theory]
    [InlineData("execution")]
    [InlineData("validation")]
    [InlineData("none")]
    public void NonPlanningSubtask_DoesNotAddPlanningDeliverableInstruction(string phase)
    {
        var task = CoordinatorDispatchService.BuildCanonicalSubtaskTask(CreateSubtask(phase));

        task.Should().NotContain("docs/planning/");
    }

    private static Subtask CreateSubtask(
        string phase,
        string scope = "Create PRD.md with the agreed requirements.",
        IReadOnlyList<string>? declaredOutputPaths = null,
        int id = 0) =>
        new()
        {
            Id = id,
            Title = "Write the product requirements",
            Scope = scope,
            AssignedAgent = "Product",
            SelectedModelId = "test-model",
            Phase = phase,
            IsolationStrategy = "worktree",
            DeclaredOutputPathsJson = CoordinatorOrchestratorExecutor.SerializeDeclaredOutputPaths(
                declaredOutputPaths),
            Status = SubtaskStatus.Pending,
        };
}
