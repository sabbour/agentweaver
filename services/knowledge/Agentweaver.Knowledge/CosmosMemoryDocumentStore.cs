using System.Collections.Immutable;
using System.Net;
using Agentweaver.Abstractions;
using Microsoft.Azure.Cosmos;

namespace Agentweaver.Knowledge;

public sealed record KnowledgeMemoryDocument(
    string Id,
    string ProjectId,
    string DocumentType,
    KnowledgeRecord? Record = null,
    KnowledgeRecordRevision? RecordRevision = null,
    Guid? RecordId = null,
    int? Revision = null,
    string? ActorFingerprint = null,
    string? IdempotencyKey = null,
    string? RequestFingerprint = null,
    string? ResultKind = null,
    string? ResultJson = null,
    AcceptedEffectReceipt? Receipt = null,
    string? StreamId = null,
    long? Sequence = null,
    long? LastSequence = null,
    bool IsDelivered = false,
    Guid? LeaseToken = null,
    string? WorkerId = null,
    DateTimeOffset? LeasedUntil = null);

public sealed record MemoryStoredDocument(KnowledgeMemoryDocument Document, string ETag);

public enum MemoryBatchOperationKind
{
    Create,
    Replace
}

public enum MemoryLeaseMutationKind
{
    Claim,
    Release,
    Acknowledge
}

public sealed record MemoryLeaseMutation(
    MemoryLeaseMutationKind Kind,
    Guid LeaseToken,
    long DurationMilliseconds = 0);

public sealed record MemoryBatchOperation(
    MemoryBatchOperationKind Kind,
    KnowledgeMemoryDocument Document,
    string? ETag = null,
    MemoryLeaseMutation? LeaseMutation = null);

public enum MemoryBatchStatus
{
    Succeeded,
    Conflict,
    Failed
}

public sealed record MemoryBatchResult(MemoryBatchStatus Status)
{
    public bool Succeeded => Status == MemoryBatchStatus.Succeeded;
}

public sealed record CosmosMemoryContainerIdentity(
    string DatabaseId,
    string ContainerId,
    ImmutableArray<string> PartitionKeyPaths,
    int? DefaultTimeToLiveSeconds,
    bool HasRequiredSearchCompositeIndex);

public interface IKnowledgeMemoryDocumentStore
{
    Task<MemoryStoredDocument?> ReadAsync(
        string projectId,
        string documentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeMemoryDocument>> FindAcceptedEffectAsync(
        Guid receiptId,
        CancellationToken cancellationToken);

    Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeRecord>> ReadTransferCandidatesAsync(
        string projectId,
        string agentId,
        int maximumRecords,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Guid>> FindRevisionIdsAsync(
        string projectId,
        IReadOnlyCollection<Guid> revisionIds,
        CancellationToken cancellationToken);

    Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeRecord>> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken);

    Task<bool> HasUndeliveredPredecessorAsync(
        string projectId,
        string streamId,
        long sequence,
        CancellationToken cancellationToken);

    Task<MemoryBatchResult> ExecuteBatchAsync(
        string projectId,
        IReadOnlyList<MemoryBatchOperation> operations,
        CancellationToken cancellationToken);
}

public interface ICosmosMemoryDocumentStore : IKnowledgeMemoryDocumentStore
{
    Task<CosmosMemoryContainerIdentity> ReadContainerIdentityAsync(
        CancellationToken cancellationToken);
}

public sealed class CosmosMemoryDocumentStore : ICosmosMemoryDocumentStore
{
    private const string RecordDocumentType = "record";
    private const string RevisionDocumentType = "revision";
    private const string AcceptedEffectDocumentType = "accepted-effect";

    private readonly CosmosClient _client;
    private readonly string _databaseId;
    private readonly string _containerId;
    private readonly Container _container;

    public CosmosMemoryDocumentStore(CosmosClient client, CosmosMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _client = client;
        _databaseId = options.DatabaseId;
        _containerId = options.ContainerId;
        _container = client.GetContainer(_databaseId, _containerId);
    }

    public async Task<CosmosMemoryContainerIdentity> ReadContainerIdentityAsync(
        CancellationToken cancellationToken)
    {
        var database = await _client.GetDatabase(_databaseId)
            .ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var container = await _container.ReadContainerAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var hasRequiredSearchCompositeIndex = container.Resource.IndexingPolicy?.CompositeIndexes?
            .Any(paths =>
                paths.Count == 2 &&
                string.Equals(paths[0].Path, "/record/updatedAt", StringComparison.Ordinal) &&
                paths[0].Order == CompositePathSortOrder.Descending &&
                string.Equals(paths[1].Path, "/record/recordId", StringComparison.Ordinal) &&
                paths[1].Order == CompositePathSortOrder.Ascending) == true;
        return new CosmosMemoryContainerIdentity(
            database.Resource.Id,
            container.Resource.Id,
            container.Resource.PartitionKeyPaths.ToImmutableArray(),
            container.Resource.DefaultTimeToLive,
            hasRequiredSearchCompositeIndex);
    }

    public async Task<MemoryStoredDocument?> ReadAsync(
        string projectId,
        string documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<KnowledgeMemoryDocument>(
                documentId,
                new PartitionKey(projectId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MemoryStoredDocument(response.Resource, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<KnowledgeMemoryDocument>> FindAcceptedEffectAsync(
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("""
            SELECT TOP 2 * FROM c
            WHERE c.id = @id AND c.documentType = @acceptedEffectType
            """)
            .WithParameter("@id", KnowledgeMemoryDocumentIds.AcceptedEffect(receiptId))
            .WithParameter("@acceptedEffectType", AcceptedEffectDocumentType);
        using var iterator = _container.GetItemQueryIterator<KnowledgeMemoryDocument>(query);
        var documents = new List<KnowledgeMemoryDocument>();
        while (iterator.HasMoreResults && documents.Count < 2)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            documents.AddRange(response.Resource.Take(2 - documents.Count));
        }
        return documents;
    }

    public async Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken)
    {
        var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var where = """
            c.projectId = @project
            AND c.documentType = @recordType
            AND c.record.agentId = @agent
            AND (@kind IS NULL OR c.record.kind = @kind)
            AND (@includeInactive = true OR c.record.state = @active)
            """;
        if (text is not null)
            where += """
                AND (
                    CONTAINS(LOWER(c.record.type), LOWER(@query))
                    OR (IS_DEFINED(c.record.title) AND c.record.title != null
                        AND CONTAINS(LOWER(c.record.title), LOWER(@query)))
                    OR CONTAINS(LOWER(c.record.content), LOWER(@query))
                    OR EXISTS (
                        SELECT VALUE tag FROM tag IN c.record.tags
                        WHERE CONTAINS(LOWER(tag), LOWER(@query)))
                )
                """;

        var countQuery = CreateSearchQuery($"SELECT VALUE COUNT(1) FROM c WHERE {where}", query, text);
        var total = await ReadScalarAsync<long>(
            countQuery, query.ProjectId, cancellationToken).ConfigureAwait(false);
        var offset = checked((long)(query.Page - 1) * query.PageSize);
        var itemsQuery = CreateSearchQuery(
            $"SELECT * FROM c WHERE {where} ORDER BY c.record.updatedAt DESC, c.record.recordId OFFSET @offset LIMIT @limit",
            query,
            text)
            .WithParameter("@offset", offset)
            .WithParameter("@limit", query.PageSize);
        var documents = await ReadManyAsync(
            itemsQuery, query.ProjectId, cancellationToken).ConfigureAwait(false);
        return new KnowledgeRecordPage(
            documents.Select(document => document.Record
                    ?? throw new KnowledgeStorageUnavailableException())
                .ToImmutableArray(),
            (int)Math.Min(total, int.MaxValue),
            query.Page,
            query.PageSize);
    }

    public async Task<IReadOnlyList<KnowledgeRecord>> ReadTransferCandidatesAsync(
        string projectId,
        string agentId,
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("""
            SELECT * FROM c
            WHERE c.projectId = @project
              AND c.documentType = @recordType
              AND c.record.agentId = @agent
              AND (
                c.record.kind = @decision
                OR c.record.kind = @memory
              )
            ORDER BY c.record.updatedAt DESC, c.record.recordId
            """)
            .WithParameter("@project", projectId)
            .WithParameter("@recordType", RecordDocumentType)
            .WithParameter("@decision", (int)KnowledgeRecordKind.Decision)
            .WithParameter("@memory", (int)KnowledgeRecordKind.Memory)
            .WithParameter("@agent", agentId);
        var documents = await ReadManyAsync(
            query, projectId, cancellationToken, maximumRecords + 1).ConfigureAwait(false);
        return documents.Select(document => document.Record
                ?? throw new KnowledgeStorageUnavailableException())
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Guid>> FindRevisionIdsAsync(
        string projectId,
        IReadOnlyCollection<Guid> revisionIds,
        CancellationToken cancellationToken)
    {
        if (revisionIds.Count == 0)
            return Array.Empty<Guid>();
        var query = new QueryDefinition("""
            SELECT VALUE c.recordRevision.revisionId
            FROM c
            WHERE c.projectId = @project
              AND c.documentType = @revisionType
              AND ARRAY_CONTAINS(@revisionIds, c.recordRevision.revisionId)
            """)
            .WithParameter("@project", projectId)
            .WithParameter("@revisionType", RevisionDocumentType)
            .WithParameter("@revisionIds", revisionIds.Select(id => id.ToString("D")).ToArray());
        using var iterator = _container.GetItemQueryIterator<string>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) });
        var found = new HashSet<Guid>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in response)
            {
                if (!Guid.TryParse(value, out var id))
                    throw new KnowledgeStorageUnavailableException();
                found.Add(id);
            }
        }
        return found;
    }

    public async Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        const string where = """
            c.projectId = @project
            AND c.documentType = @revisionType
            AND c.recordId = @record
            """;
        var countQuery = new QueryDefinition($"SELECT VALUE COUNT(1) FROM c WHERE {where}")
            .WithParameter("@project", projectId)
            .WithParameter("@revisionType", RevisionDocumentType)
            .WithParameter("@record", recordId.ToString("D"));
        var total = await ReadScalarAsync<long>(
            countQuery, projectId, cancellationToken).ConfigureAwait(false);
        var offset = checked((long)(page - 1) * pageSize);
        var itemsQuery = new QueryDefinition($"""
            SELECT * FROM c WHERE {where}
            ORDER BY c.revision DESC
            OFFSET @offset LIMIT @limit
            """)
            .WithParameter("@project", projectId)
            .WithParameter("@revisionType", RevisionDocumentType)
            .WithParameter("@record", recordId.ToString("D"))
            .WithParameter("@offset", offset)
            .WithParameter("@limit", pageSize);
        var documents = await ReadManyAsync(itemsQuery, projectId, cancellationToken).ConfigureAwait(false);
        return new KnowledgeRecordRevisionPage(
            documents.Select(document => document.RecordRevision
                    ?? throw new KnowledgeStorageUnavailableException())
                .ToImmutableArray(),
            (int)Math.Min(total, int.MaxValue),
            page,
            pageSize);
    }

    public async Task<IReadOnlyList<KnowledgeRecord>> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("""
            SELECT * FROM c
            WHERE c.projectId = @project
              AND c.documentType = @recordType
              AND (
                (c.record.kind = @decision AND c.record.state = @active
                    AND c.record.trustState = @approved)
                OR (c.record.kind = @memory AND c.record.state = @active
                    AND c.record.trustState IN (@pending, @approved)
                    AND (c.record.agentId = @agent OR (
                        c.record.trustState = @approved
                        AND EXISTS (
                            SELECT VALUE tag FROM tag IN c.record.tags
                            WHERE LOWER(tag) = 'cross-team'))))
                OR (c.record.kind = @sessionContext AND c.record.state = @active
                    AND c.record.trustState = @approved
                    AND c.record.agentId = @agent
                    AND c.record.sourceRunId = @run)
              )
            """)
            .WithParameter("@project", projectId)
            .WithParameter("@recordType", RecordDocumentType)
            .WithParameter("@decision", (int)KnowledgeRecordKind.Decision)
            .WithParameter("@memory", (int)KnowledgeRecordKind.Memory)
            .WithParameter("@sessionContext", (int)KnowledgeRecordKind.SessionContext)
            .WithParameter("@active", (int)KnowledgeRecordState.Active)
            .WithParameter("@pending", (int)KnowledgeTrustState.Pending)
            .WithParameter("@approved", (int)KnowledgeTrustState.Approved)
            .WithParameter("@agent", agentId)
            .WithParameter("@run", runId);
        var documents = await ReadManyAsync(
            query, projectId, cancellationToken, maximumRecords + 1).ConfigureAwait(false);
        return documents.Select(document => document.Record
                ?? throw new KnowledgeStorageUnavailableException())
            .ToImmutableArray();
    }

    public async Task<bool> HasUndeliveredPredecessorAsync(
        string projectId,
        string streamId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("""
            SELECT VALUE COUNT(1) FROM c
            WHERE c.projectId = @project
              AND c.documentType = @acceptedEffectType
              AND c.streamId = @stream
              AND c.sequence < @sequence
              AND c.isDelivered = false
            """)
            .WithParameter("@project", projectId)
            .WithParameter("@acceptedEffectType", AcceptedEffectDocumentType)
            .WithParameter("@stream", streamId)
            .WithParameter("@sequence", sequence);
        return await ReadScalarAsync<long>(query, projectId, cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<MemoryBatchResult> ExecuteBatchAsync(
        string projectId,
        IReadOnlyList<MemoryBatchOperation> operations,
        CancellationToken cancellationToken)
    {
        if (operations.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(operations));
        if (operations.Any(operation =>
                !string.Equals(operation.Document.ProjectId, projectId, StringComparison.Ordinal)))
            throw new ArgumentException("Every Cosmos transactional batch item must use the same project partition.");

        var batch = _container.CreateTransactionalBatch(new PartitionKey(projectId));
        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case MemoryBatchOperationKind.Create:
                    batch.CreateItem(operation.Document);
                    break;
                case MemoryBatchOperationKind.Replace when !string.IsNullOrWhiteSpace(operation.ETag):
                    batch.ReplaceItem(
                        operation.Document.Id,
                        operation.Document,
                        new TransactionalBatchItemRequestOptions { IfMatchEtag = operation.ETag });
                    break;
                default:
                    throw new ArgumentException("The Cosmos transactional batch operation is invalid.");
            }
        }

        using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode is HttpStatusCode.Conflict or
            HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound
                ? MemoryBatchStatus.Conflict
                : (int)response.StatusCode is >= 200 and < 300
                    ? MemoryBatchStatus.Succeeded
                    : MemoryBatchStatus.Failed;
        return new MemoryBatchResult(status);
    }

    private QueryDefinition CreateSearchQuery(
        string queryText,
        KnowledgeRecordQuery query,
        string? text) =>
        new QueryDefinition(queryText)
            .WithParameter("@project", query.ProjectId)
            .WithParameter("@recordType", RecordDocumentType)
            .WithParameter("@agent", query.AgentId)
            .WithParameter("@kind", query.Kind is null ? null : (int)query.Kind.Value)
            .WithParameter("@includeInactive", query.IncludeInactive)
            .WithParameter("@active", (int)KnowledgeRecordState.Active)
            .WithParameter("@query", text);

    private async Task<long> ReadScalarAsync<T>(
        QueryDefinition query,
        string projectId,
        CancellationToken cancellationToken)
    {
        using var iterator = _container.GetItemQueryIterator<T>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) });
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (response.Resource.FirstOrDefault() is { } result)
                return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        return 0;
    }

    private async Task<IReadOnlyList<KnowledgeMemoryDocument>> ReadManyAsync(
        QueryDefinition query,
        string projectId,
        CancellationToken cancellationToken,
        int? maximumResults = null)
    {
        using var iterator = _container.GetItemQueryIterator<KnowledgeMemoryDocument>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) });
        var documents = new List<KnowledgeMemoryDocument>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var document in response.Resource)
            {
                documents.Add(document);
                if (maximumResults is { } maximum && documents.Count >= maximum)
                    return documents;
            }
        }
        return documents;
    }
}
