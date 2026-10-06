using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed class EventsAddressedMessageClient(
    HttpClient httpClient,
    OrchestratorOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task EnsureSessionAsync(
        HttpContext context,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        var owner = RequireEventsOwner(context, identity);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(owner, $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "events_session_registration_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_session_registration_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                throw new CoordinationException(
                    "events_session_registration_unavailable", StatusCodes.Status502BadGateway);
            SessionRecord? session;
            try
            {
                session = await response.Content.ReadFromJsonAsync<SessionRecord>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_session_registration_contract_invalid",
                    StatusCodes.Status502BadGateway,
                    exception);
            }
            if (session?.Identity != identity)
                throw new CoordinationException(
                    "events_session_registration_contract_invalid", StatusCodes.Status502BadGateway);
        }
    }

    public async Task<MessageAdmissionReceipt> AdmitAsync(
        HttpContext context,
        OwnerOutboundMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        var owner = RequireEventsOwner(context, message.Sender);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (!string.Equals(actor.Issuer, options.Issuer, StringComparison.Ordinal))
            throw new CoordinationException("caller_invalid", StatusCodes.Status403Forbidden);

        var authorization = CoordinationIdentity.RequireBearer(context);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(owner, "/internal/addressed-messages/admit"))
        {
            Content = JsonContent.Create(message, options: JsonOptions)
        };
        request.Headers.Authorization = authorization;
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "events_message_admission_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_message_admission_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created) ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException(
                    "events_message_admission_unavailable", StatusCodes.Status502BadGateway);
            MessageAdmissionReceipt? receipt;
            try
            {
                receipt = await response.Content.ReadFromJsonAsync<MessageAdmissionReceipt>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_message_admission_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
            if (receipt is null ||
                receipt.OwnerMessageId != message.OwnerMessageId ||
                receipt.Status != AddressedMessageStatus.Accepted ||
                receipt.Sender != message.Sender ||
                receipt.Recipient != message.Recipient ||
                receipt.SenderFence != message.SenderFence ||
                receipt.RecipientFence != message.RecipientFence ||
                receipt.Purpose != message.Purpose ||
                receipt.RequestId != message.RequestId ||
                receipt.MessageId == Guid.Empty ||
                receipt.ThreadId == Guid.Empty ||
                receipt.ThreadSequence < 1 ||
                receipt.Claim is not null)
                throw new CoordinationException(
                    "events_message_admission_contract_invalid", StatusCodes.Status502BadGateway);
            return receipt;
        }
    }

    public async Task<AddressedMessageClaim?> ClaimNextAsync(
        HttpContext context,
        SessionIdentity recipient,
        CancellationToken cancellationToken)
    {
        var result = await SendControlAsync<AddressedMessageClaimResult>(
            context,
            recipient,
            HttpMethod.Post,
            $"/internal/addressed-messages/projects/{Uri.EscapeDataString(recipient.ProjectId)}/runs/{Uri.EscapeDataString(recipient.RunId)}/sessions/{Uri.EscapeDataString(recipient.SessionId)}/claim",
            content: null,
            cancellationToken).ConfigureAwait(false);
        return result.Claim;
    }

    public Task<AddressedMessageEnvelope> PresentAsync(
        HttpContext context,
        SessionIdentity recipient,
        Guid messageId,
        long claimFence,
        CancellationToken cancellationToken) =>
        SendControlAsync<AddressedMessageEnvelope>(
            context,
            recipient,
            HttpMethod.Post,
            $"/internal/addressed-messages/projects/{Uri.EscapeDataString(recipient.ProjectId)}/runs/{Uri.EscapeDataString(recipient.RunId)}/sessions/{Uri.EscapeDataString(recipient.SessionId)}/messages/{messageId:D}/present",
            new AddressedMessageClaimFenceRequest(claimFence),
            cancellationToken);

    public Task<AddressedMessageEnvelope> AcknowledgeAsync(
        HttpContext context,
        SessionIdentity recipient,
        Guid messageId,
        long claimFence,
        CancellationToken cancellationToken) =>
        SendControlAsync<AddressedMessageEnvelope>(
            context,
            recipient,
            HttpMethod.Post,
            $"/internal/addressed-messages/projects/{Uri.EscapeDataString(recipient.ProjectId)}/runs/{Uri.EscapeDataString(recipient.RunId)}/sessions/{Uri.EscapeDataString(recipient.SessionId)}/messages/{messageId:D}/acknowledge",
            new AddressedMessageClaimFenceRequest(claimFence),
            cancellationToken);

    private async Task<T> SendControlAsync<T>(
        HttpContext context,
        SessionIdentity recipient,
        HttpMethod method,
        string path,
        object? content,
        CancellationToken cancellationToken)
    {
        var owner = RequireEventsOwner(context, recipient);
        _ = CoordinationIdentity.RequireActor(context.User, options.Issuer);

        using var request = new HttpRequestMessage(method, new Uri(owner, path))
        {
            Content = content is null ? null : JsonContent.Create(content, options: JsonOptions)
        };
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "events_message_delivery_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException("events_message_delivery_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException(
                    "events_message_delivery_unavailable", StatusCodes.Status502BadGateway);
            try
            {
                var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (body is null)
                    throw new CoordinationException(
                        "events_message_delivery_contract_invalid", StatusCodes.Status502BadGateway);
                return body;
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_message_delivery_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
        }
    }

    private Uri RequireEventsOwner(HttpContext context, SessionIdentity identity)
    {
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (scope.ProjectId != identity.ProjectId || scope.RunId != identity.RunId ||
            !CoordinationIdentity.HasAudience(context.User, options.EventsAudience) ||
            !Uri.TryCreate(options.EventsOwnerBaseAddress, UriKind.Absolute, out var owner) ||
            owner.Scheme != Uri.UriSchemeHttps || owner.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(owner.Query) || !string.IsNullOrEmpty(owner.Fragment) ||
            !string.IsNullOrEmpty(owner.UserInfo))
            throw new CoordinationException(
                "events_owner_configuration_or_binding_invalid", StatusCodes.Status503ServiceUnavailable);
        return owner;
    }
}
