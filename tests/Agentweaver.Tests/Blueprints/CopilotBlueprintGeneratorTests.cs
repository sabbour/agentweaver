using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Blueprints;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;

namespace Agentweaver.Tests.Blueprints;

public sealed class CopilotBlueprintGeneratorTests
{
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

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        public string? LastTask { get; private set; }

        public Task<string> ExecuteAsync(
            string task,
            string workingDirectory,
            string repositoryPath,
            ModelSource modelSource,
            string runId,
            string? modelId,
            ChannelWriter<RunEvent>? stream,
            CancellationToken ct,
            string? systemPromptContext = null)
        {
            LastTask = task;
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
