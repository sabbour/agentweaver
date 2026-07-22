using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Github;

/// <summary>
/// REST implementation of <see cref="IGitHubPullRequestClient"/> against GitHub pull-request endpoints,
/// using the same "github" named <see cref="HttpClient"/> the blueprint-suggestion
/// (issue-sync-adjacent) feature already registers
/// (<see cref="Agentweaver.Api.Blueprints.GitHubRepoBlueprintSuggestionService"/>). Every known failure
/// mode (no commits, branch not pushed / unknown ref, insufficient token scope, unknown repository) is
/// mapped onto a <see cref="GitHubPullRequestResult"/> instead of throwing, and a pre-existing open PR
/// for the same branch is reused idempotently, so a workflow step calling this never crashes the run
/// (workflows-automation open-pull-request-action).
/// </summary>
public sealed class GitHubPullRequestClient : IGitHubPullRequestClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubPullRequestClient> _logger;

    public GitHubPullRequestClient(IHttpClientFactory httpClientFactory, ILogger<GitHubPullRequestClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GitHubPullRequestResult> CreatePullRequestAsync(
        string owner,
        string repo,
        string title,
        string? body,
        string baseBranch,
        string headBranch,
        bool draft,
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        try
        {
            using var http = _httpClientFactory.CreateClient("github");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls")
            {
                Content = JsonContent.Create(new GitHubCreatePullRequestBody(title, body, headBranch, baseBranch, draft)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = await response.Content.ReadFromJsonAsync<GitHubPullRequestResponse>(ct).ConfigureAwait(false);
                if (created is null)
                    return GitHubPullRequestResult.Failed("unexpected-response", "GitHub returned no pull request body.");
                return GitHubPullRequestResult.Ok(created.Number, created.HtmlUrl);
            }

            var errorBody = await SafeReadStringAsync(response, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
                string.Equals(ClassifyUnprocessable(errorBody), "pull-request-already-exists", StringComparison.Ordinal))
            {
                var existing = await FindOpenPullRequestAsync(
                    owner, repo, baseBranch, headBranch, accessToken, ct).ConfigureAwait(false);
                if (existing?.Success == true)
                    return existing;
            }

            return response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity => GitHubPullRequestResult.Failed(
                    ClassifyUnprocessable(errorBody),
                    $"GitHub rejected the pull request ({(int)response.StatusCode}): {errorBody}"),
                HttpStatusCode.Forbidden => GitHubPullRequestResult.Failed(
                    "insufficient-scope",
                    $"GitHub denied pull request creation (403 — the token likely lacks the 'repo' scope): {errorBody}"),
                HttpStatusCode.NotFound => GitHubPullRequestResult.Failed(
                    "repository-not-found",
                    $"GitHub repository '{owner}/{repo}' was not found or is not accessible: {errorBody}"),
                HttpStatusCode.Unauthorized => GitHubPullRequestResult.Failed(
                    "unauthorized",
                    $"GitHub rejected the access token (401): {errorBody}"),
                _ => GitHubPullRequestResult.Failed(
                    "github-api-error",
                    $"GitHub returned {(int)response.StatusCode}: {errorBody}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to reach GitHub while opening a pull request for {Owner}/{Repo}", owner, repo);
            return GitHubPullRequestResult.Failed("transport-error", ex.Message);
        }
    }

    public async Task<GitHubPullRequestResult?> FindOpenPullRequestAsync(
        string owner,
        string repo,
        string baseBranch,
        string headBranch,
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(headBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        try
        {
            using var http = _httpClientFactory.CreateClient("github");
            var query =
                $"head={Uri.EscapeDataString($"{owner}:{headBranch}")}&base={Uri.EscapeDataString(baseBranch)}&state=open";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls?{query}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var errorBody = await SafeReadStringAsync(response, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "GitHub open-PR lookup for {Owner}/{Repo} {Head}->{Base} returned {Status}: {Body}",
                    owner, repo, headBranch, baseBranch, (int)response.StatusCode, errorBody);
                return null;
            }

            var pulls = await response.Content.ReadFromJsonAsync<List<GitHubPullRequestResponse>>(ct).ConfigureAwait(false);
            var existing = pulls?.FirstOrDefault();
            return existing is null ? null : GitHubPullRequestResult.Ok(existing.Number, existing.HtmlUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Failed to look up an existing GitHub pull request for {Owner}/{Repo} {Head}->{Base}",
                owner, repo, headBranch, baseBranch);
            return null;
        }
    }

    /// <summary>
    /// GitHub's 422 for pull request creation is used for several distinct conditions (no commits
    /// between the branches, a pre-existing open PR for the same head/base, or an invalid head/base
    /// ref); the message text is the only signal available to distinguish them.
    /// </summary>
    private static string ClassifyUnprocessable(string errorBody)
    {
        if (errorBody.Contains("No commits between", StringComparison.OrdinalIgnoreCase))
            return "no-commits";
        if (errorBody.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("pull request already exists", StringComparison.OrdinalIgnoreCase))
            return "pull-request-already-exists";
        if (errorBody.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "unknown-branch";
        return "validation-failed";
    }

    private static async Task<string> SafeReadStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record GitHubCreatePullRequestBody(
        string Title,
        string? Body,
        string Head,
        string Base,
        bool Draft);

    private sealed record GitHubPullRequestResponse(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("html_url")] string HtmlUrl);
}
