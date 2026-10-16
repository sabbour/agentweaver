using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Knowledge;

public abstract class KnowledgeDocumentMemoryProviderCore : IMemoryProvider
{
    private const string RecordDocumentType = "record";
    private const string RevisionDocumentType = "revision";
    private const string WriteDocumentType = "write";
    private const string StreamDocumentType = "stream";
    private const string AcceptedEffectDocumentType = "accepted-effect";
    private const string DecisionGraphDocumentType = "decision-graph";
    private const int MaximumBatchRetries = 5;
    private const int MaximumDecisionChainLength = 64;
    private const int MaximumTransactionalBatchOperations = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IKnowledgeMemoryDocumentStore _store;
    private readonly TimeProvider _timeProvider;

    public KnowledgeDocumentMemoryProviderCore(
        IKnowledgeMemoryDocumentStore store,
        ProviderDescriptor descriptor,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(descriptor);
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = descriptor;
    }

    public ProviderDescriptor Descriptor { get; }

    public abstract Task<ResourceNegotiation> NegotiateAsync(
        ProviderCandidate candidate,
        CancellationToken cancellationToken = default);

    protected virtual bool RequiresStoreLeaseExpiryCheck => false;

    protected abstract bool IsStorageFailure(Exception exception);

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
                        normalized.Kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created",
                        normalized.ActorFingerprint,
                        normalized.Kind == KnowledgeRecordKind.Proposal ? "proposal created" : "created"),
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
            for (var attempt = 0; attempt < MaximumBatchRetries; attempt++)
            {
                var duplicate = await ReadWriteReceiptAsync(
                    normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };

                var latest = await ReadRecordDocumentAsync(
                    normalized.ProjectId, normalized.RecordId, cancellationToken).ConfigureAwait(false);
                var current = latest?.Document.Record;
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

                var isDecision = current.Kind == KnowledgeRecordKind.Decision;
                if (current.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.SessionContext or
                        KnowledgeRecordKind.Decision) ||
                    (isDecision
                        ? normalized.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived or
                            KnowledgeRecordState.Superseded)
                        : normalized.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived)) ||
                    (!isDecision && normalized.SupersededByRecordId is not null) ||
                    (isDecision &&
                        (current.State == KnowledgeRecordState.Superseded ||
                         (current.State == KnowledgeRecordState.Archived &&
                          normalized.State == KnowledgeRecordState.Active))))
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

                MemoryStoredDocument? graph = null;
                if (isDecision)
                {
                    KnowledgeTransferValidator.ValidateSupersessionState(
                        current.Kind, normalized.State, normalized.SupersededByRecordId, "invalid_replacement");
                    if (normalized.State == KnowledgeRecordState.Superseded &&
                        current.State != KnowledgeRecordState.Active)
                        return new KnowledgeRecordWriteResult(
                            KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);
                    graph = await ReadDecisionGraphDocumentAsync(normalized.ProjectId, cancellationToken)
                        .ConfigureAwait(false);
                    if (normalized.SupersededByRecordId is { } replacementId)
                    {
                        var replacementStatus = await ValidateDecisionReplacementAsync(
                            normalized.ProjectId, current.AgentId, normalized.RecordId, replacementId,
                            cancellationToken)
                            .ConfigureAwait(false);
                        if (replacementStatus is not null)
                            return new KnowledgeRecordWriteResult(
                                replacementStatus.Value, null, CurrentRevision: current.Revision);
                    }
                }

                var contentChanged = current.Type != normalized.Type ||
                    current.Title != normalized.Title ||
                    current.Content != normalized.Content ||
                    current.Rationale != normalized.Rationale ||
                    current.Importance != normalized.Importance ||
                    !current.Tags.SequenceEqual(normalized.Tags, StringComparer.Ordinal);
                var trustState = current.Kind == KnowledgeRecordKind.SessionContext
                    ? current.TrustState
                    : isDecision
                        ? current.Content != normalized.Content || current.Rationale != normalized.Rationale
                            ? KnowledgeTrustState.Pending
                            : current.TrustState
                        : contentChanged ? KnowledgeTrustState.Pending : current.TrustState;
                var changeKind = isDecision
                    ? normalized.State switch
                    {
                        KnowledgeRecordState.Archived when current.State != KnowledgeRecordState.Archived =>
                            "decision_archived",
                        KnowledgeRecordState.Superseded => "decision_superseded",
                        _ => "updated"
                    }
                    : "updated";
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
                    UpdatedAt = _timeProvider.GetUtcNow(),
                    SupersededByRecordId = normalized.SupersededByRecordId
                };
                var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
                var operations = new List<MemoryBatchOperation>
                {
                    ReplaceRecordOperation(updated, latest!.ETag),
                    CreateRevisionOperation(
                        updated, changeKind, normalized.ActorFingerprint,
                        normalized.Reason ?? changeKind.Replace('_', ' '))
                };
                if (isDecision)
                    operations.Add(AdvanceDecisionGraphOperation(normalized.ProjectId, graph));
                operations.Add(CreateWriteReceiptOperation(
                    normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record", result));
                var batch = await _store.ExecuteBatchAsync(
                    normalized.ProjectId, operations, cancellationToken).ConfigureAwait(false);
                if (batch.Succeeded)
                    return result;
                if (!IsConflict(batch))
                    throw new KnowledgeStorageUnavailableException();

                duplicate = await ReadWriteReceiptAsync(
                    normalized.ProjectId, normalized.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                if (!isDecision)
                {
                    var afterConflict = await ReadRecordDocumentAsync(
                        normalized.ProjectId, normalized.RecordId, cancellationToken).ConfigureAwait(false);
                    return afterConflict?.Document.Record is { } afterConflictRecord
                    ? new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: afterConflictRecord.Revision)
                    : new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                }
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> RestoreAsync(
        KnowledgeRecordRestore input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateActorFingerprint(input.ActorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1 || input.Revision < 1)
                throw new KnowledgeApiException(
                    "invalid_restore_revision",
                    "A record ID and positive current and historical revisions are required.",
                    StatusCodes.Status400BadRequest);

            var fingerprint = Fingerprint(input);
            for (var attempt = 0; attempt < MaximumBatchRetries; attempt++)
            {
                var duplicate = await ReadWriteReceiptAsync(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };

                var stored = await ReadRecordDocumentAsync(
                    input.ProjectId, input.RecordId, cancellationToken).ConfigureAwait(false);
                var current = stored?.Document.Record;
                if (current is null)
                    return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                if (current.Revision != input.ExpectedRevision)
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
                if (current.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.Decision))
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

                var historical = await ReadRevisionSnapshotAsync(
                    input.ProjectId, input.RecordId, input.Revision, cancellationToken).ConfigureAwait(false);
                if (historical is null)
                    return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                var updated = current with
                {
                    Type = historical.Type,
                    Title = historical.Title,
                    Content = historical.Content,
                    Rationale = historical.Rationale,
                    Importance = historical.Importance,
                    Tags = historical.Tags,
                    State = KnowledgeRecordState.Active,
                    TrustState = KnowledgeTrustState.Pending,
                    Revision = checked(current.Revision + 1),
                    PreviousRevisionId = current.RevisionId,
                    RevisionId = Guid.NewGuid(),
                    UpdatedAt = _timeProvider.GetUtcNow(),
                    SupersededByRecordId = null
                };
                var changeKind = current.Kind == KnowledgeRecordKind.Decision
                    ? "decision_restored"
                    : "updated";
                var reason = input.Reason ?? $"restored revision {input.Revision}";
                var operations = new List<MemoryBatchOperation>
                {
                    ReplaceRecordOperation(updated, stored!.ETag),
                    CreateRevisionOperation(updated, changeKind, input.ActorFingerprint, reason)
                };
                if (current.Kind == KnowledgeRecordKind.Decision)
                {
                    var graph = await ReadDecisionGraphDocumentAsync(input.ProjectId, cancellationToken)
                        .ConfigureAwait(false);
                    operations.Add(AdvanceDecisionGraphOperation(input.ProjectId, graph));
                }
                var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, updated);
                operations.Add(CreateWriteReceiptOperation(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record", result));
                var batch = await _store.ExecuteBatchAsync(input.ProjectId, operations, cancellationToken)
                    .ConfigureAwait(false);
                if (batch.Succeeded)
                    return result;
                if (!IsConflict(batch))
                    throw new KnowledgeStorageUnavailableException();
                duplicate = await ReadWriteReceiptAsync(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                if (current.Kind != KnowledgeRecordKind.Decision)
                {
                    var latest = await ReadRecordDocumentAsync(
                        input.ProjectId, input.RecordId, cancellationToken).ConfigureAwait(false);
                    return latest?.Document.Record is { } latestRecord
                        ? new KnowledgeRecordWriteResult(
                            KnowledgeWriteStatus.Stale, null, CurrentRevision: latestRecord.Revision)
                        : new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                }
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeRecordWriteResult> ApproveDecisionAsync(
        KnowledgeDecisionApproval input,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateActorFingerprint(input.ActorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            if (input.RecordId == Guid.Empty || input.ExpectedRevision < 1)
                throw new KnowledgeApiException(
                    "invalid_decision_revision",
                    "A Decision ID and positive expected revision are required.",
                    StatusCodes.Status400BadRequest);
            var fingerprint = Fingerprint(input);
            for (var attempt = 0; attempt < MaximumBatchRetries; attempt++)
            {
                var duplicate = await ReadWriteReceiptAsync(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                var stored = await ReadRecordDocumentAsync(
                    input.ProjectId, input.RecordId, cancellationToken).ConfigureAwait(false);
                var current = stored?.Document.Record;
                if (current is null || current.Kind != KnowledgeRecordKind.Decision)
                    return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                if (current.Revision != input.ExpectedRevision)
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: current.Revision);
                if (current.State != KnowledgeRecordState.Active ||
                    current.TrustState is not (KnowledgeTrustState.Pending or KnowledgeTrustState.Legacy))
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.InvalidState, null, CurrentRevision: current.Revision);

                var approved = current with
                {
                    TrustState = KnowledgeTrustState.Approved,
                    Revision = checked(current.Revision + 1),
                    PreviousRevisionId = current.RevisionId,
                    RevisionId = Guid.NewGuid(),
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
                var result = new KnowledgeRecordWriteResult(KnowledgeWriteStatus.Updated, approved);
                var batch = await _store.ExecuteBatchAsync(
                    input.ProjectId,
                    [
                        ReplaceRecordOperation(approved, stored!.ETag),
                        CreateRevisionOperation(
                            approved, "decision_approved", input.ActorFingerprint,
                            input.Reason ?? "approved"),
                        CreateWriteReceiptOperation(
                            input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record", result)
                    ],
                    cancellationToken).ConfigureAwait(false);
                if (batch.Succeeded)
                    return result;
                if (!IsConflict(batch))
                    throw new KnowledgeStorageUnavailableException();
                duplicate = await ReadWriteReceiptAsync(
                    input.ProjectId, input.ActorFingerprint, idempotencyKey, fingerprint, "record",
                    cancellationToken).ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordWriteResult>(duplicate.ResultJson) with { IsDuplicate = true };
                var latest = await ReadRecordDocumentAsync(
                    input.ProjectId, input.RecordId, cancellationToken).ConfigureAwait(false);
                if (latest?.Document.Record is not { } latestRecord)
                    return new KnowledgeRecordWriteResult(KnowledgeWriteStatus.NotFound, null);
                if (latestRecord.Revision != input.ExpectedRevision)
                    return new KnowledgeRecordWriteResult(
                        KnowledgeWriteStatus.Stale, null, CurrentRevision: latestRecord.Revision);
            }
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken);

    public Task<KnowledgeRecordTransferBundle> ExportAsync(
        string projectId,
        string agentId,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ValidateIdentifier(projectId, nameof(projectId));
            ValidateIdentifier(agentId, nameof(agentId));
            var records = await _store.ReadTransferCandidatesAsync(
                projectId, agentId, KnowledgeRecordTransferContract.MaximumRecords, cancellationToken)
                .ConfigureAwait(false);
            if (records.Count > KnowledgeRecordTransferContract.MaximumRecords)
                throw KnowledgeTransferValidator.TooLarge(
                    "The Knowledge export record count exceeds the supported bound.");

            var histories = ImmutableArray.CreateBuilder<KnowledgeRecordTransferEntry>(records.Count);
            var revisionCount = 0;
            foreach (var record in records
                         .OrderBy(item => item.Kind)
                         .ThenBy(item => item.AgentId, StringComparer.Ordinal)
                         .ThenBy(item => item.RecordId))
            {
                var firstPage = await _store.ReadRevisionsAsync(
                    projectId, record.RecordId, 1, 100, cancellationToken).ConfigureAwait(false);
                if (firstPage.TotalCount > KnowledgeRecordTransferContract.MaximumRevisions - revisionCount)
                    throw KnowledgeTransferValidator.TooLarge(
                        "The Knowledge export revision count exceeds the supported bound.");
                var revisions = new List<KnowledgeRecordRevision>(firstPage.TotalCount);
                revisions.AddRange(firstPage.Items);
                var pageCount = (firstPage.TotalCount + firstPage.PageSize - 1) / firstPage.PageSize;
                for (var page = 2; page <= pageCount; page++)
                {
                    var nextPage = await _store.ReadRevisionsAsync(
                        projectId, record.RecordId, page, 100, cancellationToken).ConfigureAwait(false);
                    if (nextPage.TotalCount != firstPage.TotalCount ||
                        nextPage.PageSize != firstPage.PageSize ||
                        nextPage.Page != page)
                        throw IncompleteExport();
                    revisions.AddRange(nextPage.Items);
                }
                if (revisions.Count != firstPage.TotalCount)
                    throw IncompleteExport();
                revisionCount = checked(revisionCount + revisions.Count);
                histories.Add(new KnowledgeRecordTransferEntry(
                    record,
                    revisions.OrderBy(item => item.Revision).ToImmutableArray()));
            }
            EnsureTransferFitsMemoryBatch(records.Count, revisionCount);
            var bundle = new KnowledgeRecordTransferBundle(
                KnowledgeRecordTransferContract.Format,
                KnowledgeRecordTransferContract.SchemaVersion,
                projectId,
                agentId,
                histories.ToImmutable());
            KnowledgeTransferValidator.Validate(
                bundle,
                projectId,
                agentId,
                "knowledge_transfer_incomplete",
                StatusCodes.Status409Conflict,
                allowEmpty: true);
            return bundle;
        }, cancellationToken);

    public Task<KnowledgeRecordImportResult> ImportAsync(
        KnowledgeRecordTransferBundle input,
        string runId,
        string actorFingerprint,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        WithStorageAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateIdentifier(input.ProjectId, nameof(input.ProjectId));
            ValidateIdentifier(input.AgentId, nameof(input.AgentId));
            ValidateIdentifier(runId, nameof(runId));
            ValidateActorFingerprint(actorFingerprint);
            ValidateIdempotencyKey(idempotencyKey);
            KnowledgeTransferValidator.Validate(input, input.ProjectId, input.AgentId);
            var revisionCount = input.Records.Sum(entry => entry.Revisions.Length);
            EnsureTransferFitsMemoryBatch(input.Records.Length, revisionCount);
            var fingerprint = Fingerprint(new { input, runId });
            var projectId = input.ProjectId;
            var resultRecordId = input.Records[0].Record.RecordId;

            for (var attempt = 0; attempt < MaximumBatchRetries; attempt++)
            {
                var duplicate = await ReadWriteReceiptAsync(
                    projectId, actorFingerprint, idempotencyKey, fingerprint, "transfer", cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordImportResult>(duplicate.ResultJson) with { IsDuplicate = true };

                var graph = await ReadDecisionGraphDocumentAsync(projectId, cancellationToken).ConfigureAwait(false);
                foreach (var entry in input.Records)
                {
                    if (await ReadRecordDocumentAsync(
                            projectId, entry.Record.RecordId, cancellationToken).ConfigureAwait(false) is not null)
                        throw TransferConflict(
                            "A transferred record ID already exists; import does not merge record heads.");
                }
                var revisionIds = input.Records
                    .SelectMany(entry => entry.Revisions)
                    .Select(revision => revision.RevisionId)
                    .ToArray();
                if ((await _store.FindRevisionIdsAsync(projectId, revisionIds, cancellationToken)
                        .ConfigureAwait(false)).Count > 0)
                    throw TransferConflict(
                        "A transferred revision ID already exists; import will not overwrite history.");
                await ValidateTransferReplacementGraphAsync(input, cancellationToken).ConfigureAwait(false);

                var now = _timeProvider.GetUtcNow();
                var importedRecords = ImmutableArray.CreateBuilder<KnowledgeRecord>(input.Records.Length);
                var operations = new List<MemoryBatchOperation>();
                foreach (var entry in input.Records)
                {
                    var source = entry.Record;
                    var imported = source with
                    {
                        State = KnowledgeRecordState.Active,
                        Revision = checked(source.Revision + 1),
                        PreviousRevisionId = source.RevisionId,
                        RevisionId = Guid.NewGuid(),
                        TrustState = KnowledgeTrustState.Pending,
                        SourceRunId = runId,
                        SourceSessionId = null,
                        UpdatedAt = now,
                        SupersededByRecordId = null
                    };
                    operations.Add(CreateRecordOperation(imported));
                    foreach (var revision in entry.Revisions)
                        operations.Add(CreateRevisionSnapshotOperation(revision, projectId));
                    operations.Add(CreateRevisionOperation(
                        imported, "imported", actorFingerprint,
                        "imported from an authorized Knowledge transfer"));
                    importedRecords.Add(imported);
                }

                var result = new KnowledgeRecordImportResult(importedRecords.ToImmutable());
                operations.Add(AdvanceDecisionGraphOperation(projectId, graph));
                operations.Add(CreateWriteReceiptOperation(
                    projectId, actorFingerprint, idempotencyKey, fingerprint, "transfer", result));
                var batch = await _store.ExecuteBatchAsync(projectId, operations, cancellationToken)
                    .ConfigureAwait(false);
                if (batch.Succeeded)
                    return result;
                if (!IsConflict(batch))
                    throw new KnowledgeStorageUnavailableException();
                duplicate = await ReadWriteReceiptAsync(
                    projectId, actorFingerprint, idempotencyKey, fingerprint, "transfer", cancellationToken)
                    .ConfigureAwait(false);
                if (duplicate is not null)
                    return Deserialize<KnowledgeRecordImportResult>(duplicate.ResultJson) with { IsDuplicate = true };
            }
            throw TransferConflict(
                "The Knowledge transfer changed during import. Retry with the same idempotency key.");
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
                    CreateRevisionOperation(
                        updated, "proposal_rejected", actorFingerprint, "proposal rejected"),
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
            var streamDocumentId = KnowledgeMemoryDocumentIds.Stream(streamId);
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
                var stream = new KnowledgeMemoryDocument(
                    streamDocumentId,
                    projectId,
                    StreamDocumentType,
                    StreamId: streamId,
                    LastSequence: nextSequence);
                var acceptedEffect = new KnowledgeMemoryDocument(
                    KnowledgeMemoryDocumentIds.AcceptedEffect(eventId),
                    projectId,
                    AcceptedEffectDocumentType,
                    Receipt: receipt,
                    StreamId: streamId,
                    Sequence: nextSequence);
                var operations = new List<MemoryBatchOperation>
                {
                    ReplaceRecordOperation(promotedProposal, storedProposal!.ETag),
                    CreateRevisionOperation(
                        promotedProposal, "proposal_promoted", actorFingerprint, "proposal promoted"),
                    CreateRecordOperation(decision),
                    CreateRevisionOperation(decision, "created", actorFingerprint, "created"),
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
                (!RequiresStoreLeaseExpiryCheck &&
                    stored.Document.LeasedUntil is { } leasedUntil &&
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
            var operation = ReplaceOperation(updated, stored.ETag);
            if (RequiresStoreLeaseExpiryCheck)
                operation = operation with
                {
                    LeaseMutation = new MemoryLeaseMutation(
                        MemoryLeaseMutationKind.Claim,
                        token,
                        checked((long)Math.Ceiling(leaseDuration.TotalMilliseconds)))
                };
            var batch = await _store.ExecuteBatchAsync(
                projectId,
                [operation],
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

    private async Task<MemoryStoredDocument?> ReadAcceptedEffectDocumentAsync(
        string projectId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId,
            KnowledgeMemoryDocumentIds.AcceptedEffect(receiptId),
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
                (acknowledge && !RequiresStoreLeaseExpiryCheck &&
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
            var operation = ReplaceOperation(updated, stored.ETag);
            if (RequiresStoreLeaseExpiryCheck)
                operation = operation with
                {
                    LeaseMutation = new MemoryLeaseMutation(
                        acknowledge
                            ? MemoryLeaseMutationKind.Acknowledge
                            : MemoryLeaseMutationKind.Release,
                        leaseToken)
                };
            var batch = await _store.ExecuteBatchAsync(
                projectId,
                [operation],
                cancellationToken).ConfigureAwait(false);
            if (batch.Succeeded)
                return true;
            if (IsConflict(batch))
                return false;
            throw new KnowledgeStorageUnavailableException();
        }, cancellationToken).ConfigureAwait(false);

    private async Task<MemoryStoredDocument?> ReadDecisionGraphDocumentAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId, KnowledgeMemoryDocumentIds.DecisionGraph(), cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        if (stored.Document.DocumentType != DecisionGraphDocumentType ||
            stored.Document.LastSequence is not > 0)
            throw new KnowledgeStorageUnavailableException();
        return stored;
    }

    private static MemoryBatchOperation AdvanceDecisionGraphOperation(
        string projectId,
        MemoryStoredDocument? stored)
    {
        var document = new KnowledgeMemoryDocument(
            KnowledgeMemoryDocumentIds.DecisionGraph(),
            projectId,
            DecisionGraphDocumentType,
            LastSequence: checked((stored?.Document.LastSequence ?? 0) + 1));
        return stored is null
            ? CreateOperation(document)
            : ReplaceOperation(document, stored.ETag);
    }

    private async Task<KnowledgeRecordRevision?> ReadRevisionSnapshotAsync(
        string projectId,
        Guid recordId,
        int revision,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var firstPage = await _store.ReadRevisionsAsync(
            projectId, recordId, 1, pageSize, cancellationToken).ConfigureAwait(false);
        if (revision > firstPage.TotalCount)
            return null;
        var pageNumber = ((firstPage.TotalCount - revision) / pageSize) + 1;
        var page = pageNumber == 1
            ? firstPage
            : await _store.ReadRevisionsAsync(
                projectId, recordId, pageNumber, pageSize, cancellationToken).ConfigureAwait(false);
        if (page.TotalCount != firstPage.TotalCount ||
            page.Page != pageNumber ||
            page.PageSize != pageSize)
            throw IncompleteExport();
        return page.Items.SingleOrDefault(item => item.Revision == revision);
    }

    private async Task<KnowledgeWriteStatus?> ValidateDecisionReplacementAsync(
        string projectId,
        string sourceAgentId,
        Guid sourceRecordId,
        Guid replacementRecordId,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { sourceRecordId };
        var currentId = replacementRecordId;
        for (var depth = 0; depth < MaximumDecisionChainLength; depth++)
        {
            if (!visited.Add(currentId))
                return KnowledgeWriteStatus.ReplacementCycle;
            var current = (await ReadRecordDocumentAsync(projectId, currentId, cancellationToken)
                    .ConfigureAwait(false))?.Document.Record;
            if (current is null || current.Kind != KnowledgeRecordKind.Decision ||
                !string.Equals(current.AgentId, sourceAgentId, StringComparison.Ordinal))
                return KnowledgeWriteStatus.InvalidReplacement;
            if (current.State == KnowledgeRecordState.Superseded)
            {
                if (current.SupersededByRecordId is not { } next)
                    return KnowledgeWriteStatus.InvalidReplacement;
                currentId = next;
                continue;
            }
            return current.State is KnowledgeRecordState.Active or KnowledgeRecordState.Archived
                ? null
                : KnowledgeWriteStatus.InvalidReplacement;
        }
        throw new KnowledgeApiException(
            "supersession_chain_too_long",
            "The Decision supersession chain exceeds the supported traversal bound.",
            StatusCodes.Status409Conflict);
    }

    private async Task ValidateTransferReplacementGraphAsync(
        KnowledgeRecordTransferBundle bundle,
        CancellationToken cancellationToken)
    {
        var included = bundle.Records.ToDictionary(entry => entry.Record.RecordId);
        var referencedIds = bundle.Records
            .SelectMany(entry => entry.Revisions
                .Where(revision => revision.SupersededByRecordId is not null)
                .Select(revision => revision.SupersededByRecordId!.Value))
            .Distinct();
        foreach (var targetId in referencedIds)
        {
            if (included.TryGetValue(targetId, out var includedTarget))
            {
                if (includedTarget.Record.Kind != KnowledgeRecordKind.Decision ||
                    !string.Equals(includedTarget.Record.AgentId, bundle.AgentId, StringComparison.Ordinal))
                    throw InvalidReplacement(
                        "A supersession link must target a Decision in the transferred agent scope.");
                continue;
            }
            var target = (await ReadRecordDocumentAsync(bundle.ProjectId, targetId, cancellationToken)
                    .ConfigureAwait(false))?.Document.Record;
            if (target?.Kind != KnowledgeRecordKind.Decision ||
                !string.Equals(target.AgentId, bundle.AgentId, StringComparison.Ordinal))
                throw InvalidReplacement(
                    "A supersession target is missing or outside the transferred project and agent scope.");
        }

        foreach (var source in bundle.Records
                     .Where(entry => entry.Record.State == KnowledgeRecordState.Superseded))
        {
            var visited = new HashSet<Guid> { source.Record.RecordId };
            var currentId = source.Record.SupersededByRecordId!.Value;
            var reachedTerminal = false;
            for (var depth = 0; depth < MaximumDecisionChainLength; depth++)
            {
                if (!visited.Add(currentId))
                    throw ReplacementCycle();
                var current = included.TryGetValue(currentId, out var entry)
                    ? entry.Record
                    : (await ReadRecordDocumentAsync(bundle.ProjectId, currentId, cancellationToken)
                        .ConfigureAwait(false))?.Document.Record;
                if (current is null || current.Kind != KnowledgeRecordKind.Decision ||
                    !string.Equals(current.AgentId, bundle.AgentId, StringComparison.Ordinal))
                    throw InvalidReplacement(
                        "A supersession target is missing or outside the transferred project and agent scope.");
                if (current.State == KnowledgeRecordState.Superseded)
                {
                    if (current.SupersededByRecordId is not { } next)
                        throw InvalidReplacement("A superseded Decision has no replacement link.");
                    currentId = next;
                    continue;
                }
                if (current.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived))
                    throw InvalidReplacement("A supersession target has an unsupported state.");
                reachedTerminal = true;
                break;
            }
            if (!reachedTerminal)
                throw new KnowledgeApiException(
                    "supersession_chain_too_long",
                    "The Decision supersession chain exceeds the supported traversal bound.",
                    StatusCodes.Status409Conflict);
        }
    }

    private static void EnsureTransferFitsMemoryBatch(int recordCount, int revisionCount)
    {
        var operations = checked((long)recordCount * 2 + revisionCount + 2);
        if (operations > MaximumTransactionalBatchOperations)
            throw KnowledgeTransferValidator.TooLarge(
                "The Knowledge transfer exceeds the atomic Memory-provider batch operation limit.");
    }

    private static KnowledgeApiException IncompleteExport() =>
        new(
            "knowledge_transfer_incomplete",
            "A complete, consistent Knowledge revision chain could not be exported.",
            StatusCodes.Status409Conflict);

    private static KnowledgeApiException TransferConflict(string detail) =>
        new("knowledge_transfer_conflict", detail, StatusCodes.Status409Conflict);

    private static KnowledgeApiException InvalidReplacement(string detail) =>
        new("invalid_replacement", detail, StatusCodes.Status409Conflict);

    private static KnowledgeApiException ReplacementCycle() =>
        new(
            "replacement_cycle",
            "A Decision cannot be superseded by itself or by a Decision that leads back to it.",
            StatusCodes.Status409Conflict);

    private async Task<MemoryStoredDocument?> ReadRecordDocumentAsync(
        string projectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId, KnowledgeMemoryDocumentIds.Record(recordId), cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        if (stored.Document.DocumentType != RecordDocumentType ||
            stored.Document.Record?.RecordId != recordId ||
            !string.Equals(stored.Document.Record.ProjectId, projectId, StringComparison.Ordinal))
            throw new KnowledgeStorageUnavailableException();
        return stored;
    }

    private async Task<KnowledgeMemoryDocument?> ReadWriteReceiptAsync(
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        string fingerprint,
        string resultKind,
        CancellationToken cancellationToken)
    {
        var stored = await _store.ReadAsync(
            projectId,
            KnowledgeMemoryDocumentIds.Write(actorFingerprint, idempotencyKey),
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

    private MemoryBatchOperation CreateWriteReceiptOperation<T>(
        string projectId,
        string actorFingerprint,
        string idempotencyKey,
        string fingerprint,
        string resultKind,
        T result) =>
        CreateOperation(new KnowledgeMemoryDocument(
            KnowledgeMemoryDocumentIds.Write(actorFingerprint, idempotencyKey),
            projectId,
            WriteDocumentType,
            ActorFingerprint: actorFingerprint,
            IdempotencyKey: idempotencyKey,
            RequestFingerprint: fingerprint,
            ResultKind: resultKind,
            ResultJson: JsonSerializer.Serialize(result, JsonOptions)));

    private static MemoryBatchOperation CreateRecordOperation(KnowledgeRecord record) =>
        CreateOperation(new KnowledgeMemoryDocument(
            KnowledgeMemoryDocumentIds.Record(record.RecordId),
            record.ProjectId,
            RecordDocumentType,
            Record: record));

    private static MemoryBatchOperation ReplaceRecordOperation(
        KnowledgeRecord record,
        string etag) =>
        ReplaceOperation(
            new KnowledgeMemoryDocument(
                KnowledgeMemoryDocumentIds.Record(record.RecordId),
                record.ProjectId,
                RecordDocumentType,
                Record: record),
            etag);

    private static MemoryBatchOperation CreateRevisionOperation(
        KnowledgeRecord record,
        string changeKind,
        string actorFingerprint,
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
            record.UpdatedAt,
            record.SupersededByRecordId,
            record.SourceRunId,
            record.SourceSessionId,
            actorFingerprint,
            changeKind);
        return CreateOperation(new KnowledgeMemoryDocument(
            KnowledgeMemoryDocumentIds.Revision(record.RecordId, record.Revision),
            record.ProjectId,
            RevisionDocumentType,
            RecordRevision: revision,
            RecordId: record.RecordId,
            Revision: record.Revision));
    }

    private static MemoryBatchOperation CreateRevisionSnapshotOperation(
        KnowledgeRecordRevision revision,
        string projectId) =>
        CreateOperation(new KnowledgeMemoryDocument(
            KnowledgeMemoryDocumentIds.Revision(revision.RecordId, revision.Revision),
            projectId,
            RevisionDocumentType,
            RecordRevision: revision,
            RecordId: revision.RecordId,
            Revision: revision.Revision));

    private static MemoryBatchOperation CreateOperation(KnowledgeMemoryDocument document) =>
        new(MemoryBatchOperationKind.Create, document);

    private static MemoryBatchOperation ReplaceOperation(
        KnowledgeMemoryDocument document,
        string etag) =>
        new(MemoryBatchOperationKind.Replace, document, etag);

    private static AcceptedEffectReceipt? ReadReceipt(
        KnowledgeMemoryDocument document,
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
        catch (KnowledgeStorageUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new KnowledgeStorageUnavailableException();
        }
    }

    private static bool IsConflict(MemoryBatchResult result) =>
        result.Status == MemoryBatchStatus.Conflict;

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
            input.Reason?.Length > 1_024 || input.Reason?.Any(char.IsControl) == true ||
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
            input.Reason?.Length > 1_024 || input.Reason?.Any(char.IsControl) == true ||
            input.SupersededByRecordId == Guid.Empty ||
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

internal static class KnowledgeMemoryDocumentIds
{
    public static string Record(Guid recordId) => $"record:{recordId:N}";

    public static string Revision(Guid recordId, int revision) =>
        $"revision:{recordId:N}:{revision:D10}";

    public static string Write(string actorFingerprint, string idempotencyKey) =>
        $"write:{Hash($"{actorFingerprint}\n{idempotencyKey}")}";

    public static string Stream(string streamId) => $"stream:{Hash(streamId)}";

    public static string AcceptedEffect(Guid receiptId) => $"accepted-effect:{receiptId:N}";

    public static string DecisionGraph() => "decision-graph";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
