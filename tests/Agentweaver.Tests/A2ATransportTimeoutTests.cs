using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Api;

public sealed class A2ATransportTimeoutTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public A2ATransportTimeoutTests(ReviewWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void StreamingA2AHttpClient_HasNoCompetingTransportTimeout()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        using var client = httpClientFactory.CreateClient("a2a-sandbox-pod");

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan,
            "runtime tool and total-turn watchdogs own cancellation for streaming A2A turns");
    }
}
