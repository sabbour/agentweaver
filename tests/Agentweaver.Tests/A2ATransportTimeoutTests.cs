using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Agentweaver.AgentRuntime.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Api;

[Trait("Category", "ProcessEnvironment")]
public sealed class A2ATransportTimeoutTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public A2ATransportTimeoutTests(ReviewWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void StreamingA2AHttpClient_HasNoCompetingTransportTimeout()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient(RemoteAgentProxy.StreamingHttpClientName);

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan,
            "the worker total/read-idle deadlines own cancellation for streaming A2A turns");
    }

    [Fact]
    public void NonStreamingAgentHostHttpClient_RetainsFiniteTimeout()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient("a2a-sandbox-pod");

        client.Timeout.Should().Be(TimeSpan.FromMinutes(2));
        RemoteAgentProxy.StreamingHttpClientName.Should().NotBe("a2a-sandbox-pod");
    }
}
