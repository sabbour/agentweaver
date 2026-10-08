using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Broker;

internal sealed record GitHubRepoAppProviderOptions(
    string ClientId,
    string ClientSecret,
    Uri CallbackUri);

internal enum GitHubRepoAppProviderFailure
{
    PermissionDenied,
    Revoked,
    RateLimited,
    NotFound,
    InvalidResponse,
    ProviderUnavailable,
    OutcomeUncertain
}

internal sealed class GitHubRepoAppProviderException(
    GitHubRepoAppProviderFailure failure,
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public GitHubRepoAppProviderFailure Failure { get; } = failure;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

internal sealed record GitHubRepoAppTokenSet(
    SecretCredential AccessToken,
    SecretCredential RefreshToken);

internal sealed record GitHubRepoAppInstallationMetadata(
    long InstallationId,
    string AccountLogin,
    string AccountType,
    string RepositorySelection);

internal sealed record GitHubRepoAppRepository(
    long InstallationId,
    long RepositoryId,
    string FullName,
    string OwnerLogin,
    bool IsPrivate,
    string DefaultBranch);

internal sealed record GitHubRepoAppBrowseResult(
    IReadOnlyList<GitHubRepoAppRepository> Repositories,
    IReadOnlyList<GitHubRepoAppInstallationMetadata> Installations);

internal sealed class GitHubRepoAppProviderClient
{
    private const int PageSize = 100;
    private const int MaximumPages = 2;
    private const int MaximumResponseBytes = 512 * 1024;
    private const string ApiVersion = "2022-11-28";
    private readonly HttpClient _oauthClient;
    private readonly HttpClient _apiClient;
    private readonly GitHubRepoAppProviderOptions _options;
    private readonly TimeProvider _timeProvider;

    public GitHubRepoAppProviderClient(
        HttpClient oauthClient,
        HttpClient apiClient,
        GitHubRepoAppProviderOptions options,
        TimeProvider timeProvider)
    {
        _oauthClient = RequireOrigin(oauthClient, "github.com", nameof(oauthClient));
        _apiClient = RequireOrigin(apiClient, "api.github.com", nameof(apiClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret) ||
            options.CallbackUri is not { IsAbsoluteUri: true } callbackUri ||
            callbackUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(callbackUri.UserInfo) ||
            !string.IsNullOrEmpty(callbackUri.Fragment))
            throw new ArgumentException("Repo App OAuth configuration is invalid.", nameof(options));
    }

    public Task<GitHubRepoAppTokenSet> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken) =>
        RequestTokensAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = RequireValue(code, nameof(code)),
                ["redirect_uri"] = _options.CallbackUri.AbsoluteUri,
                ["code_verifier"] = RequireValue(codeVerifier, nameof(codeVerifier))
            },
            cancellationToken);

    public Task<GitHubRepoAppTokenSet> RefreshAsync(
        SecretCredential refreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        return RequestTokensAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken.GetValue()
            },
            cancellationToken);
    }

    public async Task<string> ReadLoginAsync(
        SecretCredential accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        using var request = CreateApiRequest(HttpMethod.Get, "user", accessToken);
        using var response = await SendApiRequestAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var login = document.RootElement.TryGetProperty("login", out var loginElement) &&
            loginElement.ValueKind == JsonValueKind.String
                ? loginElement.GetString()
                : null;
        return !string.IsNullOrWhiteSpace(login)
            ? login
            : throw InvalidResponse("GitHub did not return a user login.");
    }

    public async Task<GitHubRepoAppBrowseResult> BrowseAsync(
        SecretCredential accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        var installations = await ReadInstallationsAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var repositoriesByInstallation = new List<GitHubRepoAppRepository>[installations.Count];
        var hasNextPage = new bool[installations.Count];
        for (var index = 0; index < installations.Count; index++)
        {
            repositoriesByInstallation[index] = [];
            hasNextPage[index] = true;
        }

        var uniqueRepositories = new Dictionary<long, GitHubRepoAppRepository>();
        for (var page = 1; page <= MaximumPages && uniqueRepositories.Count < PageSize * MaximumPages; page++)
        {
            for (var index = 0; index < installations.Count; index++)
            {
                if (!hasNextPage[index])
                    continue;

                var installation = installations[index];
                var path = $"user/installations/{installation.InstallationId.ToString(CultureInfo.InvariantCulture)}/repositories" +
                    $"?per_page={PageSize}&page={page}";
                using var request = CreateApiRequest(HttpMethod.Get, path, accessToken);
                using var response = await SendApiRequestAsync(request, cancellationToken).ConfigureAwait(false);
                using var document = await ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var batch = ReadRepositoryBatch(document.RootElement, installation.InstallationId);
                repositoriesByInstallation[index].AddRange(batch);
                hasNextPage[index] = batch.Count == PageSize;
            }

            foreach (var candidates in repositoriesByInstallation)
            {
                foreach (var candidate in candidates)
                {
                    if (uniqueRepositories.TryGetValue(candidate.RepositoryId, out var existing) &&
                        existing.InstallationId != candidate.InstallationId)
                        throw InvalidResponse("GitHub returned a repository for multiple installations.");
                    uniqueRepositories[candidate.RepositoryId] = candidate;
                    if (uniqueRepositories.Count == PageSize * MaximumPages)
                        break;
                }
                if (uniqueRepositories.Count == PageSize * MaximumPages)
                    break;
            }
        }

        return new GitHubRepoAppBrowseResult(
            uniqueRepositories.Values
                .OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(repository => repository.RepositoryId)
                .ToArray(),
            installations);
    }

    private async Task<GitHubRepoAppTokenSet> RequestTokensAsync(
        Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var requestStartedAt = _timeProvider.GetUtcNow();

        try
        {
            using var response = await _oauthClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            using var document = await ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var providerError = document.RootElement.TryGetProperty("error", out var errorElement) &&
                    errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString()
                        : null;
                if (string.Equals(providerError, "invalid_grant", StringComparison.Ordinal))
                    throw new GitHubRepoAppProviderException(
                        GitHubRepoAppProviderFailure.Revoked,
                        "GitHub rejected the Repo App authorization or refresh token.",
                        response.StatusCode);
                throw CreateResponseException(response.StatusCode, isPost: true);
            }

            var root = document.RootElement;
            var accessToken = ReadToken(root, "access_token");
            var refreshToken = ReadToken(root, "refresh_token");
            var accessExpiresAt = ReadExpiry(root, "expires_in", requestStartedAt);
            var refreshExpiresAt = ReadExpiry(root, "refresh_token_expires_in", requestStartedAt);
            var now = _timeProvider.GetUtcNow();
            if (accessExpiresAt <= now || refreshExpiresAt <= now)
                throw InvalidResponse("GitHub returned an expired Repo App authorization.");

            return new GitHubRepoAppTokenSet(
                new SecretCredential(accessToken, accessExpiresAt, _timeProvider),
                new SecretCredential(refreshToken, refreshExpiresAt, _timeProvider));
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubRepoAppProviderException(
                GitHubRepoAppProviderFailure.OutcomeUncertain,
                "The GitHub Repo App token rotation outcome is uncertain.",
                exception.StatusCode,
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubRepoAppProviderException(
                GitHubRepoAppProviderFailure.OutcomeUncertain,
                "The GitHub Repo App token rotation timed out and its outcome is uncertain.",
                innerException: exception);
        }
    }

    private async Task<IReadOnlyList<GitHubRepoAppInstallationMetadata>> ReadInstallationsAsync(
        SecretCredential accessToken,
        CancellationToken cancellationToken)
    {
        var installations = new Dictionary<long, GitHubRepoAppInstallationMetadata>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var request = CreateApiRequest(
                HttpMethod.Get,
                $"user/installations?per_page={PageSize}&page={page}",
                accessToken);
            using var response = await SendApiRequestAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = await ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("installations", out var array) ||
                array.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("GitHub returned an invalid installations response.");

            foreach (var item in array.EnumerateArray())
            {
                var installation = ReadInstallation(item);
                if (installations.TryGetValue(installation.InstallationId, out var existing) &&
                    existing != installation)
                    throw InvalidResponse("GitHub returned conflicting installation metadata.");
                installations[installation.InstallationId] = installation;
            }

            if (array.GetArrayLength() < PageSize)
                break;
        }

        return installations.Values
            .OrderBy(installation => installation.AccountLogin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.InstallationId)
            .ToArray();
    }

    private async Task<HttpResponseMessage> SendApiRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _apiClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return response;

            var exception = CreateResponseException(response.StatusCode, isPost: false);
            response.Dispose();
            throw exception;
        }
        catch (HttpRequestException exception)
        {
            throw new GitHubRepoAppProviderException(
                GitHubRepoAppProviderFailure.ProviderUnavailable,
                "GitHub repository discovery is temporarily unavailable.",
                exception.StatusCode,
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubRepoAppProviderException(
                GitHubRepoAppProviderFailure.ProviderUnavailable,
                "GitHub repository discovery timed out.",
                innerException: exception);
        }
    }

    private static HttpRequestMessage CreateApiRequest(
        HttpMethod method,
        string path,
        SecretCredential accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.GetValue());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));
        return request;
    }

    private static GitHubRepoAppInstallationMetadata ReadInstallation(JsonElement element)
    {
        var installationId = ReadPositiveInt64(element, "id");
        if (!element.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            throw InvalidResponse("GitHub returned invalid installation metadata.");
        var login = ReadRequiredString(account, "login");
        var accountType = ReadRequiredString(account, "type");
        var repositorySelection = ReadRequiredString(element, "repository_selection");
        if (accountType is not ("User" or "Organization") ||
            repositorySelection is not ("all" or "selected"))
            throw InvalidResponse("GitHub returned unsupported installation metadata.");
        return new(installationId, login, accountType, repositorySelection);
    }

    private static IReadOnlyList<GitHubRepoAppRepository> ReadRepositoryBatch(
        JsonElement root,
        long installationId)
    {
        if (!root.TryGetProperty("repositories", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            throw InvalidResponse("GitHub returned an invalid installation repositories response.");

        var results = new List<GitHubRepoAppRepository>();
        foreach (var element in array.EnumerateArray())
        {
            var repositoryId = ReadPositiveInt64(element, "id");
            var fullName = ReadRequiredString(element, "full_name");
            if (!element.TryGetProperty("owner", out var owner) ||
                owner.ValueKind != JsonValueKind.Object)
                throw InvalidResponse("GitHub returned invalid repository metadata.");
            var ownerLogin = ReadRequiredString(owner, "login");
            var nameParts = fullName.Split('/', StringSplitOptions.None);
            if (nameParts.Length != 2 ||
                !string.Equals(nameParts[0], ownerLogin, StringComparison.OrdinalIgnoreCase) ||
                !element.TryGetProperty("private", out var privateElement) ||
                privateElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidResponse("GitHub returned invalid repository metadata.");
            var defaultBranch = element.TryGetProperty("default_branch", out var branch) &&
                branch.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(branch.GetString())
                    ? branch.GetString()!
                    : "main";
            results.Add(new(
                installationId,
                repositoryId,
                fullName,
                ownerLogin,
                privateElement.GetBoolean(),
                defaultBranch));
        }

        return results;
    }

    private static string ReadToken(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw InvalidResponse("GitHub did not return complete Repo App token material.");
        return property.GetString()!;
    }

    private static DateTimeOffset ReadExpiry(
        JsonElement root,
        string propertyName,
        DateTimeOffset requestStartedAt)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var seconds) ||
            seconds <= 0 ||
            seconds > TimeSpan.FromDays(365).TotalSeconds)
            throw InvalidResponse("GitHub returned invalid Repo App token expiry metadata.");
        return requestStartedAt.AddSeconds(seconds);
    }

    private static long ReadPositiveInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value <= 0)
            throw InvalidResponse("GitHub returned invalid numeric repository metadata.");
        return value;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw InvalidResponse("GitHub returned incomplete repository metadata.");
        return property.GetString()!;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw InvalidResponse("GitHub returned a response that exceeded the permitted size.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (bounded.Length + read > MaximumResponseBytes)
                throw InvalidResponse("GitHub returned a response that exceeded the permitted size.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, checked((int)bounded.Length)));
        }
        catch (JsonException exception)
        {
            throw new GitHubRepoAppProviderException(
                GitHubRepoAppProviderFailure.InvalidResponse,
                "GitHub returned an invalid response.",
                innerException: exception);
        }
    }

    private static GitHubRepoAppProviderException CreateResponseException(
        HttpStatusCode statusCode,
        bool isPost) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new GitHubRepoAppProviderException(
                    GitHubRepoAppProviderFailure.PermissionDenied,
                    "GitHub rejected the Repo App request.",
                    statusCode),
            HttpStatusCode.NotFound =>
                new GitHubRepoAppProviderException(
                    GitHubRepoAppProviderFailure.NotFound,
                    "GitHub did not find the requested Repo App resource.",
                    statusCode),
            (HttpStatusCode)429 =>
                new GitHubRepoAppProviderException(
                    GitHubRepoAppProviderFailure.RateLimited,
                    "GitHub rate-limited the Repo App request.",
                    statusCode),
            >= HttpStatusCode.InternalServerError =>
                new GitHubRepoAppProviderException(
                    isPost
                        ? GitHubRepoAppProviderFailure.OutcomeUncertain
                        : GitHubRepoAppProviderFailure.ProviderUnavailable,
                    "GitHub could not complete the Repo App request.",
                    statusCode),
            _ =>
                new GitHubRepoAppProviderException(
                    isPost
                        ? GitHubRepoAppProviderFailure.OutcomeUncertain
                        : GitHubRepoAppProviderFailure.InvalidResponse,
                    "GitHub rejected the Repo App request.",
                    statusCode)
        };

    private static GitHubRepoAppProviderException InvalidResponse(string message) =>
        new(GitHubRepoAppProviderFailure.InvalidResponse, message);

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static HttpClient RequireOrigin(HttpClient httpClient, string expectedHost, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(httpClient, parameterName);
        if (httpClient.BaseAddress is not { IsAbsoluteUri: true } address ||
            address.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(address.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            address.AbsolutePath != "/")
            throw new ArgumentException(
                $"The GitHub client must use the HTTPS {expectedHost} origin.", parameterName);
        return httpClient;
    }
}
