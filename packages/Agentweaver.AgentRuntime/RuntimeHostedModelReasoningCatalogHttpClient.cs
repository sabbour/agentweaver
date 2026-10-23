using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Publishes token-free hosted reasoning capabilities to the Projects &amp; Config owner.</summary>
public sealed class RuntimeHostedModelReasoningCatalogHttpClient(
    HttpClient client,
    Uri projectsConfigAddress,
    RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(projectsConfigAddress);

    /// <summary>Posts verified capability values using the caller's existing owner authorization.</summary>
    internal async Task<HttpStatusCode> PublishAsync(
        string projectId,
        HostedModelReasoningCatalogPublicationV1 publication,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(projectId);
        ArgumentNullException.ThrowIfNull(publication);

        var path = $"api/projects/{Uri.EscapeDataString(projectId)}/configuration/hosted-model-reasoning-catalog";
        // The bearer authorizes the owner call; the JSON body contains only the token-free catalog publication.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_address, path))
        {
            Content = JsonContent.Create(publication)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // Owner calls must not follow redirects or return cacheable responses across this authority boundary.
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri != request.RequestUri ||
            response.Headers.CacheControl?.NoStore != true)
            throw new RuntimeAuthorizationException("runtime_owner_response_contract_invalid");
        return response.StatusCode;
    }
}
