using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Reads caller-authorized agent history from the Knowledge owner.</summary>
public sealed class KnowledgeAgentHistoryClient(HttpClient client)
{
    private const string TenantSelectorHeader = "X-Agentweaver-Tenant";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Forwards the authenticated caller and validates the bounded owner response.</summary>
    public async Task<ProjectAgentHistoryResponse> ReadAsync(
        HttpRequest callerRequest,
        string projectId,
        string agentId,
        CancellationToken cancellationToken)
    {
        // Forward only the validated bearer token and a single explicit tenant selector.
        var authorizationValues = callerRequest.Headers.Authorization;
        if (authorizationValues.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorizationValues[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new ProjectConfigException(
                "caller_token_missing",
                "A single validated bearer token is required to read agent history.",
                StatusCodes.Status401Unauthorized);

        var tenantValues = callerRequest.Headers[TenantSelectorHeader];
        if (tenantValues.Count > 1)
            throw ProjectConfigException.Forbidden();

        var path = $"/internal/projects/{Uri.EscapeDataString(projectId)}/agents/" +
            $"{Uri.EscapeDataString(agentId)}/history";
        var endpoint = new Uri(client.BaseAddress ?? throw OwnerUnavailable(), path);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.Parameter);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (tenantValues.Count == 1)
            request.Headers.TryAddWithoutValidation(TenantSelectorHeader, tenantValues[0]);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw OwnerUnavailable();
        }
        catch (HttpRequestException)
        {
            throw OwnerUnavailable();
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and <= 399 ||
                response.RequestMessage?.RequestUri != endpoint)
                throw InvalidOwnerResponse();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw ProjectConfigException.Forbidden();
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    ? OwnerUnavailable()
                    : InvalidOwnerResponse();
            if (response.Headers.CacheControl?.NoStore != true ||
                response.Content.Headers.ContentType?.MediaType != "application/json")
                throw InvalidOwnerResponse();

            var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            ProjectAgentHistoryResponse? history;
            try
            {
                history = JsonSerializer.Deserialize<ProjectAgentHistoryResponse>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                throw InvalidOwnerResponse();
            }

            if (history is null ||
                !string.Equals(history.ProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(history.AgentId, agentId, StringComparison.Ordinal) ||
                history.Items.IsDefault ||
                history.Items.Length > ProjectAgentHistoryContract.MaximumItems ||
                history.Items.Any(item =>
                    item.RecordId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.Type) ||
                    string.IsNullOrWhiteSpace(item.Content) ||
                    item.Content.Length > 40_000 ||
                    item.UpdatedAt == default))
                throw InvalidOwnerResponse();

            return history;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length &&
            (length <= 0 || length > ProjectAgentHistoryContract.MaximumResponseBytes))
            throw InvalidOwnerResponse();

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        // Enforce the limit while streaming because an owner may omit or understate Content-Length.
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > ProjectAgentHistoryContract.MaximumResponseBytes)
                throw InvalidOwnerResponse();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static ProjectConfigException OwnerUnavailable() =>
        new(
            "knowledge_history_unavailable",
            "Agent history is temporarily unavailable.",
            StatusCodes.Status503ServiceUnavailable);

    private static ProjectConfigException InvalidOwnerResponse() =>
        new(
            "invalid_knowledge_history_response",
            "Knowledge returned an invalid agent history response.",
            StatusCodes.Status502BadGateway);
}
