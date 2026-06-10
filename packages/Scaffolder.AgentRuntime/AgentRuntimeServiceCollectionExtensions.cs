using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;

namespace Scaffolder.AgentRuntime;

public static class AgentRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        // Sandbox options
        services.Configure<SandboxOptions>(opts =>
            configuration.GetSection(SandboxOptions.Section).Bind(opts));

        // Sandbox executor (singleton — platform probe runs once at startup)
        services.AddSingleton<ISandboxExecutor>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(nameof(SandboxExecutorFactory));
            return SandboxExecutorFactory.Create(logger);
        });

        services.AddSingleton<GitHubCopilotClientFactory>();
        services.AddSingleton<GitHubCopilotAgentRunner>();
        services.AddSingleton<FoundryClientFactory>();
        services.AddSingleton<FoundryAgentRunner>();
        services.AddSingleton<IAgentRunner, AgentRunnerDispatcher>();
        return services;
    }
}

