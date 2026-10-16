using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Forwards session events, message delivery, and capture operations from the orchestrator to the Events owner.
/// The client carries the caller's bearer identity and tenant selector and validates every returned contract.
/// </summary>
internal sealed class EventsAddressedMessageClient(
    HttpClient httpClient,
    OrchestratorOptions options,
    IHttpContextAccessor httpContextAccessor) : IExecutableActionPolicyEvaluationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Ensures the Events owner has registered the session represented by <paramref name="identity"/>.
    /// </summary>
    /// <param name="context">The authenticated request context whose bearer and tenant are forwarded.</param>
    /// <param name="identity">The project, run, and session that must be registered.</param>
    /// <param name="cancellationToken">Cancels the owner request and response read.</param>
    /// <exception cref="CoordinationException">Thrown when the owner denies the request, is unavailable, or returns a mismatched session.</exception>
    /// <remarks>The method performs an HTTP registration request and does not accept success unless the returned identity matches exactly.</remarks>
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

    /// <summary>
    /// Asks the Events owner to create a fork from an explicit event and returns its validated result.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="source">The source session whose event history is forked.</param>
    /// <param name="fork">The requested fork point and options.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The fork result returned by the owner.</returns>
    /// <exception cref="CoordinationException">Thrown for denied, conflicting, unavailable, or invalid owner responses.</exception>
    /// <remarks>The response must be no-store; conflict and missing-source responses are surfaced as a fork conflict.</remarks>
    public async Task<SessionForkResult> ForkFromExplicitEventAsync(
        HttpContext context,
        SessionIdentity source,
        SessionForkRequest fork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fork);
        var owner = RequireEventsOwner(context, source);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(owner, $"/internal/sessions/{Uri.EscapeDataString(source.SessionId)}/fork"))
        {
            Content = JsonContent.Create(fork, options: JsonOptions)
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
                "events_session_fork_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_session_fork_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                throw new CoordinationException(
                    "events_session_fork_conflict", StatusCodes.Status409Conflict);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new CoordinationException(
                    "events_session_fork_unavailable", StatusCodes.Status503ServiceUnavailable);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created) ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException(
                    "events_session_fork_unavailable", StatusCodes.Status502BadGateway);

            try
            {
                return await response.Content.ReadFromJsonAsync<SessionForkResult>(
                    JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "events_session_fork_contract_invalid", StatusCodes.Status502BadGateway);
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_session_fork_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
        }
    }

    /// <summary>
    /// Appends a reference to a policy-evaluation receipt to the session event journal.
    /// </summary>
    /// <param name="identity">The session receiving the receipt reference.</param>
    /// <param name="receiptId">The non-empty receipt identifier to append.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>Whether the owner recognized this as a duplicate append.</returns>
    /// <exception cref="CoordinationException">Thrown when the receipt is invalid, the request context is unavailable, or the owner response fails validation.</exception>
    /// <remarks>The acknowledgement is bound to the session and receipt ID; the owner response must be no-store.</remarks>
    public async Task<ExecutableActionPolicyEvaluationAppendResult> AppendReceiptAsync(
        SessionIdentity identity,
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        if (receiptId == Guid.Empty)
            throw new CoordinationException(
                "events_policy_receipt_reference_invalid", StatusCodes.Status400BadRequest);
        var context = httpContextAccessor.HttpContext
            ?? throw new CoordinationException(
                "events_policy_receipt_context_unavailable", StatusCodes.Status503ServiceUnavailable);
        var owner = RequireEventsOwner(context, identity);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(owner, $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/policy-evaluations"))
        {
            Content = JsonContent.Create(new PolicyEvaluationReceiptReferenceRequest(receiptId), options: JsonOptions)
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
                "events_policy_receipt_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_policy_receipt_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created) ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException(
                    "events_policy_receipt_unavailable", StatusCodes.Status502BadGateway);

            PolicyEvaluationAppendAcknowledgment? acknowledgment;
            try
            {
                acknowledgment = await response.Content.ReadFromJsonAsync<PolicyEvaluationAppendAcknowledgment>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_policy_receipt_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
            var duplicate = response.StatusCode == HttpStatusCode.OK;
            if (acknowledgment is null ||
                acknowledgment.ReceiptId != receiptId ||
                acknowledgment.Identity != identity ||
                acknowledgment.Position < 1 ||
                acknowledgment.IsDuplicate != duplicate)
                throw new CoordinationException(
                    "events_policy_receipt_contract_invalid", StatusCodes.Status502BadGateway);
            return new ExecutableActionPolicyEvaluationAppendResult(duplicate);
        }
    }

    /// <summary>
    /// Writes a verified produced-run package to the Events journal and returns its durable acknowledgement.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="proof">The identity, capture ID, byte count, and digest that bind the package.</param>
    /// <param name="packageBytes">The package bytes whose length and SHA-256 must match the proof.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The owner's acknowledgement, including whether the capture was already present.</returns>
    /// <exception cref="CoordinationException">Thrown when proof or package bytes do not match, or the owner denies or fails the write.</exception>
    /// <remarks>This operation performs an HTTP write and accepts only a no-store acknowledgement for the exact proof.</remarks>
    public async Task<ProducedRunCaptureAcknowledgment> WriteProducedRunCaptureAsync(
        HttpContext context,
        ProducedRunCaptureProof proof,
        byte[] packageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(packageBytes);
        try
        {
            ProducedRunCaptureContractValidation.Validate(proof);
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException(
                "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
        if (packageBytes.Length != proof.PackageByteLength ||
            Hash(packageBytes) != proof.PackageSha256)
            throw new CoordinationException(
                "events_output_capture_package_invalid", StatusCodes.Status502BadGateway);

        var owner = RequireEventsOwner(context, proof.Identity);
        using var content = new ByteArrayContent(packageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(owner,
                $"/internal/sessions/{Uri.EscapeDataString(proof.Identity.SessionId)}/produced-run-captures/{Uri.EscapeDataString(proof.CaptureId)}"))
        {
            Content = content
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
                "events_output_capture_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_output_capture_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new CoordinationException(
                    "events_output_capture_conflict", StatusCodes.Status409Conflict);
            var duplicate = response.StatusCode == HttpStatusCode.OK;
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created) ||
                response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException(
                    "events_output_capture_unavailable", StatusCodes.Status502BadGateway);

            ProducedRunCaptureAcknowledgment? acknowledgment;
            try
            {
                acknowledgment = await response.Content.ReadFromJsonAsync<ProducedRunCaptureAcknowledgment>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new CoordinationException(
                    "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
            if (acknowledgment is null ||
                acknowledgment.Entry is null ||
                acknowledgment.Entry.Capture != proof ||
                acknowledgment.Entry.Position < 1 ||
                acknowledgment.IsDuplicate != duplicate)
                throw new CoordinationException(
                    "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway);
            return acknowledgment;
        }
    }

    /// <summary>
    /// Reads a produced-run package by its journal event and verifies its position, length, and digest.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="proof">The proof that identifies and authenticates the expected package.</param>
    /// <param name="cancellationToken">Cancels the owner request and content read.</param>
    /// <returns>The journal entry and package bytes after the response is fully validated.</returns>
    /// <exception cref="CoordinationException">Thrown when the event is missing, access is denied, or response metadata or bytes do not match the proof.</exception>
    /// <remarks>The response is bounded by the proof's byte length and must be no-store octet-stream content.</remarks>
    public async Task<ProducedRunCaptureContentResult> ReadProducedRunCaptureAsync(
        HttpContext context,
        ProducedRunCaptureProof proof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proof);
        try
        {
            ProducedRunCaptureContractValidation.Validate(proof);
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException(
                "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }

        var owner = RequireEventsOwner(context, proof.Identity);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(owner,
                $"/internal/sessions/{Uri.EscapeDataString(proof.Identity.SessionId)}/produced-run-captures/events/{proof.EventId:D}"));
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
                "events_output_capture_unavailable", StatusCodes.Status502BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "events_output_capture_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new CoordinationException(
                    "events_output_capture_not_found", StatusCodes.Status404NotFound);
            var positions = response.Headers.TryGetValues("X-Session-Event-Position", out var positionValues)
                ? positionValues.ToArray()
                : [];
            if (response.StatusCode != HttpStatusCode.OK ||
                response.Headers.CacheControl?.NoStore != true ||
                response.Content.Headers.ContentType?.MediaType != "application/octet-stream" ||
                response.Content.Headers.ContentLength is long contentLength &&
                    contentLength != proof.PackageByteLength ||
                positions.Length != 1 ||
                !long.TryParse(positions.SingleOrDefault(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var position) ||
                position < 1)
                throw new CoordinationException(
                    "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway);

            var bytes = new byte[checked((int)proof.PackageByteLength)];
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw new CoordinationException(
                    "events_output_capture_contract_invalid", StatusCodes.Status502BadGateway, exception);
            }
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 ||
                Hash(bytes) != proof.PackageSha256)
                throw new CoordinationException(
                    "events_output_capture_package_invalid", StatusCodes.Status502BadGateway);

            return new(new ProducedRunCaptureJournalEntry(proof, position), bytes);
        }
    }

    /// <summary>
    /// Reads one recorded SDK cache material item and validates its identity, event position, and content digest.
    /// </summary>
    /// <param name="context">The authenticated request context used to locate the Events owner.</param>
    /// <param name="identity">The session that owns the material.</param>
    /// <param name="eventId">The event that recorded the material.</param>
    /// <param name="kind">The material kind; only SDK cache material is supported.</param>
    /// <param name="actor">The runtime actor authorization forwarded to the owner.</param>
    /// <param name="cancellationToken">Cancels the bounded owner read.</param>
    /// <returns>The validated material reference and bytes.</returns>
    /// <exception cref="ArgumentException">Thrown when the event ID or material kind is invalid.</exception>
    /// <exception cref="CoordinationException">Thrown when the owner response is missing or fails contract validation.</exception>
    /// <remarks>The read is size-bounded and verifies that the bytes match the material's recorded SHA-256.</remarks>
    internal async Task<SessionMaterialReadResult> ReadSessionMaterialAsync(
        HttpContext context,
        SessionIdentity identity,
        Guid eventId,
        SessionMaterialKind kind,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        if (eventId == Guid.Empty || kind != SessionMaterialKind.SdkCache)
            throw new ArgumentException("A recorded SDK cache event is required.");
        var owner = RequireEventsOwner(context, identity);
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialReadResult>(
            httpClient,
            owner,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{eventId:D}/SdkCache",
            actor,
            body: null,
            maximumBytes: checked(SessionMaterialValidation.MaximumBytes * 2),
            allowNotFound: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "events_session_material_unavailable", StatusCodes.Status502BadGateway);
        try
        {
            SessionMaterialValidation.Validate(result.Material.Reference);
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException(
                "events_session_material_invalid", StatusCodes.Status502BadGateway, exception);
        }
        if (result.Material.ContractVersion != 1 ||
            result.Material.Identity != identity ||
            result.Material.EventId != eventId ||
            result.Material.Position < 1 ||
            result.Material.Reference.Material?.Kind != kind ||
            result.Bytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes } ||
            result.Bytes.LongLength != result.Material.Reference.ByteLength ||
            Hash(result.Bytes) != result.Material.Reference.Material.Sha256)
            throw new CoordinationException(
                "events_session_material_invalid", StatusCodes.Status502BadGateway);
        return result;
    }

    /// <summary>
    /// Submits an outbound addressed message to the Events owner for admission.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="message">The sender, recipient, fences, and request identity to admit.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The validated receipt for the accepted message.</returns>
    /// <exception cref="CoordinationException">Thrown when the caller identity is invalid, access is denied, or the owner response does not bind to the submitted message.</exception>
    /// <remarks>Admission is an HTTP write; the result must be no-store and must not contain an active claim.</remarks>
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

    /// <summary>
    /// Claims the next eligible addressed message for the recipient session, if one is available.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="recipient">The session that will receive the claim.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The owner-issued claim, or <see langword="null"/> when no message is available.</returns>
    /// <remarks>The returned claim is used as a fence for the later present and acknowledge operations.</remarks>
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

    /// <summary>
    /// Marks a claimed message as presented to its recipient.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="recipient">The session holding the claim.</param>
    /// <param name="messageId">The claimed message identifier.</param>
    /// <param name="claimFence">The claim fence that prevents a stale claimant from changing delivery state.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The updated message envelope.</returns>
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

    /// <summary>
    /// Acknowledges receipt of a claimed addressed message.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="recipient">The session holding the claim.</param>
    /// <param name="messageId">The claimed message identifier.</param>
    /// <param name="claimFence">The claim fence that prevents a stale claimant from acknowledging delivery.</param>
    /// <param name="cancellationToken">Cancels the owner request.</param>
    /// <returns>The updated message envelope.</returns>
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

    /// <summary>
    /// Sends a message-delivery control request and validates its no-store JSON response.
    /// </summary>
    /// <typeparam name="T">The expected owner response type.</typeparam>
    /// <param name="context">The authenticated request context forwarded to the owner.</param>
    /// <param name="recipient">The session whose delivery state is being changed.</param>
    /// <param name="method">The HTTP method for the control operation.</param>
    /// <param name="path">The owner-relative route.</param>
    /// <param name="content">Optional JSON request body.</param>
    /// <param name="cancellationToken">Cancels the HTTP request or response read.</param>
    /// <returns>The non-null response body.</returns>
    /// <exception cref="CoordinationException">Thrown when access is denied, transport fails, or the response is invalid.</exception>
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

    /// <summary>
    /// Resolves the configured Events owner after checking the request's run scope.
    /// </summary>
    private Uri RequireEventsOwner(HttpContext context, SessionIdentity identity) =>
        RequireEventsOwner(context, new CoordinationRunScope(identity.ProjectId, identity.RunId), options);

    /// <summary>
    /// Validates caller scopes, project/run binding, Events audience, and the HTTPS owner address.
    /// </summary>
    /// <param name="context">The request whose identity and tenant will be forwarded.</param>
    /// <param name="identity">The expected project and run scope.</param>
    /// <param name="options">Orchestrator settings that identify the Events audience and owner address.</param>
    /// <returns>The absolute HTTPS Events owner URI.</returns>
    /// <exception cref="CoordinationException">Thrown when caller binding or owner configuration is invalid.</exception>
    /// <remarks>Requiring an origin-only HTTPS address prevents paths, credentials, query strings, or fragments from changing the trusted destination.</remarks>
    internal static Uri RequireEventsOwner(
        HttpContext context, CoordinationRunScope identity, OrchestratorOptions options)
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

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 used to verify package and material bytes.
    /// </summary>
    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
