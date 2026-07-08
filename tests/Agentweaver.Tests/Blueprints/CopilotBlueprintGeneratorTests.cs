using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Generation;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;

namespace Agentweaver.Tests.Blueprints;

public sealed class CopilotBlueprintGeneratorTests
{
    [Fact]
    public async Task GenerateRawAsync_UsesGpt54GenerationModelByDefault()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
            })
            .Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync("Create a research team", CancellationToken.None);

        runner.LastModelId.Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public async Task GenerateRawAsync_UsesConfiguredBlueprintGenerationModel()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Generation:Model"] = "gpt-5.4-mini",
                ["Generation:BlueprintModel"] = "claude-sonnet-4.6",
            })
            .Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync("Create a research team", CancellationToken.None);

        runner.LastModelId.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task GenerateRawAsync_UsesProjectBlueprintGenerationModelWhenProvided()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Generation:BlueprintModel"] = "claude-sonnet-4.6",
            })
            .Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync(
            "Create a research team",
            CancellationToken.None,
            modelId: "gpt-5-mini");

        runner.LastModelId.Should().Be("gpt-5-mini");
    }


    [Fact]
    public async Task GenerateRawAsync_FramesPromptAsAgentweaverOperatingBlueprint()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder().Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync("I want to create a project to handle job searches", CancellationToken.None);

        runner.LastTask.Should().NotBeNullOrWhiteSpace();
        runner.LastTask.Should().Contain("Agentweaver PROJECT BLUEPRINT");
        runner.LastTask.Should().Contain("The user is using Agentweaver to OPERATE a process");
        runner.LastTask.Should().Contain("Do NOT interpret the description as a request to BUILD SOFTWARE");
        runner.LastTask.Should().Contain("handle job searches");
        runner.LastTask.Should().Contain("OPERATE the travel-planning / job-search process");
        runner.LastTask.Should().Contain("research, writing/drafting");
        runner.LastTask.Should().Contain("Available catalog roles");
        runner.LastTask.Should().Contain("bespoke_roles");
        runner.LastTask.Should().Contain("Available workflows");
    }

    // Issue #176: the library-first matcher under-selected a generic workflow (pm-discovery) for a
    // "triage -> dedupe -> research -> PRD" prompt instead of returning [] so a specialized workflow
    // is generated. The prompt must now instruct the model that output-artifact overlap (both produce
    // a PRD) is NOT process fit and that partial coverage requires returning [].
    [Fact]
    public async Task GenerateRawAsync_WorkflowSelection_RejectsOutputArtifactOverlap_AndPrefersGeneratingOnPartialFit()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder().Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync(
            "GitHub issue triage. Deduplicate open issues, identify customer pain points, do research and validation, then write a PRD.",
            CancellationToken.None);

        runner.LastTask.Should().NotBeNullOrWhiteSpace();
        // Output-artifact overlap must be explicitly disqualified as a basis for matching.
        runner.LastTask.Should().Contain("OUTPUT-ARTIFACT OVERLAP IS NOT PROCESS FIT");
        // The full-coverage test forces partial matches to fall through to generation.
        runner.LastTask.Should().Contain("FULL-COVERAGE TEST");
        // The concrete triage -> dedupe -> research -> PRD example mirroring issue #176.
        runner.LastTask.Should().Contain("triage");
        runner.LastTask.Should().Contain("is NOT Product Management Discovery");
        runner.LastTask.Should().Contain("PREFER [] (generate)");
    }

    [Fact]
    public async Task GenerateRawAsync_WorkflowSelection_IsGateAware_AndPrefersGeneratingSpecializedGatedWorkflows()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder().Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync(
            "Build a web service that triages support tickets and requires sign-off before shipping.",
            CancellationToken.None);

        runner.LastTask.Should().NotBeNullOrWhiteSpace();
        runner.LastTask.Should().Contain("GATE-AWARE WORKFLOW SELECTION");
        runner.LastTask.Should().Contain("`build_test` is the platform-owned Build & Test gate that also lights up preview");
        runner.LastTask.Should().Contain("`rai` is a `check` gate_kind");
        runner.LastTask.Should().Contain("`rubberduck` is a `check` gate_kind");
        runner.LastTask.Should().Contain("`human-review` is a `check` gate_kind");
        runner.LastTask.Should().Contain("MANDATORY BUILD & TEST STEP (software workflows)");
        runner.LastTask.Should().Contain("build_test gate after any RAI safety check and IMMEDIATELY before the human-review gate");
        runner.LastTask.Should().Contain("PREFER [] (generate)");
        runner.LastTask.Should().Contain("generic ungated catalog workflow");
    }

    [Fact]
    public async Task GenerateRawAsync_IncludesStructuralSelfCritiqueChecklist()
    {
        var runner = new CapturingAgentRunner();
        var config = new ConfigurationBuilder().Build();
        var generator = new CopilotBlueprintGenerator(
            runner,
            new CatalogReader(),
            config,
            NullLogger<CopilotBlueprintGenerator>.Instance);

        await generator.GenerateRawAsync(
            "Create a multi-agent content studio for public release notes.",
            CancellationToken.None);

        runner.LastTask.Should().Contain("STRUCTURAL VALIDATION CHECKLIST");
        runner.LastTask.Should().Contain("Role completeness");
        runner.LastTask.Should().Contain("Workflow graph fit");
        runner.LastTask.Should().Contain("Review-policy coherence");
        runner.LastTask.Should().Contain("Sandbox validity");
        runner.LastTask.Should().Contain("missing coordinator/owner role");
        runner.LastTask.Should().Contain("missing review gate for user-facing output");
    }

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public string? LastTask { get; private set; }
        public string? LastModelId { get; private set; }

        public Task<string> ExecuteAsync(
            string task,
            string workingDirectory,
            string repositoryPath,
            ModelSource modelSource,
            string runId,
            string? modelId,
            ChannelWriter<RunEvent>? stream,
            CancellationToken ct,
            string? systemPromptContext = null,
            string? userId = null)
        {
            LastTask = task;
            LastModelId = modelId;
            return Task.FromResult(
                """
                {
                  "id": "blueprint-job-search-operations",
                  "name": "Job Search Operations",
                  "description": "Runs job-search operations in Agentweaver.",
                  "roster": ["customer-researcher", "triage-lead", "writer", "quality-reviewer"],
                  "workflow": "default",
                  "review_policy": "default",
                  "sandbox_profile": "default"
                }
                """);
        }
    }
}
