namespace Scaffolder.Domain;

public enum GitHubTokenStatus { SignedIn, SignedOut, NeverSignedIn }

public sealed record GitHubTokenEntry(GitHubTokenStatus Status, string? AccessToken);
public sealed record GitHubToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string Login, string? AvatarUrl, string[] Scopes);
public sealed record GitHubIdentity(string Login, string? AvatarUrl);

public sealed record GitHubTokenScope
{
    public string Key { get; }
    private GitHubTokenScope(string key) => Key = key;

    public static GitHubTokenScope Installation { get; } = new("installation");
    public static GitHubTokenScope ForUser(string userId) => new($"user:{userId}");

    public override string ToString() => Key;
}

public interface IGitHubTokenStore
{
    Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default);

    /// <summary>
    /// Returns the full persisted token for the scope (including RefreshToken and ExpiresAt),
    /// or null when there is no signed-in token (signed-out or never-signed-in).
    /// Unlike <see cref="GetAsync"/> this exposes the refresh material needed for token rotation.
    /// </summary>
    Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default);

    Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default);
    Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default);
    Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default);
}

/// <summary>
/// Resolves a valid (non-expired) GitHub access token for a scope, transparently refreshing
/// an expired/near-expiry token using the stored refresh token. Returns null when no token is
/// available or when re-authentication is required (refresh failed / token revoked).
/// </summary>
public interface IGitHubAccessTokenProvider
{
    Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default);
}
