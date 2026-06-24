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
        runner.LastTask.Should().Contain("Agentweaver OPERATING blueprint");
        runner.LastTask.Should().Contain("Agentweaver itself is the operational platform");
        runner.LastTask.Should().Contain("Do NOT interpret the request as a request to design or build a software product");
        runner.LastTask.Should().Contain("handle job searches");
        runner.LastTask.Should().Contain("Agentweaver to operate the job-search process");
        runner.LastTask.Should().Contain("research/sourcing, triage");
        runner.LastTask.Should().Contain("Do not invent");
        runner.LastTask.Should().Contain("introduce any role");
        runner.LastTask.Should().Contain("Every id MUST be one of the catalog ids above");
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
