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
internal sealed class GitHubRepositorySelectionClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private const int PageSize = 100;
    private const int MaximumPages = 2;
    private const int MaximumRepositories = PageSize * MaximumPages;
    private const long MaximumResponseBytes = 512 * 1024;
    private readonly string _apiUrl = (configuration["Auth:RepoApp:ApiUrl"] ?? "https://api.github.com").TrimEnd('/');
    private readonly string _baseUrl = (configuration["Auth:RepoApp:BaseUrl"] ?? "https://github.com").TrimEnd('/');

    internal async Task<IReadOnlyList<GitHubRepositorySelectionCandidate>?> ListAsync(
        string accessToken,
        CancellationToken ct) =>
        (await BrowseAsync(accessToken, ct).ConfigureAwait(false))?.Repositories;

    internal async Task<GitHubRepositoryBrowseResult?> BrowseAsync(
        string accessToken,
        CancellationToken ct)
    {
        var installations = await ListAccessibleInstallationsAsync(accessToken, ct).ConfigureAwait(false);
        if (installations is null)
            return null;

        var repositoriesByInstallation = installations
            .Select(_ => new List<GitHubRepositorySelectionCandidate>())
            .ToList();
        var installationHasMorePages = Enumerable.Repeat(true, installations.Count).ToArray();
        var nextCandidateIndexes = new int[installations.Count];
        var candidates = new Dictionary<long, GitHubRepositorySelectionCandidate>();

        for (var page = 1; page <= MaximumPages && candidates.Count < MaximumRepositories; page++)
        {
            for (var installationIndex = 0; installationIndex < installations.Count; installationIndex++)
            {
                if (!installationHasMorePages[installationIndex])
                    continue;

                var installation = installations[installationIndex];
                var repositoriesUrl = $"{_apiUrl}/user/installations/{installation.Id}/repositories";
                using var request = CreateRequest(
                    HttpMethod.Get,
                    AppendPagination(repositoriesUrl, page),
                    accessToken);
                using var response = await httpClientFactory.CreateClient("github")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
                    return null;

                var batch = await ReadBoundedEnvelopeAsync<GitHubRepositoryResponse>(
                    response.Content,
                    "repositories",
                    ct).ConfigureAwait(false);
                if (batch is null)
                    return null;

                foreach (var repository in batch.Where(IsSafe))
                {
                    repositoriesByInstallation[installationIndex].Add(new GitHubRepositorySelectionCandidate(
                        repository.Id!.Value,
                        repository.FullName!,
                        repository.Owner!.Login!,
                        repository.Private,
                        repository.DefaultBranch ?? "main",
                        $"{_baseUrl}/{repository.FullName}",
                        repository.CloneUrl!,
                        repository.PushedAt));
                }

                installationHasMorePages[installationIndex] = batch.Count >= PageSize;
            }

            while (candidates.Count < MaximumRepositories)
            {
                var candidatesRemain = false;
                for (var installationIndex = 0;
                     installationIndex < repositoriesByInstallation.Count
                        && candidates.Count < MaximumRepositories;
                     installationIndex++)
                {
                    var installationCandidates = repositoriesByInstallation[installationIndex];
                    while (nextCandidateIndexes[installationIndex] < installationCandidates.Count)
                    {
                        candidatesRemain = true;
                        var candidate = installationCandidates[nextCandidateIndexes[installationIndex]++];
                        if (candidates.TryAdd(candidate.RepositoryId, candidate))
                            break;
                    }
                }

                if (!candidatesRemain)
                    break;
            }
        }

        return new GitHubRepositoryBrowseResult(
            candidates.Values
                .OrderByDescending(candidate => candidate.PushedAt ?? DateTimeOffset.MinValue)
                .ToList(),
            installations
                .OrderByDescending(installation => installation.AccountType == "user")
                .ThenBy(installation => installation.AccountLogin, StringComparer.OrdinalIgnoreCase)
                .Select(installation => new GitHubRepositoryInstallationMetadata(
                    installation.AccountLogin,
                    installation.AccountType,
                    installation.RepositorySelection,
                    installation.ManagementUrl))
                .ToList());
    }

    internal async Task<IReadOnlyList<GitHubRepositoryOwner>?> ListOwnersAsync(string accessToken, CancellationToken ct)
    {
        var installations = await ListAccessibleInstallationsAsync(accessToken, ct).ConfigureAwait(false);
        if (installations is null)
            return null;

        return installations
            .Where(CanCreateRepositories)
            .GroupBy(installation => installation.AccountLogin, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(installation =>
                string.Equals(installation.TargetType, "User", StringComparison.OrdinalIgnoreCase)).First())
            .OrderByDescending(installation => string.Equals(installation.TargetType, "User", StringComparison.OrdinalIgnoreCase))
            .ThenBy(installation => installation.AccountLogin, StringComparer.OrdinalIgnoreCase)
            .Select(installation => new GitHubRepositoryOwner(
                installation.AccountLogin,
                string.Equals(installation.TargetType, "User", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    internal async Task<GitHubCreatedRepository?> CreateAsync(
        string owner,
        string name,
        bool isPrivate,
        string accessToken,
        CancellationToken ct)
    {
        var installations = await ListAccessibleInstallationsAsync(accessToken, ct).ConfigureAwait(false);
        var installation = installations?
            .FirstOrDefault(candidate =>
                string.Equals(candidate.AccountLogin, owner, StringComparison.OrdinalIgnoreCase) &&
                CanCreateRepositories(candidate));
        if (installation is null)
            return null;

        var endpoint = string.Equals(installation.TargetType, "Organization", StringComparison.OrdinalIgnoreCase)
            ? $"{_apiUrl}/orgs/{Uri.EscapeDataString(owner)}/repos"
            : $"{_apiUrl}/user/repos";
        using var request = CreateRequest(HttpMethod.Post, endpoint, accessToken);
        request.Content = JsonContent.Create(new { name, @private = isPrivate });
        using var response = await httpClientFactory.CreateClient("github").SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
            return null;
        var repository = await response.Content.ReadFromJsonAsync<GitHubCreatedRepositoryResponse>(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(repository?.FullName)
            || !TryValidateProviderUrl(repository.CloneUrl, out var cloneUrl)
            || !TryValidateProviderUrl(repository.HtmlUrl, out var htmlUrl)
            ? null
            : new GitHubCreatedRepository(repository.FullName, cloneUrl, htmlUrl);
    }

    private async Task<IReadOnlyList<GitHubAccessibleInstallation>?> ListAccessibleInstallationsAsync(
        string accessToken,
        CancellationToken ct)
    {
        var installations = new Dictionary<long, GitHubAccessibleInstallation>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"{_apiUrl}/user/installations?per_page={PageSize}&page={page}",
                accessToken);
            using var response = await httpClientFactory.CreateClient("github")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
                return null;

            var batch = await ReadBoundedEnvelopeAsync<GitHubInstallationResponse>(
                response.Content,
                "installations",
                ct).ConfigureAwait(false);
            if (batch is null)
                return null;

            foreach (var installation in batch)
            {
                if (!TryCreateInstallation(installation, out var mapped))
                    return null;
                installations[mapped.Id] = mapped;
            }

            if (batch.Count < PageSize)
                break;
        }

        return installations.Values.ToList();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));
        return request;
    }

    private static async Task<List<T>?> ReadBoundedEnvelopeAsync<T>(
        HttpContent content,
        string propertyName,
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

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        if (!document.RootElement.TryGetProperty(propertyName, out var payload) || payload.ValueKind != JsonValueKind.Array)
            return null;
        return payload.Deserialize<List<T>>();
    }

    private bool IsSafe(GitHubRepositoryResponse repository) =>
        repository.Id is > 0 &&
        !string.IsNullOrWhiteSpace(repository.FullName) &&
        !string.IsNullOrWhiteSpace(repository.Owner?.Login) &&
        TryValidateProviderUrl(repository.CloneUrl, out _);

    private bool TryCreateInstallation(
        GitHubInstallationResponse installation,
        out GitHubAccessibleInstallation mapped)
    {
        mapped = default!;
        var accountType = installation.TargetType switch
        {
            "User" => "user",
            "Organization" => "organization",
            _ => null,
        };
        if (installation.Id is not > 0
            || string.IsNullOrWhiteSpace(installation.Account?.Login)
            || accountType is null
            || installation.RepositorySelection is not ("all" or "selected")
            || !TryValidateProviderUrl(installation.HtmlUrl, out var managementUrl))
            return false;

        mapped = new GitHubAccessibleInstallation(
            installation.Id.Value,
            installation.Account.Login,
            installation.TargetType!,
            accountType,
            installation.RepositorySelection,
            managementUrl,
            installation.Permissions ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private bool TryValidateProviderUrl(string? value, out string validatedUrl)
    {
        validatedUrl = "";
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var configuredOrigin)
            || configuredOrigin.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.Equals(candidate.IdnHost, configuredOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != configuredOrigin.Port)
            return false;

        validatedUrl = candidate.AbsoluteUri;
        return true;
    }

    private static bool CanCreateRepositories(GitHubAccessibleInstallation installation) =>
        installation.Permissions.TryGetValue("administration", out var administration) &&
        string.Equals(administration?.Trim(), "write", StringComparison.OrdinalIgnoreCase);

    private static string AppendPagination(string repositoriesUrl, int page)
    {
        var separator = repositoriesUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{repositoriesUrl}{separator}per_page={PageSize}&page={page}";
    }

    private sealed record GitHubAccessibleInstallation(
        long Id,
        string AccountLogin,
        string TargetType,
        string AccountType,
        string RepositorySelection,
        string ManagementUrl,
        IReadOnlyDictionary<string, string> Permissions);

    private sealed class GitHubRepositoryResponse
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("owner")] public GitHubRepositoryOwnerResponse? Owner { get; init; }
        [JsonPropertyName("private")] public bool Private { get; init; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; init; }
        [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; init; }
    }

    private sealed class GitHubInstallationResponse
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("account")] public GitHubRepositoryOwnerResponse? Account { get; init; }
        [JsonPropertyName("target_type")] public string? TargetType { get; init; }
        [JsonPropertyName("repository_selection")] public string? RepositorySelection { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("permissions")] public Dictionary<string, string>? Permissions { get; init; }
    }

    private sealed class GitHubRepositoryOwnerResponse
    {
        [JsonPropertyName("login")] public string? Login { get; init; }
    }

    private sealed class GitHubCreatedRepositoryResponse
    {
        [JsonPropertyName("full_name")] public string? FullName { get; init; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    }
}

internal sealed record GitHubRepositoryOwner(string Login, bool IsUser);
internal sealed record GitHubCreatedRepository(string FullName, string CloneUrl, string HtmlUrl);
internal sealed record GitHubRepositoryInstallationMetadata(
    string AccountLogin,
    string AccountType,
    string RepositorySelection,
    string ManagementUrl);
internal sealed record GitHubRepositoryBrowseResult(
    IReadOnlyList<GitHubRepositorySelectionCandidate> Repositories,
    IReadOnlyList<GitHubRepositoryInstallationMetadata> Installations);
