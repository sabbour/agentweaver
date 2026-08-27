using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Internal broker boundary for future run-bound capability issuance. It accepts only a purpose
/// and opaque snapshot reference, never a user, project, repository, grant, or ambient scope.
/// </summary>
internal sealed class GitHubCapabilityBroker(TwoAppPersistenceStore persistence)
{
    public Task<FencedGitHubCapabilitySnapshot?> TryFenceAsync(
        GitHubCapabilityPurpose purpose,
        SnapshotRef snapshotRef,
        DateTimeOffset now,
        CancellationToken ct) =>
        persistence.TryFenceLiveSnapshotAsync(purpose, snapshotRef, now, ct);
}
