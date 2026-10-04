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
        services.AddSingleton<IModelInvocationGuard, AgentHostModelInvocationGuard>();
        services.AddSingleton<IByokProviderConfigurationProvider, AgentHostByokProviderConfigurationProvider>();
        services.AddSingleton<IEffectivePermissionBindingProvider, AgentHostPermissionBindingProvider>();
        services.AddSingleton<IToolApprovalOwnerResolver, AgentHostToolApprovalOwnerResolver>();
        services.AddAgentRuntime();
        services.AddSingleton<IAgentHostToolApprovalPolicyClient, AgentHostToolApprovalPolicyClient>();
        services.AddSingleton<AgentHostDurableToolApprovalGate>();
        services.AddSingleton<IToolApprovalGate>(sp =>
            sp.GetRequiredService<AgentHostDurableToolApprovalGate>());
        return services;
    }

    internal sealed class AgentHostByokProviderConfigurationProvider(AgentHostRuntimeState runtimeState)
        : IByokProviderConfigurationProvider
    {
        public Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct) =>
            Task.FromResult(runtimeState.ByokProviderConfiguration);
    }

    internal sealed class AgentHostPermissionBindingProvider(AgentHostRuntimeState runtimeState)
        : IEffectivePermissionBindingProvider
    {
        public Task<EffectivePermissionBinding> ResolveAsync(
            string runId,
            string repositoryPath,
            EffectivePermissionBinding? ceiling = null,
            CancellationToken ct = default)
        {
            var binding = runtimeState.EffectivePermissionBinding
                ?? throw new EffectivePermissionBindingException(
                    "AgentHost effective permission binding is unavailable.");
            binding.Validate(runId, binding.Attempt);
            return Task.FromResult(
                ceiling is null
                    ? binding
                    : EffectivePermissionBinding.Intersect(binding, ceiling));
        }
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
