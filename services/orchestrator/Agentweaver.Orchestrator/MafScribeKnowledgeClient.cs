using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed class MafScribeKnowledgeClient(HttpClient client, Uri ownerAddress)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async Task<MafScribeTerminalReceipt> WriteAsync(
        RuntimeActorAuthorization authorization, CoordinationActor actor,
        MafExecutionScribeIntent intent, CancellationToken cancellationToken)
    {
        MafScribeContract.ValidateIntent(intent);
        if (intent.Actor != actor)
            throw new CoordinationException(
                "maf_execution_scribe_actor_changed", StatusCodes.Status403Forbidden);
        var owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(ownerAddress);
        var endpoint = new Uri(owner,
            $"/api/projects/{Uri.EscapeDataString(intent.Identity.ProjectId)}" +
            $"/runs/{Uri.EscapeDataString(intent.Identity.RunId)}" +
            $"/agents/{Uri.EscapeDataString(intent.AgentId)}/records");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                kind = KnowledgeRecordKind.Memory,
                type = MafScribeContract.RecordType,
                title = (string?)null,
                content = intent.Content,
                rationale = (string?)null,
                importance = "low",
                tags = ImmutableArray<string>.Empty
            }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        request.Headers.Add("Idempotency-Key", intent.IdempotencyKey);
        if (authorization.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", authorization.TenantSelector);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint ||
                (int)response.StatusCode is >= 300 and <= 399)
                throw InvalidResponse();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "maf_execution_scribe_owner_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new CoordinationException(
                    "maf_execution_scribe_intent_conflict", StatusCodes.Status409Conflict);
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
                throw new CoordinationException(
                    "maf_execution_scribe_owner_unavailable", StatusCodes.Status502BadGateway);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw InvalidResponse();
            await response.Content.LoadIntoBufferAsync(512 * 1024, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<KnowledgeRecordWriteResult>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            var record = result?.Record;
            if (result?.Status != KnowledgeWriteStatus.Created || record is null ||
                record.RecordId == Guid.Empty || record.Revision != 1 || record.RevisionId == Guid.Empty ||
                record.ProjectId != intent.Identity.ProjectId || record.SourceRunId != intent.Identity.RunId ||
                record.AgentId != intent.AgentId || record.Kind != KnowledgeRecordKind.Memory ||
                record.Type != MafScribeContract.RecordType || record.Content != intent.Content ||
                record.Importance != "low" || record.Tags.IsDefault || !record.Tags.IsEmpty ||
                record.Title is not null || record.Rationale is not null ||
                record.State != KnowledgeRecordState.Active || record.TrustState != KnowledgeTrustState.Pending)
                throw InvalidResponse();
            return new(intent.AssociationId, record.RecordId, record.Revision, record.RevisionId);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "maf_execution_scribe_owner_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "maf_execution_scribe_owner_unavailable", StatusCodes.Status502BadGateway, exception);
        }
    }

    private static CoordinationException InvalidResponse() =>
        new("maf_execution_scribe_owner_contract_invalid", StatusCodes.Status502BadGateway);
}
