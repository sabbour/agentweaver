using System;
using System.Threading.Tasks;
using Agentweaver.Mcp;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests.Mcp;

public sealed class McpProgramTests : IDisposable
{
    public McpProgramTests()
    {
        // Clear environment variables before each test
        Environment.SetEnvironmentVariable("AGENTWEAVER_TOKEN", null);
        Environment.SetEnvironmentVariable("AGENTWEAVER_API_KEY", null);
        Environment.SetEnvironmentVariable("AGENTWEAVER_ALLOW_SHARED_KEY", null);
    }

    public void Dispose()
    {
        // Cleanup environment variables after each test
        Environment.SetEnvironmentVariable("AGENTWEAVER_TOKEN", null);
        Environment.SetEnvironmentVariable("AGENTWEAVER_API_KEY", null);
        Environment.SetEnvironmentVariable("AGENTWEAVER_ALLOW_SHARED_KEY", null);
    }

    [Fact]
    public async Task Main_Stdio_WithoutUserToken_WithApiKey_RefusesToStart()
    {
        Environment.SetEnvironmentVariable("AGENTWEAVER_API_KEY", "shared-internal-key");
        
        // When running in stdio mode without a user token but with a shared key,
        // it should refuse to start (return 1) to prevent silent fallback to the shared key (#474)
        var result = await McpProgram.Main(new[] { "--stdio" });
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task Main_Stdio_WithoutUserToken_WithApiKey_AndAllowSharedKey_DoesNotRefuseToStart()
    {
        Environment.SetEnvironmentVariable("AGENTWEAVER_API_KEY", "shared-internal-key");
        Environment.SetEnvironmentVariable("AGENTWEAVER_ALLOW_SHARED_KEY", "true");
        
        // This will attempt to start the WebApplication and block, but we can't let it run forever in tests.
        // Instead of actually starting the app and getting stuck, we know it returns 1 on failure.
        // If we pass an invalid configuration for the host or just let it throw a different exception
        // (like port already in use), we know it got past the credential check.
        // To be safer and prevent hanging the test suite, we can use a timeout.
        
        var task = McpProgram.Main(new[] { "--stdio", "--urls", "http://localhost:0" });
        var completed = await Task.WhenAny(task, Task.Delay(1500));
        
        // If it returns 1, the task will have completed with result 1
        if (completed == task && task.Status == TaskStatus.RanToCompletion)
        {
            (await task).Should().NotBe(1, "Should not fail credential check when AGENTWEAVER_ALLOW_SHARED_KEY is true");
        }
        
        // Test passes if it didn't return 1 (either threw or is still running)
    }

    [Fact]
    public async Task Main_Stdio_WithUserToken_DoesNotRefuseToStart()
    {
        Environment.SetEnvironmentVariable("AGENTWEAVER_API_KEY", "shared-internal-key");
        Environment.SetEnvironmentVariable("AGENTWEAVER_TOKEN", "user-token");
        
        var task = McpProgram.Main(new[] { "--stdio", "--urls", "http://localhost:0" });
        var completed = await Task.WhenAny(task, Task.Delay(1500));
        
        if (completed == task && task.Status == TaskStatus.RanToCompletion)
        {
            (await task).Should().NotBe(1, "Should not fail credential check when user token is provided");
        }
    }
}