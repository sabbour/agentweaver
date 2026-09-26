using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Compatibility resolver for hosts that only provide a sandbox policy store.
/// Production API execution replaces this with a run-aware provider.
/// </summary>
internal sealed class SandboxPolicyPermissionBindingProvider(
    ISandboxPolicyStore policyStore) : IEffectivePermissionBindingProvider
{
    public async Task<EffectivePermissionBinding> ResolveAsync(
        string runId,
        string repositoryPath,
        EffectivePermissionBinding? ceiling = null,
        CancellationToken ct = default)
    {
        var policy = await policyStore.GetPolicyAsync(repositoryPath, ct).ConfigureAwait(false);
        var binding = EffectivePermissionBinding.Create(
            runId,
            attempt: ceiling?.Attempt ?? 1,
            source: "sandbox-policy",
            scope: $"repository:{repositoryPath}",
            policy);
        binding.Validate(runId, binding.Attempt);
        return ceiling is null ? binding : EffectivePermissionBinding.Intersect(binding, ceiling);
    }
}
