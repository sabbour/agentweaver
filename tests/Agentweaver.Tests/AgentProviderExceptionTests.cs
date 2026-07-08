using FluentAssertions;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.Tests;

public sealed class AgentProviderExceptionTests
{
    [Fact]
    public void Classify_CopilotFailedToListModels_ReturnsMachineReadableProviderUnavailable()
    {
        var ex = new InvalidOperationException(
            "Session error: Execution failed: Error: Failed to list models");

        var classified = AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, "run-123");

        classified.Should().NotBeNull();
        classified!.FailureKind.Should().Be(AgentProviderFailureKind.ProviderUnavailable);
        classified.ErrorCode.Should().Be("github_copilot_models_unavailable");
        classified.UserMessage.Should().Contain("could not list available models");
        classified.UserMessage.Should().Contain("run-123");
    }

    [Theory]
    [InlineData("unsupported model: gpt-never")]
    [InlineData("model is not available for this account")]
    [InlineData("invalid model 'gpt-never'")]
    public void Classify_CopilotUnavailableModel_ReturnsConfigurationFailure(string message)
    {
        var classified = AgentProviderException.Classify(
            ModelSource.GitHubCopilot,
            new InvalidOperationException(message),
            "run-456");

        classified.Should().NotBeNull();
        classified!.FailureKind.Should().Be(AgentProviderFailureKind.Configuration);
        classified.ErrorCode.Should().Be("github_copilot_model_unavailable");
        classified.UserMessage.Should().Contain("configured GitHub Copilot model is not available");
        classified.UserMessage.Should().Contain("run-456");
    }
}
