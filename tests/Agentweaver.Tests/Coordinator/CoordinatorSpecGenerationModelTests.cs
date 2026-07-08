using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorSpecGenerationModelTests
{
    [Fact]
    public void GenerationModelOptions_DefaultsAllGenerationPathsToGpt54()
    {
        var options = new GenerationModelOptions();

        options.ResolveBlueprintModel().Should().Be(GenerationModelOptions.DefaultModel);
        options.ResolveWorkflowModel().Should().Be(GenerationModelOptions.DefaultModel);
        options.ResolveOutcomeSpecModel().Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public void GenerationModelOptions_SharedModelOverridesAllGenerationPaths()
    {
        var options = new GenerationModelOptions { Model = "claude-sonnet-4.6" };

        options.ResolveBlueprintModel().Should().Be("claude-sonnet-4.6");
        options.ResolveWorkflowModel().Should().Be("claude-sonnet-4.6");
        options.ResolveOutcomeSpecModel().Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public void GenerationModelOptions_ProjectSettingsOverrideIndividualGenerationPaths()
    {
        var options = new GenerationModelOptions
        {
            Model = "gpt-5.4-mini",
            BlueprintModel = "claude-sonnet-4.6",
            WorkflowModel = "claude-sonnet-4.6",
            OutcomeSpecModel = "claude-sonnet-4.6",
        };

        options.ResolveBlueprintModel("gpt-5-mini").Should().Be("gpt-5-mini");
        options.ResolveWorkflowModel("gpt-5.3-codex").Should().Be("gpt-5.3-codex");
        options.ResolveOutcomeSpecModel("claude-opus-4.8").Should().Be("claude-opus-4.8");
    }


    [Fact]
    public void CopilotCoordinatorSpecDrafter_UsesGpt54GenerationModelByDefault()
    {
        var drafter = CreateDrafter(new Dictionary<string, string?>
        {
            ["Providers:GitHubCopilot:Model"] = "gpt-4o",
        });

        drafter.OutcomeSpecModel.Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public void CopilotCoordinatorSpecDrafter_UsesConfiguredOutcomeSpecGenerationModel()
    {
        var drafter = CreateDrafter(new Dictionary<string, string?>
        {
            ["Generation:Model"] = "gpt-5.4-mini",
            ["Generation:OutcomeSpecModel"] = "claude-sonnet-4.6",
        });

        drafter.OutcomeSpecModel.Should().Be("claude-sonnet-4.6");
    }

    private static CopilotCoordinatorSpecDrafter CreateDrafter(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new CopilotCoordinatorSpecDrafter(
            copilotClientFactory: null!,
            scopeProvider: null!,
            sandboxExecutor: null!,
            sandboxPolicyStore: null!,
            approvalStore: null!,
            toolApprovalGate: null!,
            streamStore: new RunStreamStore(),
            loggerFactory: NullLoggerFactory.Instance,
            configuration: config);
    }
}
