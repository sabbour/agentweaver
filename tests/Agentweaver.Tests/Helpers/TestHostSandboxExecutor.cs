using Agentweaver.SandboxExec;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentweaver.Tests.Helpers;

internal static class TestHostSandboxExecutor
{
    internal static void UseTestSandboxExecutor(this IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // API test hosts do not need platform capability detection. The production router
            // probes WSL tools on Windows, which can block indefinitely inside wsl.exe.
            services.RemoveAll<ISandboxExecutor>();
            services.AddSingleton<ISandboxExecutor>(
                SandboxExecutorFactory.CreatePassthrough("test-host: no platform capability probe"));
        });
    }
}
