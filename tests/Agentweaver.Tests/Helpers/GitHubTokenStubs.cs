using Agentweaver.Domain;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Stub IGitHubTokenStore that always returns SignedOut. Used for tests that do not exercise
/// GitHub authentication paths.
/// </summary>
public sealed class NullGitHubTokenStore : IGitHubTokenStore
{
    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubToken?>(null);

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubIdentity?>(null);

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Stub IGitHubTokenScopeProvider that always returns the installation scope.
/// </summary>
public sealed class FixedInstallationScopeStub : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;
}

public sealed class FixedGitHubCopilotCapabilityCredentialProvider(
    string accessToken = "test-capability-credential",
    DateTimeOffset? expiresAt = null) : IGitHubCopilotCapabilityCredentialProvider
{
    public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        CancellationToken ct = default) =>
        Task.FromResult<GitHubCapabilitySnapshotCredential?>(
            string.IsNullOrWhiteSpace(runId)
                ? null
                : new("snapshot-test", accessToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)));
}

public sealed class FixedGitHubRepositoryCapabilityCredentialProvider(
    string accessToken = "test-repository-capability-credential",
    DateTimeOffset? expiresAt = null) : IGitHubRepositoryCapabilityCredentialProvider
{
    public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
        string runId,
        CancellationToken ct = default) =>
        Task.FromResult<GitHubCapabilitySnapshotCredential?>(
            string.IsNullOrWhiteSpace(runId)
                ? null
                : new("snapshot-test", accessToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)));
}
