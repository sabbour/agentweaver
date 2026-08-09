namespace Agentweaver.Domain;

/// <summary>
/// Optional extension for token stores that can manage multiple GitHub identities per
/// authenticated platform user (for Entra-backed sign-in and per-project identity selection).
/// The legacy <see cref="IGitHubTokenStore"/> contract remains the single-token primitive used
/// throughout the current API while the multi-identity model is rolled out incrementally.
/// </summary>
public interface IMultiIdentityGitHubTokenStore : IGitHubTokenStore
{
    Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedIdentitiesAsync(
        string entraUserId,
        CancellationToken ct = default);

    Task<GitHubIdentityLink?> GetLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default);

    Task<GitHubIdentityLink?> GetDefaultLinkedIdentityAsync(
        string entraUserId,
        CancellationToken ct = default);

    Task LinkIdentityAsync(
        string entraUserId,
        GitHubToken token,
        bool isDefault = false,
        bool? copilotEntitled = null,
        DateTimeOffset? copilotEntitledCheckedAt = null,
        CancellationToken ct = default);

    Task<bool> SetDefaultLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default);

    Task<bool> UnlinkIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default);
}

/// <summary>
/// Resolves the GitHub token scope that a caller/user id actually resolves to at runtime.
///
/// <para>
/// With Entra sign-in a platform user has no token at the legacy <c>user:{id}</c> scope: their GitHub
/// credentials live under <c>user-link:{entraUserId}:{githubLogin}</c>, one per linked account. Any
/// component that needs the *name* of the effective scope (rather than just reading the token through
/// <see cref="IGitHubTokenStore"/>) must resolve it through this interface so it targets the caller's
/// active (default) linked GitHub account instead of the empty legacy scope.
/// </para>
/// </summary>
public interface IEffectiveGitHubTokenScopeResolver
{
    Task<GitHubTokenScope> ResolveEffectiveScopeAsync(string userId, CancellationToken ct = default);
}
