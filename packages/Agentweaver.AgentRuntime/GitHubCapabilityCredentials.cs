namespace Agentweaver.AgentRuntime;

/// <summary>
/// An immutable credential issued from one explicit, run-bound GitHub capability snapshot.
/// Callers must never log or persist <see cref="AccessToken"/>.
/// </summary>
public sealed record GitHubCapabilitySnapshotCredential(
    string SnapshotReference,
    string AccessToken,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Resolves a Copilot credential only from the requesting run's unattended Copilot snapshot.
/// Implementations must return <see langword="null"/> when that snapshot is absent, expired, or revoked.
/// </summary>
public interface IGitHubCopilotCapabilityCredentialProvider
{
    Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        CancellationToken ct = default);

    /// <summary>
    /// Redeems a purpose-bound, project-bound non-run Copilot capability. Implementations must
    /// fail closed for an absent, expired, consumed, wrong-project, or wrong-caller capability.
    /// </summary>
    Task<GitHubCapabilitySnapshotCredential?> GetMarketplaceCredentialAsync(
        string capabilityReference,
        string projectId,
        string entraObjectId,
        CancellationToken ct = default) =>
        Task.FromResult<GitHubCapabilitySnapshotCredential?>(null);
}

/// <summary>
/// Resolves a repository credential only from the requesting run's unattended repository snapshot.
/// Implementations must return <see langword="null"/> when that snapshot is absent, expired, or revoked.
/// </summary>
public interface IGitHubRepositoryCapabilityCredentialProvider
{
    Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        CancellationToken ct = default);
}
