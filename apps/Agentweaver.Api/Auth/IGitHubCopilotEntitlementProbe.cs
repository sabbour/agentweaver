using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

public interface IGitHubCopilotEntitlementProbe
{
    Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default);
}

public sealed class GitHubCopilotEntitlementProbe(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubCopilotEntitlementProbe> logger) : IGitHubCopilotEntitlementProbe
{
    private const string CopilotTokenUrl = "https://api.githubcopilot.com/copilot_internal/v2/token";

    public async Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

            using var http = httpClientFactory.CreateClient("github");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return false;
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("token", out var tokenProp)
                && tokenProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(tokenProp.GetString()))
            {
                return true;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "GitHub Copilot entitlement probe failed.");
            return null;
        }
    }
}
