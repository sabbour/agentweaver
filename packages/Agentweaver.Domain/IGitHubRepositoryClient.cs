namespace Agentweaver.Domain;

/// <summary>
/// The result of an attempted GitHub repository creation. <see cref="Success"/> is false for every
/// known non-fatal failure mode (name collision exhausted its retry budget, insufficient token scope,
/// unknown/inaccessible owner) so callers can report the outcome without crashing (issue: allow
/// creating a GitHub repository for a project that has none connected).
/// </summary>
public sealed record GitHubRepositoryResult(
    bool Success,
    string? FullName = null,
    string? HtmlUrl = null,
    string? CloneUrl = null,
    string? DefaultBranch = null,
    string? ErrorReason = null,
    string? ErrorMessage = null)
{
    public static GitHubRepositoryResult Ok(string fullName, string htmlUrl, string cloneUrl, string defaultBranch) =>
        new(true, fullName, htmlUrl, cloneUrl, defaultBranch);

    public static GitHubRepositoryResult Failed(string reason, string message) =>
        new(false, ErrorReason: reason, ErrorMessage: message);
}

/// <summary>
/// A candidate owner (the authenticated user themself, or an org they belong to) that a new GitHub
/// repository can be created under. Returned by <see cref="IGitHubRepositoryClient.ListRepositoryOwnersAsync"/>
/// so the caller can present a user-driven choice rather than the platform auto-picking an owner.
/// </summary>
public sealed record GitHubRepositoryOwner(string Login, bool IsUser);

/// <summary>
/// Creates GitHub repositories via the REST API, authenticated with a caller-supplied access token
/// (reused from the existing GitHub token store / scope plumbing — see
/// <see cref="IGitHubAccessTokenProvider"/> and <see cref="IGitHubTokenScopeProvider"/>). Implemented
/// against the same "github" named <c>HttpClient</c> the pull-request / issue-sync features use.
/// </summary>
public interface IGitHubRepositoryClient
{
    /// <summary>
    /// Lists the accounts the caller's token can create repositories under: the authenticated user
    /// themself (always first) followed by the orgs the token can see. The owner is never auto-picked —
    /// this list exists so the caller can be asked to choose one.
    /// </summary>
    Task<IReadOnlyList<GitHubRepositoryOwner>> ListRepositoryOwnersAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Creates a new repository named <paramref name="name"/> under <paramref name="owner"/>. On a 422
    /// "name already exists" response, retries with a short numeric suffix (e.g. "-2", "-3") up to a
    /// small attempt cap before giving up with a clear error.
    /// </summary>
    Task<GitHubRepositoryResult> CreateRepositoryAsync(
        string owner,
        string name,
        bool isPrivate,
        string accessToken,
        CancellationToken ct = default);
}
