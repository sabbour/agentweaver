using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

public interface IDistributedGitHubTokenRefreshLease : IAsyncDisposable;

public interface IDistributedGitHubTokenRefreshLeaseStore
{
    Task<IDistributedGitHubTokenRefreshLease?> TryAcquireRefreshLeaseAsync(
        GitHubTokenScope scope,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default);
}
