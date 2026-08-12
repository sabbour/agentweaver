using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;

namespace Agentweaver.Api.Webhooks;

public sealed record GitHubWebhookProvisioningResult(
    long HookId,
    bool Created,
    string Repository,
    string PayloadUrl);

public sealed class GitHubWebhookProvisioningException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IGitHubWebhookProvisioningService
{
    Task<GitHubWebhookProvisioningResult> ProvisionAsync(
        Project project,
        GitHubTokenScope tokenScope,
        Uri payloadUrl,
        CancellationToken ct = default);
}

public sealed class GitHubWebhookProvisioningService(
    IGitHubAccessTokenProvider accessTokenProvider,
    IProjectStore projectStore,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory) : IGitHubWebhookProvisioningService
{
    private static readonly string[] Events =
    [
        "issues",
        "issue_comment",
        "pull_request",
        "pull_request_review",
        "push",
        "release",
        "discussion",
    ];

    public async Task<GitHubWebhookProvisioningResult> ProvisionAsync(
        Project project,
        GitHubTokenScope tokenScope,
        Uri payloadUrl,
        CancellationToken ct = default)
    {
        var repository = GitHubWebhookEndpoints.NormalizeRepoFullName(project.Origin.SourceRepository);
        if (project.Origin.Kind != ProjectOriginKind.FromGitHub || repository is null)
            throw new GitHubWebhookProvisioningException(
                StatusCodes.Status409Conflict,
                "Connect this project to a GitHub repository before creating a webhook.");

        if (!payloadUrl.IsAbsoluteUri || (payloadUrl.Scheme != Uri.UriSchemeHttps && payloadUrl.Scheme != Uri.UriSchemeHttp))
            throw new GitHubWebhookProvisioningException(
                StatusCodes.Status400BadRequest,
                "The webhook payload URL must be an absolute HTTP or HTTPS URL.");

        var accessToken = await accessTokenProvider.GetValidAccessTokenAsync(tokenScope, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GitHubWebhookProvisioningException(
                StatusCodes.Status401Unauthorized,
                "The selected GitHub account is not connected. Reconnect it and try again.");

        var secret = await EnsureSecretAsync(project, ct).ConfigureAwait(false);
        var parts = repository.Split('/', 2);
        var hooksUrl =
            $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/hooks";

        using var http = httpClientFactory.CreateClient("github");
        using var listRequest = CreateRequest(HttpMethod.Get, $"{hooksUrl}?per_page=100", accessToken);
        using var listResponse = await http.SendAsync(listRequest, ct).ConfigureAwait(false);
        await EnsureGitHubSuccessAsync(listResponse, repository, ct).ConfigureAwait(false);

        var hooks = await listResponse.Content
            .ReadFromJsonAsync<GitHubHookResponse[]>(cancellationToken: ct)
            .ConfigureAwait(false) ?? [];
        var existing = hooks.FirstOrDefault(h =>
            string.Equals(h.Config?.Url, payloadUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            using var updateRequest = CreateRequest(
                HttpMethod.Patch,
                $"{hooksUrl}/{existing.Id}",
                accessToken,
                CreatePayload(payloadUrl, secret, includeName: false));
            using var updateResponse = await http.SendAsync(updateRequest, ct).ConfigureAwait(false);
            await EnsureGitHubSuccessAsync(updateResponse, repository, ct).ConfigureAwait(false);

            return new GitHubWebhookProvisioningResult(
                existing.Id,
                Created: false,
                repository,
                payloadUrl.AbsoluteUri);
        }

        using var createRequest = CreateRequest(
            HttpMethod.Post,
            hooksUrl,
            accessToken,
            CreatePayload(payloadUrl, secret, includeName: true));
        using var createResponse = await http.SendAsync(createRequest, ct).ConfigureAwait(false);
        await EnsureGitHubSuccessAsync(createResponse, repository, ct).ConfigureAwait(false);

        var created = await createResponse.Content
            .ReadFromJsonAsync<GitHubHookResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        if (created is null || created.Id <= 0)
            throw new GitHubWebhookProvisioningException(
                StatusCodes.Status502BadGateway,
                "GitHub created the webhook but returned an invalid response.");

        return new GitHubWebhookProvisioningResult(
            created.Id,
            Created: true,
            repository,
            payloadUrl.AbsoluteUri);
    }

    private async Task<string> EnsureSecretAsync(Project project, CancellationToken ct)
    {
        var secretKey = project.WebhookSecret ?? $"github-webhook:{project.Id}";
        if (project.WebhookSecret is not null)
        {
            var existing = await secretStore.GetSecretAsync(secretKey, ct).ConfigureAwait(false);
            if (existing.Found && !string.IsNullOrWhiteSpace(existing.Value))
                return existing.Value;
        }

        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await secretStore.SetSecretAsync(secretKey, secret, ct: ct).ConfigureAwait(false);
        await projectStore.UpdateWebhookSecretAsync(project.Id, secretKey, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return secret;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string accessToken,
        GitHubHookRequest? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        return request;
    }

    private static GitHubHookRequest CreatePayload(Uri payloadUrl, string secret, bool includeName) => new()
    {
        Name = includeName ? "web" : null,
        Active = true,
        Events = Events,
        Config = new GitHubHookConfig
        {
            Url = payloadUrl.AbsoluteUri,
            ContentType = "json",
            Secret = secret,
            InsecureSsl = "0",
        },
    };

    private static async Task EnsureGitHubSuccessAsync(
        HttpResponseMessage response,
        string repository,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        _ = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (statusCode, message) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => (
                StatusCodes.Status401Unauthorized,
                "GitHub rejected the selected account authorization. Reconnect the account and try again."),
            HttpStatusCode.Forbidden => (
                StatusCodes.Status403Forbidden,
                "The selected GitHub account cannot manage webhooks for this repository. Grant repo or write:repo_hook access, or use the manual setup."),
            HttpStatusCode.NotFound => (
                StatusCodes.Status404NotFound,
                $"GitHub repository '{repository}' was not found or is not accessible to the selected account."),
            HttpStatusCode.UnprocessableEntity => (
                StatusCodes.Status409Conflict,
                "GitHub rejected the webhook configuration. Check for a conflicting webhook or use the manual setup."),
            _ => (
                StatusCodes.Status502BadGateway,
                $"GitHub webhook provisioning failed with status {(int)response.StatusCode}."),
        };
        throw new GitHubWebhookProvisioningException(statusCode, message);
    }

    private sealed record GitHubHookRequest
    {
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; init; }
        [JsonPropertyName("active")] public required bool Active { get; init; }
        [JsonPropertyName("events")] public required IReadOnlyList<string> Events { get; init; }
        [JsonPropertyName("config")] public required GitHubHookConfig Config { get; init; }
    }

    private sealed record GitHubHookConfig
    {
        [JsonPropertyName("url")] public required string Url { get; init; }
        [JsonPropertyName("content_type")] public required string ContentType { get; init; }
        [JsonPropertyName("secret")] public string? Secret { get; init; }
        [JsonPropertyName("insecure_ssl")] public required string InsecureSsl { get; init; }
    }

    private sealed record GitHubHookResponse
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("config")] public GitHubHookConfig? Config { get; init; }
    }
}
