using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.AgentHost;

internal static class AgentHostRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentHostRuntime(this IServiceCollection services)
    {
        services.AddHttpClient("agentweaver-api");
        services.AddSingleton<AgentHostRuntimeState>();
        services.AddSingleton<IToolApprovalOwnerResolver, AgentHostToolApprovalOwnerResolver>();
        services.AddAgentRuntime();
        services.AddSingleton<IAgentHostToolApprovalPolicyClient, AgentHostToolApprovalPolicyClient>();
        services.AddSingleton<AgentHostDurableToolApprovalGate>();
        services.AddSingleton<IToolApprovalGate>(sp =>
            sp.GetRequiredService<AgentHostDurableToolApprovalGate>());
        return services;
    }
}

internal sealed class AgentHostToolApprovalOwnerResolver(
    AgentHostRuntimeState runtimeState) : IToolApprovalOwnerResolver
{
    public string? GetCanonicalOwner(string runId)
    {
        if (!runtimeState.IsConfigured ||
            string.IsNullOrWhiteSpace(runId) ||
            !string.Equals(runId, runtimeState.RunId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(runtimeState.UserId))
        {
            return null;
        }

        return runtimeState.UserId;
    }
}
