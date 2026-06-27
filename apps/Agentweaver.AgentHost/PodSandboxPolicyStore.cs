using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Passthrough <see cref="ISandboxPolicyStore"/> for the in-pod AgentHost.
/// The pod never accesses the database; enforcement relies on the Kata VM
/// boundary and the NetworkPolicy egress allowlist applied by the platform team.
/// Returns a permissive policy (shell enabled, network enabled) for every
/// repository path so tool execution is not blocked inside the pod.
/// </summary>
internal sealed class PodSandboxPolicyStore : ISandboxPolicyStore
{
    public Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default)
    {
        var policy = new SandboxPolicy
        {
            RepositoryPath = repositoryPath,
            ShellEnabled = true,
            NetworkEnabled = true,
            Direct = false,
            AllowedRepositoryRoots = [],
            RequireApprovalForAllShell = false,
        };
        return Task.FromResult(policy);
    }

    public Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default) =>
        Task.CompletedTask; // No-op: pod never persists policy changes.
}
