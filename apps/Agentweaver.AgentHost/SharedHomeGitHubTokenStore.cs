using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Read-only <see cref="IGitHubTokenStore"/> that serves tokens from the shared, RWX file-based
/// store written by the API/worker tier (<c>FileSystemGitHubTokenStore</c>). See
/// <see cref="SharedTokenStorePaths"/> for the path/format contract.
///
/// <para>
/// spec-018 P1.5: this is how the agent-host pod obtains the run's GitHub Copilot token without any
/// secret injection — it reads the same <c>user_&lt;id&gt;.json</c> the API persisted on the shared
/// <c>agentweaver-workspace</c> volume. Mutating operations (<see cref="SetAsync"/>,
/// <see cref="SignOutAsync"/>) are intentionally no-ops: the pod must never clobber the user's
/// shared credentials.
/// </para>
/// </summary>
internal sealed class SharedHomeGitHubTokenStore : IGitHubTokenStore
{
    private readonly string _authDir;

    public SharedHomeGitHubTokenStore(string authDir) => _authDir = authDir;

    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = Read(scope);
        if (stored is null)
            return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));
        if (stored.Status == "signed-out")
            return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null));
        if (!string.IsNullOrEmpty(stored.AccessToken))
            return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedIn, stored.AccessToken));
        return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));
    }

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = Read(scope);
        if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
            return Task.FromResult<GitHubToken?>(new GitHubToken(
                stored.AccessToken,
                stored.RefreshToken,
                stored.ExpiresAt,
                stored.Login ?? "unknown",
                stored.AvatarUrl,
                stored.Scopes ?? []));
        return Task.FromResult<GitHubToken?>(null);
    }

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var stored = Read(scope);
        if (stored?.Login is not null)
            return Task.FromResult<GitHubIdentity?>(new GitHubIdentity(stored.Login, stored.AvatarUrl));
        return Task.FromResult<GitHubIdentity?>(null);
    }

    // Pod is a read-only consumer of the shared store — never mutate the user's credentials.
    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
        => Task.CompletedTask;

    private StoredCredential? Read(GitHubTokenScope scope)
    {
        var path = SharedTokenStorePaths.FilePath(_authDir, scope);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null; // malformed — treat as absent
        }
    }

    // Mirrors the on-disk shape written by FileSystemGitHubTokenStore (PascalCase, default policy).
    internal sealed record StoredCredential
    {
        public string? Status { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? Login { get; init; }
        public string? AvatarUrl { get; init; }
        public string[]? Scopes { get; init; }
    }
}
