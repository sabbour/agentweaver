using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Run-bound <see cref="ISandboxPolicyStore"/> for AgentHost. The API resolves the current
/// effective binding and delivers its credential-free policy through the control plane.
/// Missing bindings fail closed instead of restoring the former permissive pod policy.
/// </summary>
internal sealed class PodSandboxPolicyStore(AgentHostRuntimeState runtimeState) : ISandboxPolicyStore
{
    public Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        var binding = runtimeState.EffectivePermissionBinding
            ?? throw new EffectivePermissionBindingException(
                "AgentHost has no effective permission binding for this run.");
        return Task.FromResult(binding.Policy with { RepositoryPath = repositoryPath });
    }

    public Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default) =>
        Task.CompletedTask; // No-op: pod never persists policy changes.
}
