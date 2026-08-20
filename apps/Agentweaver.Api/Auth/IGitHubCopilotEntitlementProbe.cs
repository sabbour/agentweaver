using System.Net;
using System.Net.Http.Headers;

namespace Agentweaver.Api.Auth;

public interface IGitHubCopilotEntitlementProbe
{
    Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default);
}

public sealed class GitHubCopilotEntitlementProbe(
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubCopilotEntitlementProbe> logger) : IGitHubCopilotEntitlementProbe
{
    /// <summary>
    /// Copilot API endpoint used to prove entitlement. The token-exchange endpoint
    /// (<c>copilot_internal/v2/token</c>) is NOT usable for this: it does not exist on
    /// <c>api.githubcopilot.com</c> (404) and on <c>api.github.com</c> it is restricted to
    /// allow-listed editor OAuth apps (403 even for a Copilot-entitled account), so probing it
    /// reported every account as un-entitled. <c>GET /models</c> is the same surface the agent
    /// runtime itself calls, so a 200 here means Copilot really is usable with this token.
    /// Auth rejections from this probe are not authoritative for non-official OAuth apps, so they
    /// must stay inconclusive instead of flipping the UI to "No Copilot entitlement".
    /// </summary>
    private const string CopilotModelsUrl = "https://api.githubcopilot.com/models";

    public async Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CopilotModelsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Agentweaver", "1.0"));

            using var http = httpClientFactory.CreateClient("github");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                logger.LogWarning(
                    "GitHub Copilot entitlement probe returned {StatusCode} for token; treating as inconclusive because this probe may be restricted to official Copilot app tokens.",
                    response.StatusCode);
                return null;
            }

            // Any other non-success (5xx, throttling, network edge) is INCONCLUSIVE — returning null
            // leaves the previously known entitlement untouched instead of flipping it to "no Copilot".
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "GitHub Copilot entitlement probe returned unexpected {StatusCode}; treating as unknown.",
                    response.StatusCode);
                return null;
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
