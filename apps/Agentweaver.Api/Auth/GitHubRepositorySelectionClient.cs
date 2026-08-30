using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Bounded, metadata-only view of repositories available through one Repo App user authorization.
/// This client never returns provider permission maps, content, or raw failure bodies.
/// </summary>
internal sealed class GitHubRepositorySelectionClient(IHttpClientFactory httpClientFactory)
{
    private const int PageSize = 100;
    private const int MaximumPages = 2;
    private const long MaximumResponseBytes = 512 * 1024;

    internal async Task<IReadOnlyList<GitHubRepositorySelectionCandidate>?> ListAsync(
        string accessToken,
        CancellationToken ct)
    {
        var candidates = new List<GitHubRepositorySelectionCandidate>();
        using var http = httpClientFactory.CreateClient("github");
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/user/repos?sort=pushed&per_page={PageSize}&page={page}&affiliation=owner%2Ccollaborator%2Corganization_member");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
                return null;

            var batch = await ReadBoundedJsonAsync(response.Content, ct).ConfigureAwait(false);
            if (batch is null)
                return null;

            candidates.AddRange(batch
                .Where(IsSafe)
                .Select(repository => new GitHubRepositorySelectionCandidate(
                    repository.Id!.Value,
                    repository.FullName!,
                    repository.Owner!.Login!,
                    repository.Private,
                    repository.DefaultBranch ?? "main",
                    repository.PushedAt)));
            if (batch.Count < PageSize)
                break;
        }

        return candidates;
    }

    internal async Task<IReadOnlyList<GitHubRepositoryOwner>?> ListOwnersAsync(string accessToken, CancellationToken ct)
    {
        using var http = httpClientFactory.CreateClient("github");
        using var userRequest = CreateRequest(HttpMethod.Get, "https://api.github.com/user", accessToken);
        using var userResponse = await http.SendAsync(userRequest, ct).ConfigureAwait(false);
        if (!userResponse.IsSuccessStatusCode)
            return null;
        var user = await userResponse.Content.ReadFromJsonAsync<GitHubRepositoryOwnerResponse>(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(user?.Login))
            return null;

        var owners = new List<GitHubRepositoryOwner> { new(user.Login, true) };
        using var orgsRequest = CreateRequest(HttpMethod.Get, "https://api.github.com/user/orgs", accessToken);
        using var orgsResponse = await http.SendAsync(orgsRequest, ct).ConfigureAwait(false);
        if (orgsResponse.IsSuccessStatusCode)
        {
            var orgs = await orgsResponse.Content.ReadFromJsonAsync<List<GitHubRepositoryOwnerResponse>>(ct).ConfigureAwait(false);
            owners.AddRange((orgs ?? []).Where(org => !string.IsNullOrWhiteSpace(org.Login))
                .Select(org => new GitHubRepositoryOwner(org.Login!, false)));
        }
        return owners;
    }

    internal async Task<GitHubCreatedRepository?> CreateAsync(
        string owner, string name, bool isPrivate, string accessToken, CancellationToken ct)
    {
        using var http = httpClientFactory.CreateClient("github");
        using var userRequest = CreateRequest(HttpMethod.Get, "https://api.github.com/user", accessToken);
        using var userResponse = await http.SendAsync(userRequest, ct).ConfigureAwait(false);
        var user = userResponse.IsSuccessStatusCode
            ? await userResponse.Content.ReadFromJsonAsync<GitHubRepositoryOwnerResponse>(ct).ConfigureAwait(false)
            : null;
        if (string.IsNullOrWhiteSpace(user?.Login))
            return null;

        var endpoint = string.Equals(user.Login, owner, StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com/user/repos"
            : $"https://api.github.com/orgs/{Uri.EscapeDataString(owner)}/repos";
        using var request = CreateRequest(HttpMethod.Post, endpoint, accessToken);
        request.Content = JsonContent.Create(new { name, @private = isPrivate });
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
            return null;
        var repository = await response.Content.ReadFromJsonAsync<GitHubCreatedRepositoryResponse>(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(repository?.FullName) || string.IsNullOrWhiteSpace(repository.CloneUrl)
            ? null
            : new GitHubCreatedRepository(repository.FullName, repository.CloneUrl);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));
        return request;
    }

    private static async Task<List<GitHubRepositoryResponse>?> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return JsonSerializer.Deserialize<List<GitHubRepositoryResponse>>(
            Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private static bool IsSafe(GitHubRepositoryResponse repository) =>
        repository.Id is > 0 &&
        !string.IsNullOrWhiteSpace(repository.FullName) &&
        !string.IsNullOrWhiteSpace(repository.Owner?.Login);

    private sealed class GitHubRepositoryResponse
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("owner")] public GitHubRepositoryOwnerResponse? Owner { get; init; }
        [JsonPropertyName("private")] public bool Private { get; init; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
        [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; init; }
    }

    private sealed class GitHubRepositoryOwnerResponse
    {
        [JsonPropertyName("login")] public string? Login { get; init; }
    }

    private sealed class GitHubCreatedRepositoryResponse
    {
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; init; }
    }
}

internal sealed record GitHubRepositoryOwner(string Login, bool IsUser);
internal sealed record GitHubCreatedRepository(string FullName, string CloneUrl);
