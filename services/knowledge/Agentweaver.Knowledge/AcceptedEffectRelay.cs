using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Knowledge;

public sealed record AcceptedEffectDeliveryResult(
    string Delivery,
    string? Code = null,
    string? RequiredAudienceSubject = null,
    string? RequiredAudience = null,
    ProjectFactAcknowledgment? Acknowledgment = null);

public sealed class AcceptedEffectRelay(
    HttpClient client,
    IHttpContextAccessor contextAccessor,
    PostgresOutbox outbox,
    KnowledgeRuntimeOptions options,
    ILogger<AcceptedEffectRelay> logger)
{
    private const string EventsAuthorizationHeader = "X-Agentweaver-Events-Authorization";
    private const string KnowledgeAuthorizationHeader = "X-Agentweaver-Knowledge-Authorization";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<AcceptedEffectDeliveryResult> TryDeliverAsync(
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        OutboxDelivery? delivery = null;
        try
        {
            var stored = await outbox.ReadAsync(receiptId, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.Value.Event.Message.EventType != "knowledge.accepted-effect")
                return Pending("accepted_effect_outbox_missing");
            if (stored.Value.IsDelivered)
                return new AcceptedEffectDeliveryResult("DELIVERED");

            delivery = await outbox.ClaimAsync(
                $"knowledge-relay-{Environment.ProcessId}",
                receiptId,
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (delivery is null)
            {
                var current = await outbox.ReadAsync(receiptId, cancellationToken).ConfigureAwait(false);
                return current?.IsDelivered == true
                    ? new AcceptedEffectDeliveryResult("DELIVERED")
                    : Pending("accepted_effect_delivery_busy");
            }

            AcceptedEffectReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<AcceptedEffectReceipt>(
                    delivery.Event.Message.Payload.GetRawText(), JsonOptions);
            }
            catch (JsonException exception)
            {
                LogFailure(exception, "accepted_effect_receipt_invalid");
                return await FailAsync(delivery, "accepted_effect_receipt_invalid").ConfigureAwait(false);
            }
            if (receipt is null ||
                receipt.ReceiptId != receiptId ||
                receipt.SchemaVersion != AcceptedEffectContractVersions.CurrentSchemaVersion ||
                receipt.EventVersion != AcceptedEffectContractVersions.CurrentEventVersion ||
                receipt.EventVersion != delivery.Event.Message.EventVersion)
                return await FailAsync(delivery, "accepted_effect_receipt_invalid").ConfigureAwait(false);

            var forwarded = GetForwardedCallerHeaders();
            if (forwarded.Error is not null)
                return await FailAsync(delivery, forwarded.Error).ConfigureAwait(false);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(options.EventsBaseAddress, "/internal/project-facts/accepted-effects"))
            {
                Content = JsonContent.Create(new AcceptedEffectDeliveryRequest(
                    receipt.ReceiptId, receipt.SchemaVersion, receipt.EventVersion), options: JsonOptions)
            };
            request.Headers.Authorization = forwarded.EventsAuthorization;
            if (forwarded.KnowledgeAuthorization is not null)
                request.Headers.TryAddWithoutValidation(
                    KnowledgeAuthorizationHeader, forwarded.KnowledgeAuthorization.ToString());
            if (forwarded.TenantSelector is not null)
                request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", forwarded.TenantSelector);

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return await FailAsync(
                    delivery,
                    "events_audience_required",
                    receipt.Subject,
                    options.EventsAudience).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Redirect ||
                response.StatusCode == HttpStatusCode.MovedPermanently ||
                response.StatusCode == HttpStatusCode.TemporaryRedirect ||
                response.StatusCode == HttpStatusCode.PermanentRedirect)
                return await FailAsync(delivery, "events_redirect_rejected").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return await FailAsync(delivery, StatusCode(response.StatusCode)).ConfigureAwait(false);

            ProjectFactAcknowledgment? acknowledgment;
            try
            {
                acknowledgment = await response.Content.ReadFromJsonAsync<ProjectFactAcknowledgment>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                acknowledgment = null;
            }
            if (acknowledgment is null ||
                acknowledgment.ReceiptId != receipt.ReceiptId ||
                acknowledgment.SchemaVersion != receipt.SchemaVersion ||
                acknowledgment.EventVersion != receipt.EventVersion ||
                acknowledgment.FactId == Guid.Empty ||
                !string.Equals(acknowledgment.ProjectId, receipt.ProjectId, StringComparison.Ordinal) ||
                acknowledgment.Sequence < 1)
                return await FailAsync(delivery, "events_acknowledgment_invalid").ConfigureAwait(false);

            if (!await outbox.AcknowledgeAsync(
                    receiptId, delivery.LeaseToken, cancellationToken).ConfigureAwait(false))
                return await FailAsync(delivery, "accepted_effect_acknowledgment_fenced")
                    .ConfigureAwait(false);
            return new AcceptedEffectDeliveryResult("DELIVERED", Acknowledgment: acknowledgment);
        }
        catch (OperationCanceledException exception)
        {
            var code = cancellationToken.IsCancellationRequested
                ? "accepted_effect_delivery_cancelled"
                : "accepted_effect_delivery_timeout";
            LogFailure(exception, code);
            return delivery is null ? Pending(code) : await FailAsync(delivery, code).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            LogFailure(exception, "events_transport_unavailable");
            return delivery is null
                ? Pending("events_transport_unavailable")
                : await FailAsync(delivery, "events_transport_unavailable").ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            LogFailure(exception, "accepted_effect_outbox_invalid");
            return delivery is null
                ? Pending("accepted_effect_outbox_invalid")
                : await FailAsync(delivery, "accepted_effect_outbox_invalid").ConfigureAwait(false);
        }
        catch (NpgsqlException exception)
        {
            LogFailure(exception, "accepted_effect_storage_unavailable");
            return delivery is null
                ? Pending("accepted_effect_storage_unavailable")
                : await FailAsync(delivery, "accepted_effect_storage_unavailable").ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            LogFailure(exception, "accepted_effect_storage_timeout");
            return delivery is null
                ? Pending("accepted_effect_storage_timeout")
                : await FailAsync(delivery, "accepted_effect_storage_timeout").ConfigureAwait(false);
        }
    }

    private ForwardedCallerHeaders GetForwardedCallerHeaders()
    {
        var context = contextAccessor.HttpContext;
        var values = context?.Request.Headers.Authorization ?? default;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return new ForwardedCallerHeaders(null, null, null, "caller_token_missing");

        if (context!.Request.Headers[KnowledgeAuthorizationHeader].Count != 0)
            return new ForwardedCallerHeaders(null, null, null, "unexpected_forwarded_knowledge_token");

        var eventValues = context.Request.Headers[EventsAuthorizationHeader];
        if (eventValues.Count > 1)
            return new ForwardedCallerHeaders(null, null, null, "invalid_events_token");
        var eventsAuthorization = authorization;
        var knowledgeAuthorization = (AuthenticationHeaderValue?)null;
        if (eventValues.Count == 1)
        {
            if (!AuthenticationHeaderValue.TryParse(eventValues[0], out var parsedEventsAuthorization) ||
                !string.Equals(parsedEventsAuthorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parsedEventsAuthorization.Parameter))
                return new ForwardedCallerHeaders(null, null, null, "invalid_events_token");
            eventsAuthorization = parsedEventsAuthorization;
            if (!string.Equals(
                    authorization.Parameter, parsedEventsAuthorization.Parameter, StringComparison.Ordinal))
                knowledgeAuthorization = authorization;
        }

        var tenantValues = context!.Request.Headers["X-Agentweaver-Tenant"];
        if (tenantValues.Count > 1 ||
            (tenantValues.Count == 1 && !IsIdentifier(tenantValues[0])))
            return new ForwardedCallerHeaders(null, null, null, "invalid_tenant_selector");
        return new ForwardedCallerHeaders(
            eventsAuthorization,
            knowledgeAuthorization,
            tenantValues.Count == 1 ? tenantValues[0] : null,
            null);
    }

    private async Task<AcceptedEffectDeliveryResult> FailAsync(
        OutboxDelivery delivery,
        string code,
        string? requiredAudienceSubject = null,
        string? requiredAudience = null)
    {
        try
        {
            if (!await outbox.ReleaseAsync(
                    delivery.Event.Message.Id, delivery.LeaseToken, CancellationToken.None).ConfigureAwait(false))
                logger.LogDebug(
                    "Accepted-effect delivery lease was no longer owned. FailureCode={FailureCode}", code);
        }
        catch (NpgsqlException exception)
        {
            LogFailure(exception, code);
        }
        catch (TimeoutException exception)
        {
            LogFailure(exception, code);
        }
        return Pending(code, requiredAudienceSubject, requiredAudience);
    }

    private void LogFailure(Exception exception, string code) =>
        logger.LogWarning(
            "Accepted-effect delivery remains pending. FailureCode={FailureCode}; FailureType={FailureType}",
            code,
            exception.GetType().Name);

    private static AcceptedEffectDeliveryResult Pending(
        string code,
        string? subject = null,
        string? audience = null) =>
        new("PENDING", code, subject, audience);

    private static string StatusCode(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Forbidden => "events_forbidden",
            HttpStatusCode.Conflict => "events_conflict",
            HttpStatusCode.UnprocessableEntity => "events_contract_rejected",
            _ when (int)statusCode >= 500 => "events_unavailable",
            _ => "events_rejected"
        };

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private sealed record ForwardedCallerHeaders(
        AuthenticationHeaderValue? EventsAuthorization,
        AuthenticationHeaderValue? KnowledgeAuthorization,
        string? TenantSelector,
        string? Error);
}
