using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentRuntime.Safety;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Registers the agent runtime services: the provider factories, the provider
/// router, the content-safety checker, the run bounds, and the
/// <see cref="IAgentRunner"/> implementation.
/// </summary>
public static class AgentRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton<GitHubCopilotChatClientFactory>();
        services.AddSingleton<MicrosoftFoundryChatClientFactory>();
        services.AddSingleton<ChatClientFactoryRouter>();
        services.AddSingleton<ContentSafetyChecker>();
        services.AddSingleton(_ => new RunBounds
        {
            MaxSteps = config.GetValue("RunBounds:MaxSteps", 50),
            MaxDuration = TimeSpan.FromMinutes(config.GetValue("RunBounds:MaxMinutes", 10.0)),
        });
        services.AddSingleton<IAgentRunner, AgentRunner>();

        return services;
    }
}
