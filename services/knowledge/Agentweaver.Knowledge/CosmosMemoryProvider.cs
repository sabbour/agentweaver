using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;

namespace Agentweaver.Knowledge;

public sealed class CosmosMemoryProvider : IMemoryProvider
{
    public const string ProviderId = "cosmos.memory";
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private const string RecordDocumentType = "record";
    private const string RevisionDocumentType = "revision";
    private const string WriteDocumentType = "write";
    private const string StreamDocumentType = "stream";
    private const string AcceptedEffectDocumentType = "accepted-effect";
    private const int MaximumBatchRetries = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ICosmosMemoryDocumentStore _store;
    private readonly CosmosMemoryOptions _options;
    private readonly TimeProvider _timeProvider;

    public CosmosMemoryProvider(
        ICosmosMemoryDocumentStore store,
        CosmosMemoryOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _store = store;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new ProviderDescriptor(
            ProviderSeam.Memory,
            ProviderId,
            AdapterVersion,
            options.OptionsSchemaVersion,
            ProviderHostingPattern.RemoteService,
            MemoryProviderCapabilities.All);
    }

    public ProviderDescriptor Descriptor { get; }

    public async Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Seam != ProviderSeam.Memory ||
            !string.Equals(candidate.ProviderId, ProviderId, StringComparison.Ordinal) ||
            candidate.AdapterVersion != AdapterVersion ||
            candidate.OptionsSchemaVersion != _options.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, _options.OptionsRevision, StringComparison.Ordinal) ||
            !MemoryProviderCapabilities.All.IsSubsetOf(candidate.AdvertisedCapabilities))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider candidate does not match configured Cosmos Memory options.");

        try
        {
            var identity = await _store.ReadContainerIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(identity.DatabaseId, _options.DatabaseId, StringComparison.Ordinal) ||
                !string.Equals(identity.ContainerId, _options.ContainerId, StringComparison.Ordinal) ||
                identity.PartitionKeyPaths.Length != 1 ||
                !string.Equals(
                    identity.PartitionKeyPaths[0],
                    CosmosMemoryOptions.PartitionKeyPath,
                    StringComparison.Ordinal) ||
                identity.DefaultTimeToLiveSeconds is > 0 ||
                !identity.HasRequiredSearchCompositeIndex)
                throw new KnowledgeProviderUnavailableException(
                    "The configured Cosmos Memory container has an incompatible identity, partition key, indexing, or retention policy.");
            return new ResourceNegotiation(
                new ProviderResourceRef(
                    ProviderSeam.Memory,
                    ProviderId,
                    _options.ResourceId,
                    _options.ResourceGeneration),
                MemoryProviderCapabilities.All);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KnowledgeProviderUnavailableException)
        {
            throw;
        }
        catch (CosmosException)
        {
            throw new KnowledgeProviderUnavailableException(
                "The configured Cosmos Memory container could not be negotiated.");
        }
    }

    public Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ValidateIdentifier(query.ProjectId, nameof(query.ProjectId));
            ValidateIdentifier(query.AgentId, nameof(query.AgentId));
            if (query.Page < 1 || query.PageSize is < 1 or > 100)
                throw new KnowledgeApiException(
                    "invalid_page",
                    "Knowledge search page must be positive and page size must be between 1 and 100.",
                    StatusCodes.Status400BadRequest);
            var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
            if (text?.Length > 256)
                throw new KnowledgeApiException(
                    "invalid_query",
                    "Knowledge search text cannot exceed 256 characters.",
                    StatusCodes.Status400BadRequest);
            return await _store.SearchAsync(query with { Query = text }, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    public Task<KnowledgeRecord?> ReadAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            if (recordId == Guid.Empty)
                throw new KnowledgeApiException(
                    "invalid_record_id",
                    "A Knowledge record ID is required.",
                    StatusCodes.Status400BadRequest);
            var stored = await ReadRecordDocumentAsync(projectId, recordId, cancellationToken)
                .ConfigureAwait(false);
            return stored?.Document.Record;
        }, cancellationToken);

    public Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            if (recordId == Guid.Empty)
                throw new KnowledgeApiException(
                    "invalid_record_id",
                    "A Knowledge record ID is required.",
                    StatusCodes.Status400BadRequest);
            ValidatePage(page, pageSize);
            return await _store.ReadRevisionsAsync(
                projectId, recordId, page, pageSize, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> CreateAsync(
        KnowledgeRecordCreate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateCreate(input, idempotencyKey);
            var normalized = input with { Tags = NormalizeTags(input.Tags) };
            var fingerprint = Fingerprint(normalized);
            var duplicate = await ReadWriteReceiptAsync(
                input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };

            var now = _timeProvider.GetUtcNow();
            var revisionId = Guid.NewGuid();
            var state = normalized.Kind == KnowledgeRecordKind.Proposal
                ? KnowledgeRecordState.Pending
                : KnowledgeRecordState.Active;
            var trustState = normalized.Kind == KnowledgeRecordKind.SessionContext
                ? KnowledgeTrustState.Approved
                : KnowledgeTrustState.Pending;
            var record = new KnowledgeRecord(
                Guid.NewGuid(),
                normalized.ProjectId,
                normalized.AgentId,
                normalized.Kind,
                normalized.Type,
                normalized.Title,
                normalized.Content,
                normalized.Rationale,
                normalized.Importance,
                normalized.Tags,
                state,
                trustState,
                1,
                revisionId,
                null,
                normalized.SourceRunId,
                normalized.SourceSessionId,
                null,
                now,
                now);
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Created, record);
            var batch = await _store.ExecuteBatchAsync(
                input.ProjectId,
                [
                    CreateRecordOperation(record),
                    CreateRevisionOperation(
                        record,
                        normalized.Kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created"),
                    CreateWriteReceiptOperation(
                        input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record", result)
                ],
                cancellationToken).ConfigureAwait(false);
            if (batch.Succeeded)
                return result;
            if (IsConflict(batch))
            {
                duplicate = await ReadWriteReceiptAsync(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> UpdateAsync(
        KnowledgeRecordUpdate input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateUpdate(input, idempotencyKey);
            var normalized = input with { Tags = NormalizeTags(input.Tags) };
            var fingerprint = Fingerprint(normalized);
            var duplicate = await ReadWriteReceiptAsync(
                normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record",
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };

            var stored = await ReadRecordDocumentAsync(
                normalized.ProjectId, normalized.RecordId, cancellationToken).ConfigureAwait(false);
            var current = stored?.Document.Record;
            if (current is null)
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != normalized.ExpectedRevision)
            {
                duplicate = await ReadWriteReceiptAsync(
                    normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            }
            if (current.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.SessionContext) ||
                normalized.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived))
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

            var contentChanged = current.Type != normalized.Type ||
                current.Title != normalized.Title ||
                current.Content != normalized.Content ||
                current.Rationale != normalized.Rationale ||
                current.Importance != normalized.Importance ||
                !current.Tags.SequenceEqual(normalized.Tags, StringComparer.Ordinal);
            var trustState = current.Kind == KnowledgeRecordKind.SessionContext
                ? current.TrustState
                : contentChanged ? KnowledgeTrustState.Pending : current.TrustState;
            var updated = current with
            {
                Type = normalized.Type,
                Title = normalized.Title,
                Content = normalized.Content,
                Rationale = normalized.Rationale,
                Importance = normalized.Importance,
                Tags = normalized.Tags,
                State = normalized.State,
                TrustState = trustState,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
            var batch = await _store.ExecuteBatchAsync(
                normalized.ProjectId,
                [
                    ReplaceRecordOperation(updated, stored!.ETag),
                    CreateRevisionOperation(updated, "updated"),
                    CreateWriteReceiptOperation(
                        normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record", result)
                ],
                cancellationToken).ConfigureAwait(false);
            if (batch.Succeeded)
                return result;
            if (IsConflict(batch))
            {
                duplicate = await ReadWriteReceiptAsync(
                    normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                var latest = await ReadRecordDocumentAsync(
                    normalized.ProjectId, normalized.RecordId, cancellationToken).ConfigureAwait(false);
                return latest?.Document.Record is { } latestRecord
                    ? new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: latestRecord.Revision)
                    : new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> RejectProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (proposalId == Guid.Empty || expectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_proposal_revision",
                    "A proposal ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(new { projectId, runId, proposalId, expectedRevision });
            var duplicate = await ReadWriteReceiptAsync(
                projectId, actorFingerprint, idempotencyKey, fingerprint, "record",
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };

            var stored = await ReadRecordDocumentAsync(projectId, proposalId, cancellationToken).ConfigureAwait(false);
            var current = stored?.Document.Record;
            if (current is null || current.Kind != KnowledgeRecordKind.Proposal ||
                !string.Equals(current.SourceRunId, runId, StringComparison.Ordinal))
                return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            if (current.Revision != expectedRevision)
            {
                duplicate = await ReadWriteReceiptAsync(
                    projectId, actorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
            }
            if (current.State != KnowledgeRecordState.Pending ||
                current.TrustState != KnowledgeTrustState.Pending)
                return new KnowledgeRecordWriteResult(
                    KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

            var updated = current with
            {
                State = KnowledgeRecordState.Rejected,
                TrustState = KnowledgeTrustState.Rejected,
                Revision = checked(current.Revision + 1),
                PreviousRevisionId = current.RevisionId,
                RevisionId = Guid.NewGuid(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
            var batch = await _store.ExecuteBatchAsync(
                projectId,
                [
                    ReplaceRecordOperation(updated, stored!.ETag),
                    CreateRevisionOperation(updated, "proposal_rejected"),
                    CreateWriteReceiptOperation(
                        projectId, actorFingerprint, idempotencyKey, fingerprint, "record", result)
                ],
                cancellationToken).ConfigureAwait(false);
            if (batch.Succeeded)
                return result;
            if (IsConflict(batch))
            {
                duplicate = await ReadWriteReceiptAsync(
                    projectId, actorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                var latest = await ReadRecordDocumentAsync(projectId, proposalId, cancellationToken)
                    .ConfigureAwait(false);
                return latest?.Document.Record is { } latestRecord
                    ? new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: latestRecord.Revision)
                    : new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeProposalPromotionResult> PromoteProposalAsync(
        string projectId,
        string runId,
        Guid proposalId,
        int expectedRevision,
        string actorFingerprint,
        AcceptedEffectAuthorizationBounds authorization,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateAuthorizationBounds(projectId, runId, authorization);
            ValidateIdempotencyKey(idempotencyKey);
            if (proposalId == Guid.Empty || expectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_proposal_revision",
                    "A proposal ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(new { projectId, runId, proposalId, expectedRevision });
            var duplicate = await ReadWriteReceiptAsync(
                projectId, actorFingerprint, idempotencyKey, fingerprint, "promotion",
                cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                return Deserialize<KnowledgeProposalPromotionResult>(duplicate.ResultJson) with { IsDuplicate = true };

            var streamId = $"knowledge/{projectId}/{runId}";
            var streamDocumentId = CosmosMemoryDocumentIds.Stream(streamId);
            for (var attempt = 0; attempt < MaximumBatchRetries; attempt++)
            {
                var storedProposal = await ReadRecordDocumentAsync(
                    projectId, proposalId, cancellationToken).ConfigureAwait(false);
                var proposal = storedProposal?.Document.Record;
                if (proposal is null || proposal.Kind != KnowledgeRecordKind.Proposal ||
                    !string.Equals(proposal.SourceRunId, runId, StringComparison.Ordinal))
                    return new KnowledgeProposalPromotionResult(KnowledgeWriteStatus.NotFound, null, null, null);
                if (proposal.Revision != expectedRevision)
                {
                    duplicate = await ReadWriteReceiptAsync(
                        projectId, actorFingerprint, idempotencyKey, fingerprint, "promotion",
                        cancellationToken).ConfigureAwait(false);
                    if (duplicate is not null)
                        return Deserialize<KnowledgeProposalPromotionResult>(duplicate.ResultJson)
                            with { IsDuplicate = true };
                    return new KnowledgeProposalPromotionResult(
                        KnowledgeWriteStatus.Stale, null, null, null, CurrentRevision: proposal.Revision);
                }
                if (proposal.State != KnowledgeRecordState.Pending ||
                    proposal.TrustState != KnowledgeTrustState.Pending)
                    return new KnowledgeProposalPromotionResult(
                        KnowledgeWriteStatus.InvalidState, null, null, null, CurrentRevision: proposal.Revision);

                var storedStream = await _store.ReadAsync(
                    projectId, streamDocumentId, cancellationToken).ConfigureAwait(false);
                var nextSequence = checked((storedStream?.Document.LastSequence ?? 0) + 1);
                var now = _timeProvider.GetUtcNow();
                var decisionId = Guid.NewGuid();
                var eventId = Guid.NewGuid();
                var decision = new KnowledgeRecord(
                    decisionId,
                    proposal.ProjectId,
                    proposal.AgentId,
                    KnowledgeRecordKind.Decision,
                    proposal.Type,
                    proposal.Title,
                    proposal.Content,
                    proposal.Rationale,
                    proposal.Importance,
                    proposal.Tags,
                    KnowledgeRecordState.Active,
                    KnowledgeTrustState.Approved,
                    1,
                    Guid.NewGuid(),
                    null,
                    proposal.SourceRunId,
                    proposal.SourceSessionId,
                    null,
                    now,
                    now);
                var promotedProposal = proposal with
                {
                    State = KnowledgeRecordState.Promoted,
                    Revision = checked(proposal.Revision + 1),
                    PreviousRevisionId = proposal.RevisionId,
                    RevisionId = Guid.NewGuid(),
                    PromotedDecisionId = decisionId,
                    UpdatedAt = now
                };
                var receipt = new AcceptedEffectReceipt(
                    eventId,
                    AcceptedEffectContractVersions.CurrentSchemaVersion,
                    AcceptedEffectContractVersions.CurrentEventVersion,
                    projectId,
                    runId,
                    decisionId,
                    decisionId,
                    decision.Revision,
                    authorization.Issuer,
                    authorization.Subject,
                    authorization.TenantId,
                    authorization.BoundProjectId,
                    authorization.BoundRunId,
                    authorization.AuthorizationResourceType,
                    authorization.AuthorizationResourceId,
                    authorization.AuthorizationRevision,
                    authorization.MembershipRevision,
                    authorization.ProjectRevision,
                    authorization.ProjectConfigurationRevision,
                    authorization.ContextRevision,
                    now);
                var result = new KnowledgeProposalPromotionResult(
                    KnowledgeWriteStatus.Updated,
                    promotedProposal,
                    decision,
                    eventId);
                var stream = new CosmosMemoryDocument(
                    streamDocumentId,
                    projectId,
                    StreamDocumentType,
                    StreamId: streamId,
                    LastSequence: nextSequence);
                var acceptedEffect = new CosmosMemoryDocument(
                    CosmosMemoryDocumentIds.AcceptedEffect(eventId),
                    projectId,
                    AcceptedEffectDocumentType,
                    Receipt: receipt,
                    StreamId: streamId,
                    Sequence: nextSequence);
                var operations = new List<CosmosMemoryBatchOperation>
                {
                    ReplaceRecordOperation(promotedProposal, storedProposal!.ETag),
                    CreateRevisionOperation(promotedProposal, "proposal_promoted"),
                    CreateRecordOperation(decision),
                    CreateRevisionOperation(decision, "created"),
                    storedStream is null
                        ? CreateOperation(stream)
                        : ReplaceOperation(stream, storedStream.ETag),
                    CreateOperation(acceptedEffect),
                    CreateWriteReceiptOperation(
                        projectId, actorFingerprint, idempotencyKey, fingerprint, "promotion", result)
                };
                var batch = await _store.ExecuteBatchAsync(
                    projectId, operations, cancellationToken).ConfigureAwait(false);
                if (batch.Succeeded)
                    return result;
                if (IsConflict(batch))
                {
                    duplicate = await ReadWriteReceiptAsync(
                        projectId, actorFingerprint, idempotencyKey, fingerprint, "promotion",
                        cancellationToken).ConfigureAwait(false);
                    if (duplicate is not null)
                        return Deserialize<KnowledgeProposalPromotionResult>(duplicate.ResultJson)
                            with { IsDuplicate = true };
                    var latest = await ReadRecordDocumentAsync(projectId, proposalId, cancellationToken)
                        .ConfigureAwait(false);
                    if (latest?.Document.Record is not { } latestProposal)
                        return new KnowledgeProposalPromotionResult(
                            KnowledgeWriteStatus.NotFound, null, null, null);
                    if (latestProposal.Revision != expectedRevision)
                        return new KnowledgeProposalPromotionResult(
                            KnowledgeWriteStatus.Stale, null, null, null, CurrentRevision: latestProposal.Revision);
                    if (latestProposal.State != KnowledgeRecordState.Pending ||
                        latestProposal.TrustState != KnowledgeTrustState.Pending)
                        return new KnowledgeProposalPromotionResult(
                            KnowledgeWriteStatus.InvalidState, null, null, null,
                            CurrentRevision: latestProposal.Revision);
                    continue;
                }
                throw new KnowledgeStorageUnavailableException();
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            if (receiptId == Guid.Empty)
                throw new KnowledgeApiException(
                    "invalid_receipt_id",
                    "An accepted-effect receipt ID is required.",
                    StatusCodes.Status400BadRequest);
            var matches = await _store.FindAcceptedEffectAsync(receiptId, cancellationToken).ConfigureAwait(false);
            if (matches.Count == 0)
                return null;
            if (matches.Count != 1)
                throw new KnowledgeStorageUnavailableException();
            return ReadReceipt(matches[0], receiptId);
        }, cancellationToken);

    public Task<AcceptedEffectReceipt?> ReadAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            var state = await ReadDeliveryStateAsync(
                projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
            return state?.Receipt;
        }, cancellationToken);

    public Task<AcceptedEffectDeliveryState?> ReadAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(() => ReadDeliveryStateAsync(
            projectId, runId, receiptId, cancellationToken), cancellationToken);

    public Task<AcceptedEffectDeliveryLease?> ClaimAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateAcceptedEffectScope(projectId, runId, receiptId);
            ValidateIdentifier(workerId, nameof(workerId));
            if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(
                    nameof(leaseDuration), "Lease duration must be positive and at most one hour.");
            var stored = await ReadAcceptedEffectDocumentAsync(projectId, receiptId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null ||
                !string.Equals(stored.Document.Receipt!.RunId, runId, StringComparison.Ordinal) ||
                stored.Document.IsDelivered ||
                (stored.Document.LeasedUntil is { } leasedUntil &&
                    leasedUntil > _timeProvider.GetUtcNow()) ||
                stored.Document.Sequence is not { } sequence ||
                stored.Document.StreamId is not { } streamId)
                return null;
            if (await _store.HasUndeliveredPredecessorAsync(
                    projectId, streamId, sequence, cancellationToken).ConfigureAwait(false))
                return null;

            var token = Guid.NewGuid();
            var updated = stored.Document with
            {
                LeaseToken = token,
                WorkerId = workerId,
                LeasedUntil = _timeProvider.GetUtcNow() + leaseDuration
            };
            var batch = await _store.ExecuteBatchAsync(
                projectId,
                [ReplaceOperation(updated, stored.ETag)],
                cancellationToken).ConfigureAwait(false);
            if (!batch.Succeeded)
            {
                if (IsConflict(batch))
                    return null;
                throw new KnowledgeStorageUnavailableException();
            }
            return new AcceptedEffectDeliveryLease(stored.Document.Receipt, token);
        }, cancellationToken);

    public Task<bool> ReleaseAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default) =>
        UpdateDeliveryLeaseAsync(
            projectId, runId, receiptId, leaseToken, acknowledge: false, cancellationToken);

    public Task<bool> AcknowledgeAcceptedEffectDeliveryAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        CancellationToken cancellationToken = default) =>
        UpdateDeliveryLeaseAsync(
            projectId, runId, receiptId, leaseToken, acknowledge: true, cancellationToken);

    public Task<KnowledgeContextCandidates> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(agentId, nameof(agentId));
            ValidateIdentifier(runId, nameof(runId));
            if (maximumRecords is < 1 or > 10_000)
                throw new KnowledgeApiException(
                    "invalid_context_limit",
                    "The Knowledge context candidate limit is invalid.",
                    StatusCodes.Status500InternalServerError);
            var records = await _store.ReadContextCandidatesAsync(
                projectId, agentId, runId, maximumRecords, cancellationToken).ConfigureAwait(false);
            if (records.Count > maximumRecords)
                throw new KnowledgeContextCandidateLimitException(maximumRecords);
            var ordered = records
                .OrderBy(record => record.Kind switch
                {
                    KnowledgeRecordKind.Decision => 0,
                    KnowledgeRecordKind.Memory => 1,
                    KnowledgeRecordKind.SessionContext => 2,
                    _ => 3
                })
                .ThenByDescending(record => record.Importance, StringComparer.Ordinal)
                .ThenByDescending(record => record.UpdatedAt)
                .ThenBy(record => record.RecordId)
                .ToImmutableArray();
            return new KnowledgeContextCandidates(ordered);
        }, cancellationToken);

    private async Task<AcceptedEffectDeliveryState?> ReadDeliveryStateAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        ValidateAcceptedEffectScope(projectId, runId, receiptId);
        var stored = await ReadAcceptedEffectDocumentAsync(projectId, receiptId, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null ||
            !string.Equals(stored.Document.Receipt!.RunId, runId, StringComparison.Ordinal))
            return null;
        return new AcceptedEffectDeliveryState(stored.Document.Receipt, stored.Document.IsDelivered);
    }

    private async Task<CosmosMemoryStoredDocument?> ReadAcceptedEffectDocumentAsync(
        string projectId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId,
            CosmosMemoryDocumentIds.AcceptedEffect(receiptId),
            cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        if (stored.Document.DocumentType != AcceptedEffectDocumentType ||
            stored.Document.Receipt is null)
            throw new KnowledgeStorageUnavailableException();
        var receipt = ReadReceipt(stored.Document, receiptId);
        if (receipt is null ||
            !string.Equals(receipt.ProjectId, projectId, StringComparison.Ordinal))
            return null;
        return stored;
    }

    private async Task<bool> UpdateDeliveryLeaseAsync(
        string projectId,
        string runId,
        Guid receiptId,
        Guid leaseToken,
        bool acknowledge,
        CancellationToken cancellationToken) =>
        await WithStorageAsync(async () =>
        {
            ValidateAcceptedEffectScope(projectId, runId, receiptId);
            if (leaseToken == Guid.Empty)
                throw new ArgumentException("A delivery lease token is required.", nameof(leaseToken));
            var stored = await ReadAcceptedEffectDocumentAsync(projectId, receiptId, cancellationToken)
                .ConfigureAwait(false);
            if (stored is null ||
                !string.Equals(stored.Document.Receipt!.RunId, runId, StringComparison.Ordinal) ||
                stored.Document.LeaseToken != leaseToken ||
                stored.Document.IsDelivered ||
                (acknowledge &&
                    (stored.Document.LeasedUntil is not { } leasedUntil ||
                        leasedUntil <= _timeProvider.GetUtcNow())))
                return false;
            var updated = acknowledge
                ? stored.Document with
                {
                    IsDelivered = true,
                    LeaseToken = null,
                    WorkerId = null,
                    LeasedUntil = null
                }
                : stored.Document with
                {
                    LeaseToken = null,
                    WorkerId = null,
                    LeasedUntil = null
                };
            var batch = await _store.ExecuteBatchAsync(
                projectId,
                [ReplaceOperation(updated, stored.ETag)],
                cancellationToken).ConfigureAwait(false);
            if (batch.Succeeded)
                return true;
            if (IsConflict(batch))
                return false;
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken).ConfigureAwait(false);

    private async Task<CosmosMemoryStoredDocument?> ReadRecordDocumentAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId, CosmosMemoryDocumentIds.Record(recordId), cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        if (stored.Document.DocumentType != RecordDocumentType ||
            stored.Document.Record?.RecordId != recordId ||
            !string.Equals(stored.Document.Record.ProjectId, projectId, StringComparison.Ordinal))
            throw new KnowledgeStorageUnavailableException();
        return stored;
    }

    private async Task<CosmosMemoryDocument?> ReadWriteReceiptAsync(
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        string fingerprint,
        string resultKind,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId,
            CosmosMemoryDocumentIds.Write(actorFingerprint, idempotencyKey),
            cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        var document = stored.Document;
        if (document.DocumentType != WriteDocumentType ||
            !string.Equals(document.ActorFingerprint, actorFingerprint, StringComparison.Ordinal) ||
            !string.Equals(document.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) ||
            document.ResultJson is null)
            throw new KnowledgeStorageUnavailableException();
        if (!string.Equals(document.RequestFingerprint, fingerprint, StringComparison.Ordinal) ||
            !string.Equals(document.ResultKind, resultKind, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "idempotency_conflict",
                "The idempotency key was already used for a different Knowledge write.",
                StatusCodes.Status409Conflict);
        return document;
    }

    private CosmosMemoryBatchOperation CreateWriteReceiptOperation<T>(
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        string fingerprint,
        string resultKind,
        T result) =>
        CreateOperation(new CosmosMemoryDocument(
            CosmosMemoryDocumentIds.Write(actorFingerprint, idempotencyKey),
            projectId,
            WriteDocumentType,
            ActorFingerprint: actorFingerprint,
            IdempotencyKey: idempotencyKey,
            RequestFingerprint: fingerprint,
            ResultKind: resultKind,
            ResultJson: JsonSerializer.Serialize(result, JsonOptions)));

    private static CosmosMemoryBatchOperation CreateRecordOperation(KnowledgeRecord record) =>
        CreateOperation(new CosmosMemoryDocument(
            CosmosMemoryDocumentIds.Record(record.RecordId),
            record.ProjectId,
            RecordDocumentType,
            Record: record));

    private static CosmosMemoryBatchOperation ReplaceRecordOperation(
        KnowledgeRecord record,
        string etag) =>
        ReplaceOperation(
            new CosmosMemoryDocument(
                CosmosMemoryDocumentIds.Record(record.RecordId),
                record.ProjectId,
                RecordDocumentType,
                Record: record),
            etag);

    private static CosmosMemoryBatchOperation CreateRevisionOperation(
        KnowledgeRecord record,
        string reason)
    {
        var revision = new KnowledgeRecordRevision(
            record.RecordId,
            record.Revision,
            record.RevisionId,
            record.PreviousRevisionId,
            record.Kind,
            record.Type,
            record.Title,
            record.Content,
            record.Rationale,
            record.Importance,
            record.Tags,
            record.State,
            record.TrustState,
            reason,
            record.UpdatedAt);
        return CreateOperation(new CosmosMemoryDocument(
            CosmosMemoryDocumentIds.Revision(record.RecordId, record.Revision),
            record.ProjectId,
            RevisionDocumentType,
            RecordRevision: revision,
            RecordId: record.RecordId,
            Revision: record.Revision));
    }

    private static CosmosMemoryBatchOperation CreateOperation(CosmosMemoryDocument document) =>
        new(CosmosMemoryBatchOperationKind.Create, document);

    private static CosmosMemoryBatchOperation ReplaceOperation(
        CosmosMemoryDocument document,
        string etag) =>
        new(CosmosMemoryBatchOperationKind.Replace, document, etag);

    private static AcceptedEffectReceipt? ReadReceipt(
        CosmosMemoryDocument document,
        Guid receiptId)
    {
        var receipt = document.Receipt;
        if (receipt is null)
            return null;
        if (receipt.ReceiptId != receiptId ||
            receipt.SchemaVersion != AcceptedEffectContractVersions.CurrentSchemaVersion ||
            receipt.EventVersion != AcceptedEffectContractVersions.CurrentEventVersion ||
            receipt.EffectId == Guid.Empty ||
            receipt.RecordId == Guid.Empty ||
            receipt.RecordVersion < 1)
            throw new KnowledgeStorageUnavailableException();
        return receipt;
    }

    private async Task<T> WithStorageAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CosmosException)
        {
            throw new KnowledgeStorageUnavailableException();
        }
    }

    private static bool IsConflict(CosmosMemoryBatchResult result) =>
        result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound;

    private static T Deserialize<T>(string? json) =>
        json is null
            ? throw new KnowledgeStorageUnavailableException()
            : JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new KnowledgeStorageUnavailableException();

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)))
            .ToLowerInvariant();

    private static void ValidateCreate(KnowledgeRecordCreate input, string idempotencyKey)
    {
        ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
        ValidateIdentifier(input.AgentId, nameof(input.AgentId));
        ValidateActorFingerprint(input.ActorFingerprint);
        ValidateIdempotencyKey(idempotencyKey);
        if (!Enum.IsDefined(input.Kind) || input.Kind == KnowledgeRecordKind.Decision ||
            string.IsNullOrWhiteSpace(input.Type) || input.Type.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 40_000 ||
            input.Title?.Length > 512 || input.Rationale?.Length > 8_000 ||
            input.Importance is not ("low" or "medium" or "high") ||
            input.Tags.IsDefault || input.Tags.Length > 32)
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Knowledge record kind, type, content, importance, tags, or length is invalid.",
                StatusCodes.Status400BadRequest);
        ValidateTags(input.Tags);
    }

    private static void ValidateUpdate(KnowledgeRecordUpdate input, string idempotencyKey)
    {
        ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
        ValidateActorFingerprint(input.ActorFingerprint);
        ValidateIdempotencyKey(idempotencyKey);
        if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1 ||
            string.IsNullOrWhiteSpace(input.Type) || input.Type.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > 40_000 ||
            input.Title?.Length > 512 || input.Rationale?.Length > 8_000 ||
            input.Importance is not ("low" or "medium" or "high") ||
            input.Tags.IsDefault || input.Tags.Length > 32)
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Knowledge record update fields or expected revision are invalid.",
                StatusCodes.Status400BadRequest);
        ValidateTags(input.Tags);
    }

    private static void ValidateTags(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl))
                throw new KnowledgeApiException(
                    "invalid_knowledge_record",
                    "Knowledge tags must be nonempty values of at most 64 characters without control characters.",
                    StatusCodes.Status400BadRequest);
    }

    private static ImmutableArray<string> NormalizeTags(ImmutableArray<string> tags) =>
        tags.Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new KnowledgeApiException(
                "invalid_page",
                "Knowledge history page must be positive and page size must be between 1 and 100.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateActorFingerprint(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new KnowledgeApiException(
                "invalid_actor_fingerprint",
                "A validated actor fingerprint is required.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateAuthorizationBounds(
        string projectId,
        string runId,
        AcceptedEffectAuthorizationBounds authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!Uri.TryCreate(authorization.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps ||
            !IsReceiptIdentifier(authorization.Subject) ||
            !IsReceiptIdentifier(authorization.TenantId) ||
            (authorization.BoundProjectId is not null &&
                !string.Equals(authorization.BoundProjectId, projectId, StringComparison.Ordinal)) ||
            (authorization.BoundRunId is not null &&
                !string.Equals(authorization.BoundRunId, runId, StringComparison.Ordinal)) ||
            (authorization.BoundRunId is not null && authorization.BoundProjectId is null) ||
            !Enum.IsDefined(authorization.AuthorizationResourceType) ||
            (authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Project &&
                !string.Equals(authorization.AuthorizationResourceId, projectId, StringComparison.Ordinal)) ||
            (authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Tenant &&
                !string.Equals(authorization.AuthorizationResourceId, authorization.TenantId, StringComparison.Ordinal)) ||
            authorization.AuthorizationResourceType == ProjectAuthorityResourceType.Platform ||
            !IsReceiptIdentifier(authorization.AuthorizationResourceId) ||
            authorization.AuthorizationRevision < 1 ||
            authorization.MembershipRevision < 1 ||
            authorization.ProjectRevision < 1 ||
            authorization.ProjectConfigurationRevision < 1 ||
            !IsReceiptIdentifier(authorization.ContextRevision))
            throw new KnowledgeApiException(
                "invalid_accepted_effect_authority",
                "Validated project and run authorization bounds are required for accepted effects.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateAcceptedEffectScope(string projectId, string runId, Guid receiptId)
    {
        ValidateIdentifier(projectId, nameof(projectId));
        ValidateIdentifier(runId, nameof(runId));
        if (receiptId == Guid.Empty)
            throw new KnowledgeApiException(
                "invalid_receipt_id",
                "An accepted-effect receipt ID is required.",
                StatusCodes.Status400BadRequest);
    }

    private static bool IsReceiptIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new KnowledgeApiException(
                "idempotency_key_required",
                "A valid Idempotency-Key header is required for Knowledge writes.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new KnowledgeApiException(
                "invalid_identifier",
                $"The {name} value is invalid.",
                StatusCodes.Status400BadRequest);
    }
}

internal static class CosmosMemoryDocumentIds
{
    public static string Record(Guid recordId) => $"record:{recordId:N}";

    public static string Revision(Guid recordId, int revision) =>
        $"revision:{recordId:N}:{revision:D10}";

    public static string Write(string actorFingerprint, string idempotencyKey) =>
        $"write:{Hash($"{actorFingerprint}\n{idempotencyKey}")}";

    public static string Stream(string streamId) => $"stream:{Hash(streamId)}";

    public static string AcceptedEffect(Guid receiptId) => $"accepted-effect:{receiptId:N}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
