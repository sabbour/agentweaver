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
    public void DoSubtasksConflict_ValidEmptyStructuredOutputs_DoesNotConflictWithDeclaredWriter()
    {
        // #261: a literal `[]` is a genuine declaration that this subtask writes no files (e.g. a
        // read-only investigation subtask) — it must NOT be treated the same as untrustworthy
        // (Invalid) metadata, so it should not conflict with a sibling that declares real outputs.
        var readOnly = CreateSubtask(
            "execution",
            "Scope prose mentions unrelated.cs but declares no outputs.",
            declaredOutputPaths: []);
        var declared = CreateSubtask(
            "execution",
            "Update declared.cs.",
            declaredOutputPaths: ["declared.cs"]);

        CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(readOnly.DeclaredOutputPathsJson).State
            .Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty);
        CoordinatorAssemblyService.DoSubtasksConflict(readOnly, declared).Should().BeFalse();
    }

    [Fact]
    public void DoSubtasksConflict_ValidEmptyStructuredOutputs_DoesNotConflictWithAnotherEmpty()
    {
        var readOnly1 = CreateSubtask("execution", declaredOutputPaths: []);
        var readOnly2 = CreateSubtask("execution", declaredOutputPaths: []);

        CoordinatorAssemblyService.DoSubtasksConflict(readOnly1, readOnly2).Should().BeFalse();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[\"\", \"  \"]")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    public void DoSubtasksConflict_InvalidStructuredOutputsFailClosed(string json)
    {
        var invalid = CreateSubtask("execution");
        invalid.DeclaredOutputPathsJson = json;
        var declared = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/real.md"]);

        CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(json).Should().BeEmpty();
        CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(json).State
            .Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid);
        CoordinatorAssemblyService.DoSubtasksConflict(invalid, declared).Should().BeTrue();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[\"\", \"  \"]")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    public void DoSubtasksConflict_InvalidStructuredOutputs_StillConflictsWithGenuinelyEmptyOutputs(string json)
    {
        // Invalid metadata is untrustworthy and must fail closed even against a subtask that
        // genuinely declared no outputs — Invalid always wins over ValidEmpty (#261).
        var invalid = CreateSubtask("execution");
        invalid.DeclaredOutputPathsJson = json;
        var readOnly = CreateSubtask("execution", declaredOutputPaths: []);

        CoordinatorAssemblyService.DoSubtasksConflict(invalid, readOnly).Should().BeTrue();
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

    // =========================================================================
    // #261: ParseDeclaredOutputPaths tri-state coverage
    // =========================================================================

    [Fact]
    public void ParseDeclaredOutputPaths_LiteralEmptyArray_IsValidEmpty()
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths("[]");

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidEmpty);
        result.Paths.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[\"\", \"  \"]")]
    [InlineData("[null]")]
    [InlineData("[123]")]
    [InlineData("[\"/\"]")]
    [InlineData("[\"///\"]")]
    [InlineData("[\"\\\\\"]")]
    [InlineData("[\"/docs/a.md\"]")]
    [InlineData("[\".\"]")]
    [InlineData("[\"./\"]")]
    [InlineData("[\"/.\"]")]
    [InlineData("[\"..\"]")]
    [InlineData("[\"src/..\"]")]
    [InlineData("[\"../outside.txt\"]")]
    [InlineData("[\"a/b/../../../outside.txt\"]")]
    [InlineData("[\"foo/../../bar\"]")]
    [InlineData("[\"C:\\\\outside.txt\"]")]
    [InlineData("[\"C:/outside.txt\"]")]
    public void ParseDeclaredOutputPaths_MalformedOrAllInvalidEntries_IsInvalid(string? json)
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(json);

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid);
        result.Paths.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[\"/\"]")]
    [InlineData("[\"///\"]")]
    [InlineData("[\"\\\\\"]")]
    [InlineData("[\"/docs/a.md\"]")]
    [InlineData("[\".\"]")]
    [InlineData("[\"./\"]")]
    [InlineData("[\"/.\"]")]
    [InlineData("[\"..\"]")]
    [InlineData("[\"src/..\"]")]
    [InlineData("[\"../outside.txt\"]")]
    [InlineData("[\"a/b/../../../outside.txt\"]")]
    [InlineData("[\"foo/../../bar\"]")]
    [InlineData("[\"C:\\\\outside.txt\"]")]
    [InlineData("[\"C:/outside.txt\"]")]
    public void DoSubtasksConflict_NormalizationToNoConcretePaths_FailsClosed(string json)
    {
        var invalid = CreateSubtask("execution");
        invalid.DeclaredOutputPathsJson = json;
        var declared = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/real.md"]);

        CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths(json).Should().BeEmpty();
        CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(json).State
            .Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid);
        CoordinatorAssemblyService.DoSubtasksConflict(invalid, declared).Should().BeTrue();
    }

    [Fact]
    public void ParseDeclaredOutputPaths_ValidNonEmptyArray_IsValidWithPaths()
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths("[\"docs/a.md\", \"src/b.cs\"]");

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidWithPaths);
        result.Paths.Should().Equal("docs/a.md", "src/b.cs");
    }

    [Fact]
    public void ParseDeclaredOutputPaths_MixedNullAndValidEntry_IsValidWithPaths_NotInvalid()
    {
        // At least one entry survived filtering, so this must NOT be treated the same as fully
        // malformed metadata (#261 preserves rev8's partial-recovery behavior for mixed arrays).
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths("[null, \"docs/real.md\"]");

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidWithPaths);
        result.Paths.Should().Equal("docs/real.md");
    }

    // =========================================================================
    // #261: unified path normalization at the parsing boundary
    // =========================================================================

    [Theory]
    [InlineData("[\"src\\\\foo.cs\"]", "src/foo.cs")] // backslash -> forward slash
    [InlineData("[\" docs/a.md \"]", "docs/a.md")]     // surrounding whitespace trimmed
    [InlineData("[\"src/./foo.txt\"]", "src/foo.txt")] // dot segments normalized without rejecting nested paths
    [InlineData("[\"src/../foo.txt\"]", "foo.txt")]     // parent segment collapses to a real repo-relative file
    [InlineData("[\"docs/a.md/\"]", "docs/a.md/")]      // trailing slash is left as-is (not a path segment concern here)
    [InlineData("[\"foo..bar\"]", "foo..bar")]          // literal dot characters are preserved
    [InlineData("[\"...\"]", "...")]                    // literal triple-dot name is preserved
    public void ParseDeclaredOutputPaths_NormalizesSurvivingPaths(string json, string expected)
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(json);

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidWithPaths);
        result.Paths.Should().Equal(expected);
    }

    [Fact]
    public void ParseDeclaredOutputPaths_DeduplicatesCaseInsensitivelyAfterNormalization()
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(
            "[\"docs\\\\A.MD\", \" docs/a.md \", \"DOCS/a.md\"]");

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.ValidWithPaths);
        result.Paths.Should().ContainSingle();
    }

    [Theory]
    [InlineData("[\"docs/real.md\", \"../outside.txt\"]")]
    [InlineData("[\"a.txt\", \"C:/outside.txt\"]")]
    [InlineData("[\"a.txt\", \"\\\\\\\\server\\\\share\\\\x\"]")]
    [InlineData("[\".\", \"src/./foo.txt\", \"./\"]")]
    public void ParseDeclaredOutputPaths_MixedValidAndRejectedEntry_IsInvalid(string json)
    {
        var result = CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(json);

        result.State.Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid);
        result.Paths.Should().BeEmpty();
    }

    [Fact]
    public void DoSubtasksConflict_BackslashAndWhitespaceVariantsStillMatchAfterNormalization()
    {
        var subtask1 = CreateSubtask("execution");
        subtask1.DeclaredOutputPathsJson = "[\"src\\\\foo.cs\"]";
        var subtask2 = CreateSubtask("execution");
        subtask2.DeclaredOutputPathsJson = "[\" src/foo.cs \"]";

        CoordinatorAssemblyService.DoSubtasksConflict(subtask1, subtask2).Should().BeTrue();
    }

    [Theory]
    [InlineData("[\"docs/real.md\", \"../outside.txt\"]")]
    [InlineData("[\"a.txt\", \"C:/outside.txt\"]")]
    [InlineData("[\"a.txt\", \"\\\\\\\\server\\\\share\\\\x\"]")]
    [InlineData("[\".\", \"src/./foo.txt\"]")]
    public void DoSubtasksConflict_MixedValidAndRejectedEntry_FailsClosed(string json)
    {
        var mixed = CreateSubtask("execution");
        mixed.DeclaredOutputPathsJson = json;
        var matching = CreateSubtask(
            "execution",
            declaredOutputPaths: ["docs/real.md"]);

        CoordinatorOrchestratorExecutor.ParseDeclaredOutputPaths(mixed.DeclaredOutputPathsJson).State
            .Should().Be(CoordinatorOrchestratorExecutor.DeclaredOutputPathsParseState.Invalid);
        CoordinatorAssemblyService.DoSubtasksConflict(mixed, matching).Should().BeTrue();
    }

    // =========================================================================
    // #261: shared matcher unification between runtime conflict detection and the persisted
    // dependency-edge builder (suffix/bare-filename matches now produce a deterministic edge too).
    // =========================================================================

    [Fact]
    public void FindDeclaredOutputConflictEdges_SuffixMatchingPaths_ProduceDeterministicEdge()
    {
        var bare = CreateSubtask("execution", declaredOutputPaths: ["foo.cs"], id: 20);
        var nested = CreateSubtask("execution", declaredOutputPaths: ["src/foo.cs"], id: 21);

        var edges = CoordinatorDispatchService.FindDeclaredOutputConflictEdges(
            [bare, nested],
            new HashSet<(int, int)>());

        edges.Should().ContainSingle().Which.Should().Be((21, 20));
        // The same pair must also be flagged as conflicting at runtime — this is the exact scenario
        // #261 called out as inconsistent (edge builder used exact match only; runtime used
        // suffix/filename matching too).
        CoordinatorAssemblyService.DoSubtasksConflict(bare, nested).Should().BeTrue();
    }

    [Fact]
    public void FindDeclaredOutputConflictEdges_InvalidStructuredOutputs_ContributeNoEdges()
    {
        var invalid = CreateSubtask("execution", id: 30);
        invalid.DeclaredOutputPathsJson = "[null]";
        var declared = CreateSubtask("execution", declaredOutputPaths: ["docs/real.md"], id: 31);

        var edges = CoordinatorDispatchService.FindDeclaredOutputConflictEdges(
            [invalid, declared],
            new HashSet<(int, int)>());

        edges.Should().BeEmpty();
    }

    [Fact]
    public void FindDeclaredOutputConflictEdges_ValidEmptyStructuredOutputs_ContributeNoEdges()
    {
        var readOnly = CreateSubtask("execution", declaredOutputPaths: [], id: 40);
        var declared = CreateSubtask("execution", declaredOutputPaths: ["docs/real.md"], id: 41);

        var edges = CoordinatorDispatchService.FindDeclaredOutputConflictEdges(
            [readOnly, declared],
            new HashSet<(int, int)>());

        edges.Should().BeEmpty();
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
