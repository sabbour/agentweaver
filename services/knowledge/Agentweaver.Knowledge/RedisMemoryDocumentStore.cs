using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

public sealed class RedisMemoryDocumentStore : IKnowledgeMemoryDocumentStore
{
    public const int MaximumGlobalReceiptScanKeys = 100_000;
    public const int MaximumProjectHashFields = 1_000_000;

    private const string RecordDocumentType = "record";
    private const string RevisionDocumentType = "revision";
    private const string AcceptedEffectDocumentType = "accepted-effect";
    private const string DecisionGraphDocumentType = "decision-graph";
    private const int MaximumBatchOperations = 100;
    private const int MaximumRedisArgumentCount = 3 + MaximumBatchOperations * 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string BatchScript = """
        local keyType = redis.call('TYPE', KEYS[1]).ok
        if keyType ~= 'none' and keyType ~= 'hash' then return -1 end
        local keyTtl = redis.call('PTTL', KEYS[1])
        if keyTtl ~= -2 and keyTtl ~= -1 then return -1 end

        local fieldExpiry = ARGV[1] == '1'
        local operationCount = tonumber(ARGV[2])
        if not operationCount or operationCount < 1 or operationCount > 100 then return -1 end
        local serverTime
        local writes = {}
        local seen = {}
        local nowMilliseconds
        for operationIndex = 1, operationCount do
            local offset = 3 + (operationIndex - 1) * 10
            local kind = ARGV[offset]
            local documentField = ARGV[offset + 1]
            local etagField = ARGV[offset + 2]
            local leaseField = ARGV[offset + 3]
            local expectedEtag = ARGV[offset + 4]
            local nextEtag = ARGV[offset + 5]
            local documentJson = ARGV[offset + 6]
            local mutation = ARGV[offset + 7]
            local leaseToken = ARGV[offset + 8]
            local leaseDurationMilliseconds = tonumber(ARGV[offset + 9])

            if not documentField or not etagField or not leaseField or
                not nextEtag or not documentJson or
                seen[documentField] or documentField == etagField or documentField == leaseField or
                etagField == leaseField then return -1 end
            seen[documentField] = true

            local currentDocument = redis.call('HGET', KEYS[1], documentField)
            local currentEtag = redis.call('HGET', KEYS[1], etagField)
            local currentLease = redis.call('HGET', KEYS[1], leaseField)
            if fieldExpiry then
                local expirations = redis.call(
                    'HPTTL', KEYS[1], 'FIELDS', 3, documentField, etagField, leaseField)
                local values = {currentDocument, currentEtag, currentLease}
                for index = 1, 3 do
                    if expirations[index] >= 0 or (values[index] and expirations[index] == -2) then
                        return -1
                    end
                end
            end

            if kind == 'C' then
                if currentDocument then return 0 end
                if currentEtag or currentLease then return -1 end
            elseif kind == 'R' then
                if not currentDocument then return 0 end
                if not currentEtag or not currentLease then return -1 end
                if currentEtag ~= expectedEtag then return 0 end
            else
                return -1
            end

            local nextLease = currentLease or '0'
            if mutation ~= '' then
                if not currentDocument or not currentLease or not leaseToken or leaseToken == '' then
                    return -1
                end
                if not serverTime then
                    serverTime = redis.call('TIME')
                    nowMilliseconds = tonumber(serverTime[1]) * 1000 +
                        math.floor(tonumber(serverTime[2]) / 1000)
                end
                local currentDeadline
                local currentToken
                if currentLease ~= '0' then
                    currentDeadline, currentToken = string.match(currentLease, '^(%d+)|([0-9a-fA-F%-]+)$')
                    if not currentDeadline or not currentToken then return -1 end
                end

                if mutation == 'claim' then
                    if not leaseDurationMilliseconds or leaseDurationMilliseconds < 1 or
                        leaseDurationMilliseconds > 3600000 then return -1 end
                    if currentDeadline and tonumber(currentDeadline) > nowMilliseconds then return 0 end
                    nextLease = string.format('%.0f', nowMilliseconds + leaseDurationMilliseconds) ..
                        '|' .. leaseToken
                elseif mutation == 'acknowledge' then
                    if currentToken ~= leaseToken or
                        not currentDeadline or tonumber(currentDeadline) <= nowMilliseconds then return 0 end
                    nextLease = '0'
                elseif mutation == 'release' then
                    if currentToken ~= leaseToken then return 0 end
                    nextLease = '0'
                else
                    return -1
                end
            elseif currentLease and currentLease ~= '0' then
                return -1
            end

            table.insert(writes, documentField)
            table.insert(writes, documentJson)
            table.insert(writes, etagField)
            table.insert(writes, nextEtag)
            table.insert(writes, leaseField)
            table.insert(writes, nextLease)
        end

        redis.call('HSET', KEYS[1], unpack(writes))
        return 1
        """;

    private readonly IRedisMemoryCommandClient client;
    private readonly RedisMemoryOptions options;
    private volatile bool _supportsHashFieldExpiration;

    public RedisMemoryDocumentStore(IRedisMemoryCommandClient client, RedisMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.client = client;
        this.options = options;
    }

    public async Task VerifyProviderCompatibilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await client.ReadServerSnapshotAsync(cancellationToken).ConfigureAwait(false);
            ValidateSnapshot(snapshot, negotiation: true);
            var serverVersion = ReadVersion(snapshot);
            _supportsHashFieldExpiration = SupportsHashFieldExpiration(serverVersion);
            var keys = await client.ScanKeysAsync(ProjectKeyPattern, cancellationToken).ConfigureAwait(false);
            var uniqueKeys = keys.ToHashSet(StringComparer.Ordinal);
            if (uniqueKeys.Count > MaximumGlobalReceiptScanKeys)
                throw new KnowledgeProviderUnavailableException(
                    "The configured Redis Memory namespace exceeds the supported retention audit limit.");
            foreach (var key in uniqueKeys)
                await ReadPersistentHashAsync(key, negotiation: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KnowledgeProviderUnavailableException)
        {
            throw;
        }
        catch (KnowledgeStorageUnavailableException)
        {
            throw new KnowledgeProviderUnavailableException(
                "The configured Redis Memory namespace contains invalid stored data.");
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            throw new KnowledgeProviderUnavailableException(
                "The configured Redis Memory primary could not be inspected.");
        }
    }

    public async Task<MemoryStoredDocument?> ReadAsync(
        string projectId,
        string documentId,
        CancellationToken cancellationToken)
    {
        ValidateScope(projectId, documentId);
        await EnsureOperationalAsync(cancellationToken).ConfigureAwait(false);
        var values = await client.ReadHashFieldsAsync(
            ProjectKey(projectId),
            [DocumentField(documentId), ETagField(documentId), LeaseField(documentId)],
            _supportsHashFieldExpiration,
            cancellationToken).ConfigureAwait(false);
        if (values.Count == 0)
            throw new KnowledgeStorageUnavailableException();
        if (values[0] == "missing")
        {
            if (values.Count != 1)
                throw new KnowledgeStorageUnavailableException();
            return null;
        }
        if (values.Count != 4)
            throw new KnowledgeStorageUnavailableException();
        if (values[0] != "ok")
            throw new KnowledgeStorageUnavailableException();
        var documentJson = values[1];
        var etag = values[2];
        var leaseState = values[3];
        if (documentJson is null)
        {
            if (etag is not null || leaseState is not null)
                throw new KnowledgeStorageUnavailableException();
            return null;
        }
        if (string.IsNullOrWhiteSpace(etag) || leaseState is null)
            throw new KnowledgeStorageUnavailableException();
        var document = DeserializeDocument(documentJson);
        if (!string.Equals(document.Id, documentId, StringComparison.Ordinal) ||
            !string.Equals(document.ProjectId, projectId, StringComparison.Ordinal) ||
            !HasValidDocumentPayload(document))
            throw new KnowledgeStorageUnavailableException();
        return new MemoryStoredDocument(ApplyLeaseState(document, leaseState), etag);
    }

    public async Task<IReadOnlyList<KnowledgeMemoryDocument>> FindAcceptedEffectAsync(
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        if (receiptId == Guid.Empty)
            throw new KnowledgeStorageUnavailableException();
        await EnsureOperationalAsync(cancellationToken).ConfigureAwait(false);
        var keys = await client.ScanKeysAsync(ProjectKeyPattern, cancellationToken).ConfigureAwait(false);
        var uniqueKeys = keys.ToHashSet(StringComparer.Ordinal);
        if (uniqueKeys.Count > MaximumGlobalReceiptScanKeys)
            throw new KnowledgeStorageUnavailableException();
        var matches = new List<KnowledgeMemoryDocument>(2);
        foreach (var key in uniqueKeys)
        {
            var fields = await ReadPersistentHashAsync(key, cancellationToken).ConfigureAwait(false);
            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (!field.Name.StartsWith("doc:", StringComparison.Ordinal) ||
                    !seenFields.Add(field.Name))
                    continue;
                var document = DeserializeDocument(field.Value);
                if (document.DocumentType == AcceptedEffectDocumentType &&
                    document.Receipt?.ReceiptId == receiptId)
                    matches.Add(document);
            }
        }
        return matches.Take(2).ToArray();
    }

    public async Task<KnowledgeRecordPage> SearchAsync(
        KnowledgeRecordQuery query,
        CancellationToken cancellationToken)
    {
        var documents = await ReadProjectDocumentsAsync(query.ProjectId, cancellationToken).ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var records = documents
            .Where(document => document.DocumentType == RecordDocumentType && document.Record is not null)
            .Select(document => document.Record!)
            .Where(record =>
                string.Equals(record.AgentId, query.AgentId, StringComparison.Ordinal) &&
                (query.Kind is null || record.Kind == query.Kind) &&
                (query.IncludeInactive || record.State == KnowledgeRecordState.Active) &&
                (text is null || Matches(record, text)))
            .OrderByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.RecordId)
            .ToArray();
        var offset = checked((long)(query.Page - 1) * query.PageSize);
        var page = offset >= records.Length
            ? ImmutableArray<KnowledgeRecord>.Empty
            : records.Skip(checked((int)offset)).Take(query.PageSize).ToImmutableArray();
        return new KnowledgeRecordPage(
            page,
            records.Length,
            query.Page,
            query.PageSize);
    }

    public async Task<IReadOnlyList<KnowledgeRecord>> ReadTransferCandidatesAsync(
        string projectId,
        string agentId,
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        if (maximumRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        var documents = await ReadProjectDocumentsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return documents
            .Where(document => document.DocumentType == RecordDocumentType && document.Record is not null)
            .Select(document => document.Record!)
            .Where(record =>
                string.Equals(record.AgentId, agentId, StringComparison.Ordinal) &&
                record.Kind is KnowledgeRecordKind.Decision or KnowledgeRecordKind.Memory)
            .OrderByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.RecordId)
            .Take(maximumRecords + 1)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Guid>> FindRevisionIdsAsync(
        string projectId,
        IReadOnlyCollection<Guid> revisionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revisionIds);
        if (revisionIds.Count == 0)
            return Array.Empty<Guid>();
        var requested = revisionIds.ToHashSet();
        var documents = await ReadProjectDocumentsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return documents
            .Where(document =>
                document.DocumentType == RevisionDocumentType &&
                document.RecordRevision is not null &&
                requested.Contains(document.RecordRevision.RevisionId))
            .Select(document => document.RecordRevision!.RevisionId)
            .ToHashSet();
    }

    public async Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var documents = await ReadProjectDocumentsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var revisions = documents
            .Where(document =>
                document.DocumentType == RevisionDocumentType &&
                document.RecordId == recordId &&
                document.RecordRevision is not null)
            .Select(document => document.RecordRevision!)
            .OrderByDescending(revision => revision.Revision)
            .ToArray();
        var offset = checked((long)(page - 1) * pageSize);
        var items = offset >= revisions.Length
            ? ImmutableArray<KnowledgeRecordRevision>.Empty
            : revisions.Skip(checked((int)offset)).Take(pageSize).ToImmutableArray();
        return new KnowledgeRecordRevisionPage(items, revisions.Length, page, pageSize);
    }

    public async Task<IReadOnlyList<KnowledgeRecord>> ReadContextCandidatesAsync(
        string projectId,
        string agentId,
        string runId,
        int maximumRecords,
        CancellationToken cancellationToken)
    {
        var documents = await ReadProjectDocumentsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return documents
            .Where(document => document.DocumentType == RecordDocumentType && document.Record is not null)
            .Select(document => document.Record!)
            .Where(record =>
                (record.Kind == KnowledgeRecordKind.Decision &&
                    record.State == KnowledgeRecordState.Active &&
                    record.TrustState == KnowledgeTrustState.Approved) ||
                (record.Kind == KnowledgeRecordKind.Memory &&
                    record.State == KnowledgeRecordState.Active &&
                    record.TrustState is KnowledgeTrustState.Pending or KnowledgeTrustState.Approved &&
                    (string.Equals(record.AgentId, agentId, StringComparison.Ordinal) ||
                        (record.TrustState == KnowledgeTrustState.Approved &&
                            record.Tags.Any(tag => string.Equals(tag, "cross-team",
                                StringComparison.OrdinalIgnoreCase))))) ||
                (record.Kind == KnowledgeRecordKind.SessionContext &&
                    record.State == KnowledgeRecordState.Active &&
                    record.TrustState == KnowledgeTrustState.Approved &&
                    string.Equals(record.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(record.SourceRunId, runId, StringComparison.Ordinal)))
            .Take(maximumRecords + 1)
            .ToArray();
    }

    public async Task<bool> HasUndeliveredPredecessorAsync(
        string projectId,
        string streamId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var documents = await ReadProjectDocumentsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return documents.Any(document =>
            document.DocumentType == AcceptedEffectDocumentType &&
            string.Equals(document.StreamId, streamId, StringComparison.Ordinal) &&
            document.Sequence < sequence &&
            !document.IsDelivered);
    }

    public async Task<MemoryBatchResult> ExecuteBatchAsync(
        string projectId,
        IReadOnlyList<MemoryBatchOperation> operations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            operations.Count is < 1 or > MaximumBatchOperations ||
            operations.Any(operation =>
                !string.Equals(operation.Document.ProjectId, projectId, StringComparison.Ordinal)))
            throw new ArgumentException("The Redis Memory batch is invalid.");

        await EnsureOperationalAsync(cancellationToken).ConfigureAwait(false);
        var key = ProjectKey(projectId);
        await VerifyPersistentHashAsync(key, negotiation: false, cancellationToken).ConfigureAwait(false);
        var supportsFieldExpiration = _supportsHashFieldExpiration;
        var arguments = new List<string>(MaximumRedisArgumentCount)
        {
            supportsFieldExpiration ? "1" : "0",
            operations.Count.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var operation in operations)
        {
            var document = operation.Document;
            var mutation = operation.LeaseMutation;
            if (mutation is not null &&
                document.DocumentType != AcceptedEffectDocumentType)
                throw new ArgumentException("Delivery lease mutations are valid only for accepted effects.");
            var storedDocument = document.DocumentType == AcceptedEffectDocumentType
                ? document with { LeaseToken = null, LeasedUntil = null }
                : document;
            arguments.Add(operation.Kind switch
            {
                MemoryBatchOperationKind.Create => "C",
                MemoryBatchOperationKind.Replace => "R",
                _ => throw new ArgumentException("The Redis Memory batch operation kind is invalid.")
            });
            arguments.Add(DocumentField(document.Id));
            arguments.Add(ETagField(document.Id));
            arguments.Add(LeaseField(document.Id));
            arguments.Add(operation.ETag ?? string.Empty);
            arguments.Add(Guid.NewGuid().ToString("N"));
            arguments.Add(JsonSerializer.Serialize(storedDocument, JsonOptions));
            arguments.Add(mutation?.Kind switch
            {
                MemoryLeaseMutationKind.Claim => "claim",
                MemoryLeaseMutationKind.Release => "release",
                MemoryLeaseMutationKind.Acknowledge => "acknowledge",
                _ => string.Empty
            });
            arguments.Add(mutation?.LeaseToken.ToString("N") ?? string.Empty);
            arguments.Add(mutation?.DurationMilliseconds.ToString(CultureInfo.InvariantCulture) ?? "0");
        }

        var result = await client.ExecuteBatchScriptAsync(
            BatchScript, key, arguments, cancellationToken).ConfigureAwait(false);
        return new MemoryBatchResult(result switch
        {
            1 => MemoryBatchStatus.Succeeded,
            0 => MemoryBatchStatus.Conflict,
            _ => MemoryBatchStatus.Failed
        });
    }

    private string ProjectKeyPattern => $"{options.KeyPrefix}:project:*";

    private string ProjectKey(string projectId)
    {
        var projectHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(projectId)))
            .ToLowerInvariant();
        return $"{options.KeyPrefix}:project:{{{projectHash}}}";
    }

    private static string DocumentField(string documentId) => $"doc:{documentId}";

    private static string ETagField(string documentId) => $"etag:{documentId}";

    private static string LeaseField(string documentId) => $"lease:{documentId}";

    private async Task EnsureOperationalAsync(CancellationToken cancellationToken)
    {
        var snapshot = await client.ReadServerSnapshotAsync(cancellationToken).ConfigureAwait(false);
        ValidateSnapshot(snapshot, negotiation: false);
    }

    private async Task VerifyPersistentHashAsync(
        string key,
        bool negotiation,
        CancellationToken cancellationToken)
    {
        var ttl = await client.GetKeyTimeToLiveAsync(key, cancellationToken).ConfigureAwait(false);
        if (ttl is not null)
            ThrowIncompatibleRetention(negotiation);
    }

    private async Task<IReadOnlyList<RedisMemoryHashField>> ReadPersistentHashAsync(
        string key,
        CancellationToken cancellationToken) =>
        await ReadPersistentHashAsync(key, negotiation: false, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<RedisMemoryHashField>> ReadPersistentHashAsync(
        string key,
        bool negotiation,
        CancellationToken cancellationToken)
    {
        await VerifyPersistentHashAsync(key, negotiation, cancellationToken).ConfigureAwait(false);
        var fields = await client.ScanHashAsync(key, cancellationToken).ConfigureAwait(false);
        if (_supportsHashFieldExpiration)
        {
            foreach (var fieldBatch in fields.Chunk(128))
            {
                var expirations = await client.ReadHashFieldTtlsAsync(
                    key, fieldBatch.Select(field => field.Name).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                if (expirations.Count != fieldBatch.Length ||
                    expirations.Any(expiration => expiration != -1))
                    ThrowIncompatibleRetention(negotiation);
            }
        }

        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
            fieldValues[field.Name] = field.Value;
        foreach (var entry in fieldValues.Where(pair => pair.Key.StartsWith("doc:", StringComparison.Ordinal)))
        {
            var documentId = entry.Key["doc:".Length..];
            var document = DeserializeDocument(entry.Value);
            if (!string.Equals(document.Id, documentId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(document.ProjectId) ||
                !string.Equals(ProjectKey(document.ProjectId), key, StringComparison.Ordinal) ||
                !HasValidDocumentPayload(document) ||
                !fieldValues.TryGetValue(ETagField(documentId), out var etag) ||
                string.IsNullOrWhiteSpace(etag))
                ThrowInvalidHashData(negotiation);
            if (!fieldValues.TryGetValue(LeaseField(documentId), out var leaseState))
                ThrowInvalidHashData(negotiation);
            _ = ApplyLeaseState(document, leaseState);
        }
        foreach (var name in fieldValues.Keys)
            if ((name.StartsWith("etag:", StringComparison.Ordinal) ||
                    name.StartsWith("lease:", StringComparison.Ordinal)) &&
                !fieldValues.ContainsKey(DocumentField(name[(name.IndexOf(':') + 1)..])))
                ThrowInvalidHashData(negotiation);
        return fields;
    }

    private async Task<IReadOnlyList<KnowledgeMemoryDocument>> ReadProjectDocumentsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ValidateScope(projectId, "project");
        await EnsureOperationalAsync(cancellationToken).ConfigureAwait(false);
        var fields = await ReadPersistentHashAsync(ProjectKey(projectId), cancellationToken).ConfigureAwait(false);
        var documents = new Dictionary<string, KnowledgeMemoryDocument>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!field.Name.StartsWith("doc:", StringComparison.Ordinal))
                continue;
            var documentId = field.Name["doc:".Length..];
            var document = DeserializeDocument(field.Value);
            if (!string.Equals(document.Id, documentId, StringComparison.Ordinal) ||
                !string.Equals(document.ProjectId, projectId, StringComparison.Ordinal))
                throw new KnowledgeStorageUnavailableException();
            if (!documents.TryAdd(documentId, document))
                documents[documentId] = document;
        }
        return documents.Values.ToArray();
    }

    private void ValidateSnapshot(RedisMemoryServerSnapshot snapshot, bool negotiation)
    {
        var incompatible =
            !ConfigEquals(snapshot, "appendonly", "yes") ||
            !ConfigEquals(snapshot, "appendfsync", "always") ||
            !ConfigEquals(snapshot, "no-appendfsync-on-rewrite", "no") ||
            !ConfigEquals(snapshot, "maxmemory-policy", "noeviction") ||
            !InfoEquals(snapshot.Persistence, "aof_enabled", "1") ||
            !InfoEquals(snapshot.Persistence, "aof_last_write_status", "ok") ||
            !InfoEquals(snapshot.Replication, "role", "master") ||
            !HasNoReplicas(snapshot.Replication) ||
            !InfoEquals(snapshot.Cluster, "cluster_enabled", "0") ||
            !Version.TryParse(snapshot.Server.GetValueOrDefault("redis_version"), out var version) ||
            version < new Version(6, 0);
        if (!incompatible)
            return;
        if (negotiation)
            throw new KnowledgeProviderUnavailableException(
                "Redis Memory requires a standalone primary with verified AOF-always, no-rewrite-fsync suppression, noeviction, and healthy AOF status.");
        throw new KnowledgeStorageUnavailableException();
    }

    private static Version ReadVersion(RedisMemoryServerSnapshot snapshot) =>
        Version.TryParse(snapshot.Server.GetValueOrDefault("redis_version"), out var version)
            ? version
            : throw new KnowledgeProviderUnavailableException("Redis did not report a valid server version.");

    private static bool SupportsHashFieldExpiration(Version version) =>
        version >= new Version(7, 4);

    private static bool ConfigEquals(
        RedisMemoryServerSnapshot snapshot,
        string name,
        string expected) =>
        string.Equals(
            snapshot.Configuration.GetValueOrDefault(name),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static bool InfoEquals(
        ImmutableDictionary<string, string> values,
        string name,
        string expected) =>
        string.Equals(values.GetValueOrDefault(name), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasNoReplicas(ImmutableDictionary<string, string> replication)
    {
        var count = replication.GetValueOrDefault("connected_replicas") ??
            replication.GetValueOrDefault("connected_slaves");
        return int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var replicaCount) &&
            replicaCount == 0;
    }

    private static void ThrowIncompatibleRetention(bool negotiation)
    {
        if (negotiation)
            throw new KnowledgeProviderUnavailableException(
                "The Redis Memory namespace contains expiring keys or fields.");
        throw new KnowledgeStorageUnavailableException();
    }

    private static KnowledgeMemoryDocument DeserializeDocument(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<KnowledgeMemoryDocument>(json, JsonOptions)
                ?? throw new KnowledgeStorageUnavailableException();
        }
        catch (JsonException)
        {
            throw new KnowledgeStorageUnavailableException();
        }
    }

    private static bool HasValidDocumentPayload(KnowledgeMemoryDocument document) =>
        document.DocumentType switch
        {
            RecordDocumentType when document.Record is { } record =>
                record.RecordId != Guid.Empty &&
                string.Equals(document.Id, KnowledgeMemoryDocumentIds.Record(record.RecordId), StringComparison.Ordinal) &&
                string.Equals(record.ProjectId, document.ProjectId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(record.AgentId) &&
                !string.IsNullOrWhiteSpace(record.Type) &&
                record.Content is not null &&
                !string.IsNullOrWhiteSpace(record.Importance) &&
                !record.Tags.IsDefault &&
                record.Revision >= 1 &&
                record.RevisionId != Guid.Empty &&
                Enum.IsDefined(record.Kind) &&
                Enum.IsDefined(record.State) &&
                Enum.IsDefined(record.TrustState),
            RevisionDocumentType when document.RecordRevision is { } revision =>
                revision.RecordId != Guid.Empty &&
                revision.Revision >= 1 &&
                revision.RevisionId != Guid.Empty &&
                string.Equals(
                    document.Id,
                    KnowledgeMemoryDocumentIds.Revision(revision.RecordId, revision.Revision),
                    StringComparison.Ordinal) &&
                document.RecordId == revision.RecordId &&
                document.Revision == revision.Revision &&
                !string.IsNullOrWhiteSpace(revision.Type) &&
                revision.Content is not null &&
                !string.IsNullOrWhiteSpace(revision.Importance) &&
                !string.IsNullOrWhiteSpace(revision.Reason) &&
                !revision.Tags.IsDefault &&
                Enum.IsDefined(revision.Kind) &&
                Enum.IsDefined(revision.State) &&
                Enum.IsDefined(revision.TrustState),
            "write" when
                !string.IsNullOrWhiteSpace(document.ActorFingerprint) &&
                !string.IsNullOrWhiteSpace(document.IdempotencyKey) &&
                !string.IsNullOrWhiteSpace(document.RequestFingerprint) &&
                document.ResultKind is "record" or "promotion" or "transfer" &&
                !string.IsNullOrWhiteSpace(document.ResultJson) =>
                string.Equals(
                    document.Id,
                    KnowledgeMemoryDocumentIds.Write(document.ActorFingerprint, document.IdempotencyKey),
                    StringComparison.Ordinal),
            "stream" when
                !string.IsNullOrWhiteSpace(document.StreamId) &&
                document.LastSequence is > 0 =>
                string.Equals(
                    document.Id,
                    KnowledgeMemoryDocumentIds.Stream(document.StreamId),
                    StringComparison.Ordinal) &&
                document.StreamId.StartsWith(
                    $"knowledge/{document.ProjectId}/",
                    StringComparison.Ordinal),
            AcceptedEffectDocumentType when document.Receipt is { } receipt =>
                receipt.ReceiptId != Guid.Empty &&
                string.Equals(
                    document.Id,
                    KnowledgeMemoryDocumentIds.AcceptedEffect(receipt.ReceiptId),
                    StringComparison.Ordinal) &&
                string.Equals(receipt.ProjectId, document.ProjectId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(receipt.RunId) &&
                string.Equals(
                    document.StreamId,
                    $"knowledge/{receipt.ProjectId}/{receipt.RunId}",
                    StringComparison.Ordinal) &&
                document.Sequence is > 0 &&
                receipt.SchemaVersion == AcceptedEffectContractVersions.CurrentSchemaVersion &&
                receipt.EventVersion == AcceptedEffectContractVersions.CurrentEventVersion &&
                receipt.EffectId != Guid.Empty &&
                receipt.RecordId != Guid.Empty &&
                receipt.RecordVersion >= 1 &&
                !string.IsNullOrWhiteSpace(receipt.Issuer) &&
                !string.IsNullOrWhiteSpace(receipt.Subject) &&
                !string.IsNullOrWhiteSpace(receipt.TenantId) &&
                Enum.IsDefined(receipt.AuthorizationResourceType) &&
                !string.IsNullOrWhiteSpace(receipt.AuthorizationResourceId) &&
                !string.IsNullOrWhiteSpace(receipt.ContextRevision),
            DecisionGraphDocumentType when document.LastSequence is > 0 =>
                string.Equals(
                    document.Id,
                    KnowledgeMemoryDocumentIds.DecisionGraph(),
                    StringComparison.Ordinal),
            _ => false
        };

    [DoesNotReturn]
    private static void ThrowInvalidHashData(bool negotiation)
    {
        if (negotiation)
            throw new KnowledgeProviderUnavailableException(
                "The Redis Memory namespace contains malformed or incomplete documents.");
        throw new KnowledgeStorageUnavailableException();
    }

    private static KnowledgeMemoryDocument ApplyLeaseState(
        KnowledgeMemoryDocument document,
        string leaseState)
    {
        if (document.DocumentType != AcceptedEffectDocumentType)
        {
            if (!string.Equals(leaseState, "0", StringComparison.Ordinal))
                throw new KnowledgeStorageUnavailableException();
            return document;
        }
        if (string.Equals(leaseState, "0", StringComparison.Ordinal))
            return document with { LeaseToken = null, LeasedUntil = null };
        var separator = leaseState.IndexOf('|');
        if (separator <= 0 ||
            !long.TryParse(
                leaseState.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiresAtMilliseconds) ||
            !Guid.TryParseExact(leaseState.AsSpan(separator + 1), "N", out var leaseToken))
            throw new KnowledgeStorageUnavailableException();
        try
        {
            return document with
            {
                LeaseToken = leaseToken,
                LeasedUntil = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMilliseconds)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new KnowledgeStorageUnavailableException();
        }
    }

    private static bool Matches(KnowledgeRecord record, string text) =>
        record.Type.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        (record.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
        record.Content.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        record.Tags.Any(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static void ValidateScope(string projectId, string documentId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Redis Memory document scope is invalid.");
    }

    private static bool IsRedisFailure(Exception exception) =>
        exception is StackExchange.Redis.RedisException or TimeoutException or IOException;
}
