using Scaffolder.Domain;

namespace Scaffolder.Tests.Helpers;

/// <summary>
/// In-memory <see cref="ISandboxPolicyStore"/> for unit tests. Returns a fixed
/// policy (re-keyed to the requested repository path) and ignores writes.
/// </summary>
public sealed class StubPolicyStore : ISandboxPolicyStore
{
    private readonly SandboxPolicy _policy;

    public StubPolicyStore(SandboxPolicy? policy = null)
        => _policy = policy ?? SandboxPolicy.Default("test");

    public Task<SandboxPolicy> GetPolicyAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_policy with { RepositoryPath = path });

    public Task SetPolicyAsync(SandboxPolicy policy, CancellationToken ct = default)
        => Task.CompletedTask;
}
