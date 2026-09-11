using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorSpecGenerationModelTests
{
    [Fact]
    public void GenerationModelOptions_DefaultsAllGenerationPathsToGpt54()
    {
        var options = new GenerationModelOptions();

        options.ResolveBlueprintModel().Should().Be(GenerationModelOptions.DefaultModel);
        options.ResolveSkillModel().Should().Be(GenerationModelOptions.DefaultModel);
        options.ResolveWorkflowModel().Should().Be(GenerationModelOptions.DefaultModel);
        options.ResolveOutcomeSpecModel().Should().Be(GenerationModelOptions.DefaultModel);
    }

    [Fact]
    public void GenerationModelOptions_SharedModelOverridesAllGenerationPaths()
    {
        var options = new GenerationModelOptions { Model = "claude-sonnet-4.6" };

        options.ResolveBlueprintModel().Should().Be("claude-sonnet-4.6");
        options.ResolveSkillModel().Should().Be("claude-sonnet-4.6");
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

    [Fact]
    public void OutcomeSpecDrafting_UsesAcceptedByokSnapshotAndOutcomeModelOverride()
    {
        var configuration = new ByokProviderConfiguration(
            "accepted", "Accepted", "azure", "https://accepted.example.test",
            "provider-default-model", "not-a-real-key");
        var fingerprint = configuration.ExecutionFingerprint();
        var input = new CoordinatorDraftInput(
            RunId: Guid.NewGuid().ToString(),
            ProjectId: Guid.NewGuid().ToString(),
            Goal: "Draft with BYOK",
            SubmittingUser: "owner",
            RepositoryPath: ".",
            ModelId: null,
            OutcomeSpecGenerationModel: "outcome-model",
            ModelSource: ModelSource.Byok.ToApiString(),
            ByokProviderFingerprint: fingerprint);
        var boundary = new ResolvedRunModelProviderBoundary(
            new EffectiveModelProviderResult.Byok(
                configuration.Id, configuration.Type, fingerprint),
            fingerprint,
            configuration);

        CopilotCoordinatorSpecDrafter.ResolveDraftByokConfiguration(input, boundary)
            .Should().Be(configuration);
        CopilotAIAgent.ResolveSessionModel(
                configuration,
                input.OutcomeSpecGenerationModel,
                preferModelIdOverByokConfiguration: true)
            .Should().Be("outcome-model");
    }

    [Fact]
    public void OutcomeSpecDrafting_RejectsChangedByokSnapshot()
    {
        var accepted = new ByokProviderConfiguration(
            "accepted", "Accepted", "azure", "https://accepted.example.test",
            "provider-default-model", "not-a-real-key");
        var changed = accepted with { Model = "changed-model" };
        var input = new CoordinatorDraftInput(
            RunId: Guid.NewGuid().ToString(),
            ProjectId: Guid.NewGuid().ToString(),
            Goal: "Draft with BYOK",
            SubmittingUser: "owner",
            RepositoryPath: ".",
            ModelId: null,
            ModelSource: ModelSource.Byok.ToApiString(),
            ByokProviderFingerprint: accepted.ExecutionFingerprint());
        var changedFingerprint = changed.ExecutionFingerprint();
        var boundary = new ResolvedRunModelProviderBoundary(
            new EffectiveModelProviderResult.Byok(
                changed.Id, changed.Type, changedFingerprint),
            changedFingerprint,
            changed);

        var action = () => CopilotCoordinatorSpecDrafter.ResolveDraftByokConfiguration(input, boundary);

        action.Should().Throw<AgentProviderException>()
            .Which.ErrorCode.Should().Be("model_provider_changed");
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
