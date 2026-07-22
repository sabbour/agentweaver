using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Github;

/// <summary>
/// REST implementation of <see cref="IGitHubRepositoryClient"/> against GitHub's user/org repository
/// endpoints, using the same "github" named <see cref="HttpClient"/> the pull-request /
/// blueprint-suggestion features already register. Supports attaching a brand-new GitHub repository to
/// a currently-unconnected (<c>Blank</c>-origin) project: the owner is resolved by the caller from
/// <see cref="ListRepositoryOwnersAsync"/> (never auto-picked), and repo-name collisions are retried
/// with a short numeric suffix instead of failing outright.
/// </summary>
public sealed class GitHubRepositoryClient : IGitHubRepositoryClient
{
    /// <summary>Max attempts (initial + retries) before giving up on a name collision.</summary>
    internal const int MaxNameAttempts = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubRepositoryClient> _logger;

    public GitHubRepositoryClient(IHttpClientFactory httpClientFactory, ILogger<GitHubRepositoryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GitHubRepositoryOwner>> ListRepositoryOwnersAsync(
        string accessToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var owners = new List<GitHubRepositoryOwner>();

        using var http = _httpClientFactory.CreateClient("github");

        using (var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user"))
        {
            AddGitHubHeaders(userRequest, accessToken);
            using var userResponse = await http.SendAsync(userRequest, ct).ConfigureAwait(false);
            if (userResponse.IsSuccessStatusCode)
            {
                var user = await userResponse.Content.ReadFromJsonAsync<GitHubUserResponse>(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(user?.Login))
                    owners.Add(new GitHubRepositoryOwner(user!.Login!, IsUser: true));
            }
            else
            {
                _logger.LogWarning(
                    "GitHub /user lookup returned {Status} while listing repository owners", (int)userResponse.StatusCode);
            }
        }

        using (var orgsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs"))
        {
            AddGitHubHeaders(orgsRequest, accessToken);
            using var orgsResponse = await http.SendAsync(orgsRequest, ct).ConfigureAwait(false);
            if (orgsResponse.IsSuccessStatusCode)
            {
                var orgs = await orgsResponse.Content.ReadFromJsonAsync<List<GitHubOrgResponse>>(ct).ConfigureAwait(false);
                if (orgs is not null)
                {
                    owners.AddRange(orgs
                        .Where(o => !string.IsNullOrWhiteSpace(o.Login))
                        .Select(o => new GitHubRepositoryOwner(o.Login!, IsUser: false)));
                }
            }
            else
            {
                _logger.LogWarning(
                    "GitHub /user/orgs lookup returned {Status} while listing repository owners", (int)orgsResponse.StatusCode);
            }
        }

        return owners;
    }

    public async Task<GitHubRepositoryResult> CreateRepositoryAsync(
        string owner,
        string name,
        bool isPrivate,
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        // Resolve up front whether the owner is the authenticated user themself (POST /user/repos)
        // or an org (POST /orgs/{owner}/repos) — GitHub uses distinct endpoints for the two cases.
        bool isOwnUser;
        try
        {
            using var http = _httpClientFactory.CreateClient("github");
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            AddGitHubHeaders(userRequest, accessToken);
            using var userResponse = await http.SendAsync(userRequest, ct).ConfigureAwait(false);
            if (!userResponse.IsSuccessStatusCode)
                return GitHubRepositoryResult.Failed(
                    "unauthorized", $"Could not resolve the authenticated GitHub user ({(int)userResponse.StatusCode}).");

            var user = await userResponse.Content.ReadFromJsonAsync<GitHubUserResponse>(ct).ConfigureAwait(false);
            isOwnUser = string.Equals(user?.Login, owner, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to reach GitHub while resolving the authenticated user for repository creation");
            return GitHubRepositoryResult.Failed("transport-error", ex.Message);
        }

        var candidate = name;
        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var result = await TryCreateAsync(owner, candidate, isPrivate, isOwnUser, accessToken, ct).ConfigureAwait(false);
            if (result.Success || result.ErrorReason != "name-already-exists")
                return result;

            candidate = $"{name}-{attempt + 1}";
        }

        return GitHubRepositoryResult.Failed(
            "name-already-exists",
            $"Could not find an available repository name for '{owner}/{name}' after {MaxNameAttempts} attempts.");
    }

    private async Task<GitHubRepositoryResult> TryCreateAsync(
        string owner, string name, bool isPrivate, bool isOwnUser, string accessToken, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient("github");
            var url = isOwnUser
                ? "https://api.github.com/user/repos"
                : $"https://api.github.com/orgs/{Uri.EscapeDataString(owner)}/repos";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new GitHubCreateRepositoryBody(name, isPrivate)),
            };
            AddGitHubHeaders(request, accessToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse>(ct).ConfigureAwait(false);
                if (created is null)
                    return GitHubRepositoryResult.Failed("unexpected-response", "GitHub returned no repository body.");
                return GitHubRepositoryResult.Ok(created.FullName, created.HtmlUrl, created.CloneUrl, created.DefaultBranch ?? "main");
            }

            var errorBody = await SafeReadStringAsync(response, ct).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity when errorBody.Contains("name already exists", StringComparison.OrdinalIgnoreCase) =>
                    GitHubRepositoryResult.Failed("name-already-exists", $"Repository name '{owner}/{name}' is already taken: {errorBody}"),
                HttpStatusCode.UnprocessableEntity =>
                    GitHubRepositoryResult.Failed("validation-failed", $"GitHub rejected the repository ({(int)response.StatusCode}): {errorBody}"),
                HttpStatusCode.Forbidden =>
                    GitHubRepositoryResult.Failed("insufficient-scope", $"GitHub denied repository creation (403 — the token likely lacks the 'repo' scope): {errorBody}"),
                HttpStatusCode.NotFound =>
                    GitHubRepositoryResult.Failed("owner-not-found", $"GitHub owner '{owner}' was not found or is not accessible: {errorBody}"),
                HttpStatusCode.Unauthorized =>
                    GitHubRepositoryResult.Failed("unauthorized", $"GitHub rejected the access token (401): {errorBody}"),
                _ => GitHubRepositoryResult.Failed("github-api-error", $"GitHub returned {(int)response.StatusCode}: {errorBody}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to reach GitHub while creating repository {Owner}/{Name}", owner, name);
            return GitHubRepositoryResult.Failed("transport-error", ex.Message);
        }
    }

    private static void AddGitHubHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));
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

    private sealed record GitHubCreateRepositoryBody(string Name, [property: JsonPropertyName("private")] bool Private);

    private sealed record GitHubUserResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubOrgResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("clone_url")] string CloneUrl,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch);
}
