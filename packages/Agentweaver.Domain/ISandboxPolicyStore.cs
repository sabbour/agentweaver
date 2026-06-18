namespace Agentweaver.Domain;

/// <summary>
/// Persists and retrieves per-project sandbox policies.
/// </summary>
public interface ISandboxPolicyStore
{
    /// <summary>
    /// Returns the policy for the given repository path, or the default policy if none exists.
    /// </summary>
    Task<SandboxPolicy> GetPolicyAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>Persists (insert or replace) a sandbox policy for the given repository path.</summary>
    Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default);
}
