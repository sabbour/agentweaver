using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Safety;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

public static class AgentRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton<MicrosoftFoundryChatClientFactory>();
        services.AddSingleton<GitHubCopilotClientFactory>();
        services.AddSingleton<ContentSafetyChecker>();
        services.AddSingleton(_ => new RunBounds
        {
            MaxSteps = config.GetValue("RunBounds:MaxSteps", 50),
            MaxDuration = TimeSpan.FromMinutes(config.GetValue("RunBounds:MaxMinutes", 10.0)),
        });
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<GitHubCopilotAgentRunner>();
        services.AddSingleton<IAgentRunner, AgentRunnerRouter>();

        return services;
    }
}
