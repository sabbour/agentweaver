using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Sandbox;

public sealed class ApiTestHostSandboxExecutorTests
{
    [Fact]
    public void ApiHost_UsesPassthroughWithoutResolvingPlatformCapabilities()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = factory.CreateClient();

        var executor = factory.Services.GetRequiredService<ISandboxExecutor>();

        executor.Should().BeOfType<PassthroughExecutor>();
        executor.IsRealIsolation.Should().BeFalse();
        executor.SelectionReason.Should().Be("test-host: no platform capability probe");
    }
}
