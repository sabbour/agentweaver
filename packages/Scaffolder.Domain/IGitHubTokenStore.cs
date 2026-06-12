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
    Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default);
    Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default);
    Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default);
}
