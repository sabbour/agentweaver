using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;

namespace Agentweaver.Identity.Broker;

public sealed record LinkRemoteMcpIdentityBindingRequest(
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256,
    string IdentityBindingReference,
    Guid OperationId);

public sealed record RemoteMcpIdentityBindingReceipt(
    string ProjectId,
    Guid ConnectionId,
    Guid OperationId,
    long FinalConfigurationRevision,
    string FinalConfigurationSha256,
    string IdentityBindingReference);

internal sealed class RemoteMcpOAuthEnvironmentException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class RemoteMcpOAuthIdentityBindingClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _environmentAddress;

    public RemoteMcpOAuthIdentityBindingClient(Uri environmentAddress)
        : this(environmentAddress, new HttpClient(RuntimeOwnerHttpTransport.CreateHandler(), disposeHandler: true),
            ownsClient: true)
    {
    }

    internal RemoteMcpOAuthIdentityBindingClient(Uri environmentAddress, HttpMessageHandler handler)
        : this(environmentAddress, new HttpClient(handler, disposeHandler: true), ownsClient: true)
    {
        ArgumentNullException.ThrowIfNull(handler);
    }

    internal RemoteMcpOAuthIdentityBindingClient(Uri environmentAddress, HttpClient client)
        : this(environmentAddress, client, ownsClient: false)
    {
    }

    private RemoteMcpOAuthIdentityBindingClient(Uri environmentAddress, HttpClient client, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        _environmentAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentAddress);
        _client = client;
        _ownsClient = ownsClient;
    }

    public async Task<RemoteMcpIdentityBindingReceipt> LinkAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        Guid connectionId,
        LinkRemoteMcpIdentityBindingRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(projectId) || connectionId == Guid.Empty ||
            input.ExpectedConfigurationRevision < 1 ||
            !IsLowerHexHash(input.ExpectedConfigurationSha256) ||
            !Guid.TryParseExact(input.IdentityBindingReference, "N", out var reference) ||
            reference == Guid.Empty ||
            !string.Equals(reference.ToString("N"), input.IdentityBindingReference, StringComparison.Ordinal) ||
            input.OperationId == Guid.Empty)
            throw InvalidRequest();
        if (!actor.Bearer.IsUsable())
            throw Denied();

        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/remote-mcp/connections/{connectionId:D}/identity-binding";
        var endpoint = new Uri(_environmentAddress, path);
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        request.Content = JsonContent.Create(input, input.GetType(), options: Json);

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!actor.Bearer.IsUsable())
            throw Denied();
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri != endpoint)
            throw new RuntimeAuthorizationException("runtime_owner_redirect_rejected");
        if (response.Headers.CacheControl?.NoStore != true)
            throw new RuntimeAuthorizationException("runtime_owner_cache_contract_invalid");
        if (!response.IsSuccessStatusCode)
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Denied(),
                HttpStatusCode.BadRequest => InvalidRequest(),
                HttpStatusCode.NotFound => NotFound(),
                HttpStatusCode.Conflict => RevisionConflict(),
                _ => new RuntimeAuthorizationException("runtime_owner_unavailable")
            };

        RemoteMcpIdentityBindingReceipt receipt;
        try
        {
            receipt = await response.Content.ReadFromJsonAsync<RemoteMcpIdentityBindingReceipt>(
                Json, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
        if (receipt.ProjectId != projectId || receipt.ConnectionId != connectionId ||
            receipt.OperationId != input.OperationId ||
            receipt.FinalConfigurationRevision <= input.ExpectedConfigurationRevision ||
            !IsLowerHexHash(receipt.FinalConfigurationSha256) ||
            receipt.IdentityBindingReference != input.IdentityBindingReference)
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");

        return receipt;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private static bool IsLowerHexHash(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static RemoteMcpOAuthEnvironmentException InvalidRequest() =>
        new("remote_mcp_identity_binding_request_invalid", StatusCodes.Status400BadRequest);

    private static RemoteMcpOAuthEnvironmentException NotFound() =>
        new("remote_mcp_connection_not_found", StatusCodes.Status404NotFound);

    private static RemoteMcpOAuthEnvironmentException RevisionConflict() =>
        new("remote_mcp_connection_revision_conflict", StatusCodes.Status409Conflict);

    private static RuntimeAuthorizationException Denied() =>
        new("runtime_owner_denied");
}
