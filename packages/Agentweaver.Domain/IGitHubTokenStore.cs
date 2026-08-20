namespace Agentweaver.Domain;

public enum GitHubTokenStatus { SignedIn, SignedOut, NeverSignedIn }

public sealed record GitHubTokenEntry(GitHubTokenStatus Status, string? AccessToken);
public sealed record GitHubToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string Login, string? AvatarUrl, string[] Scopes);
public sealed record GitHubIdentity(string Login, string? AvatarUrl);
public sealed record GitHubIdentityLink(
    string EntraUserId,
    string GitHubLogin,
    string TokenScopeKey,
    bool IsDefault,
    DateTimeOffset LinkedAt,
    bool? CopilotEntitled,
    DateTimeOffset? CopilotEntitledCheckedAt,
    string? AvatarUrl);

public sealed record GitHubTokenScope
{
    public string Key { get; }
    private GitHubTokenScope(string key) => Key = key;

    public static GitHubTokenScope Installation { get; } = new("installation");

    /// <summary>
    /// Legacy single-account scope keyed directly by the GitHub login. Retained for backward
    /// compatibility with pre-Entra deployments and for lazy migration of existing tokens.
    /// </summary>
    public static GitHubTokenScope ForUser(string userId) => new($"user:{userId}");

    /// <summary>
    /// Multi-account scope for a specific GitHub identity linked to a verified Entra user.
    /// </summary>
    public static GitHubTokenScope ForLinkedIdentity(string entraUserId, string githubLogin) =>
        new($"user-link:{entraUserId}:{githubLogin}");

    /// <summary>
    /// Metadata index describing all GitHub identities linked to a verified Entra user.
    /// </summary>
    public static GitHubTokenScope ForLinkedIdentityIndex(string entraUserId) =>
        new($"user-links:{entraUserId}");

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
/// Optional capability for token stores that can enumerate all known scopes.
/// Implemented by stores that support background token management such as proactive refresh.
/// </summary>
public interface IGitHubTokenScopeEnumerable
{
    Task<IReadOnlyList<GitHubTokenScope>> ListScopesAsync(CancellationToken ct = default);
}

/// <summary>
/// Resolves a valid (non-expired) GitHub access token for a scope, transparently refreshing
/// an expired/near-expiry token using the stored refresh token. Returns null when no token is
/// available or when re-authentication is required (refresh failed / token revoked).
/// </summary>
public interface IGitHubAccessTokenProvider
{
    Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default);

    /// <summary>
    /// Handles an access token that a downstream GitHub consumer explicitly rejected as
    /// unauthorized. Implementations may rotate refreshable credentials, but must return
    /// <see langword="null"/> when re-authentication is required. The default preserves existing
    /// providers that only support expiry-based refresh.
    /// </summary>
    Task<string?> RefreshAfterUnauthorizedAsync(
        GitHubTokenScope scope,
        string? rejectedAccessToken,
        CancellationToken ct = default) =>
        GetValidAccessTokenAsync(scope, ct);
}
