using Microsoft.Extensions.DependencyInjection;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

public static class AgentRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services)
    {
        services.AddSingleton<GitHubCopilotClientFactory>();
        services.AddSingleton<GitHubCopilotAgentRunner>();
        services.AddSingleton<FoundryClientFactory>();
        services.AddSingleton<FoundryAgentRunner>();
        services.AddSingleton<IAgentRunner, AgentRunnerDispatcher>();
        return services;
    }
}
