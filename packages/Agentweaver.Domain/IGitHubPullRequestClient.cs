namespace Agentweaver.Domain;

/// <summary>
/// The result of an attempted GitHub pull request creation. <see cref="Success"/> is false for every
/// known non-fatal failure mode (no commits between head/base, branch not pushed, PR already exists,
/// insufficient token scope, unknown repository) so callers can report the outcome without crashing
/// the run (workflows-automation open-pull-request-action).
/// </summary>
public sealed record GitHubPullRequestResult(
    bool Success,
    int? Number = null,
    string? Url = null,
    string? ErrorReason = null,
    string? ErrorMessage = null)
{
    public static GitHubPullRequestResult Ok(int number, string url) => new(true, number, url);

    public static GitHubPullRequestResult Failed(string reason, string message) =>
        new(false, ErrorReason: reason, ErrorMessage: message);
}

/// <summary>
/// Opens pull requests on a GitHub repository via the REST API, authenticated with a caller-supplied
/// access token (reused from the existing GitHub token store / scope plumbing — see
/// <see cref="IGitHubAccessTokenProvider"/> and <see cref="IGitHubTokenScopeProvider"/>). Implemented
/// against the same "github" named <c>HttpClient</c> the issue-sync / blueprint-suggestion features use.
/// </summary>
public interface IGitHubPullRequestClient
{
    Task<GitHubPullRequestResult> CreatePullRequestAsync(
        string owner,
        string repo,
        string title,
        string? body,
        string baseBranch,
        string headBranch,
        bool draft,
        string accessToken,
        CancellationToken ct = default);

    Task<GitHubPullRequestResult?> FindOpenPullRequestAsync(
        string owner,
        string repo,
        string baseBranch,
        string headBranch,
        string accessToken,
        CancellationToken ct = default);
}
