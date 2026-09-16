using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class ByokProviderConfigMapperTests
{
    [Fact]
    public void AzureProvider_AppendsOpenAiPath_AndMapsDeploymentModel()
    {
        var configuration = new ByokProviderConfiguration(
            "provider-id",
            "Azure provider",
            "azure",
            "https://example.cognitiveservices.azure.com/",
            "gpt-deployment",
            "secret",
            WireApi: "responses",
            AzureApiVersion: "2025-04-01-preview");

        var provider = ByokProviderConfigMapper.ToProviderConfig(configuration);

        provider.BaseUrl.Should().Be("https://example.cognitiveservices.azure.com/openai");
        provider.ModelId.Should().Be("gpt-deployment");
        provider.WireModel.Should().Be("gpt-deployment");
        provider.WireApi.Should().Be("responses");
        provider.Azure.Should().NotBeNull();
        provider.Azure!.ApiVersion.Should().Be("2025-04-01-preview");
    }

    [Fact]
    public void OpenAiProvider_PreservesConfiguredBaseUrl()
    {
        var configuration = new ByokProviderConfiguration(
            "provider-id",
            "OpenAI-compatible provider",
            "openai",
            "https://provider.example.test/v1/",
            "custom-model",
            "secret");

        var provider = ByokProviderConfigMapper.ToProviderConfig(configuration);

        provider.BaseUrl.Should().Be("https://provider.example.test/v1");
        provider.ModelId.Should().Be("custom-model");
        provider.WireModel.Should().Be("custom-model");
    }
}
