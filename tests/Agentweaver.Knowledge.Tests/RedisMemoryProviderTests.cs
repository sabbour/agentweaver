using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

public sealed class RedisMemoryProviderTests
{
    private const string ActorFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task NegotiationFailsClosedForUnverifiedDurabilityEvictionAndTopology()
    {
        var (provider, client, options) = CreateProvider();
        var candidate = Candidate(provider, options);
        var baseline = client.Snapshot;
        var incompatible = new[]
        {
            baseline with
            {
                Configuration = baseline.Configuration.SetItem("appendfsync", "everysec")
            },
            baseline with
            {
                Configuration = baseline.Configuration.SetItem("no-appendfsync-on-rewrite", "yes")
            },
            baseline with
            {
                Configuration = baseline.Configuration.SetItem("maxmemory-policy", "allkeys-lru")
            },
            baseline with
            {
                Persistence = baseline.Persistence.SetItem("aof_enabled", "0")
            },
            baseline with
            {
                Persistence = baseline.Persistence.SetItem("aof_last_write_status", "err")
            },
            baseline with
            {
                Replication = baseline.Replication.SetItem("role", "slave")
            },
            baseline with
            {
                Replication = baseline.Replication.SetItem("connected_slaves", "1")
            },
            baseline with
            {
                Cluster = baseline.Cluster.SetItem("cluster_enabled", "1")
            },
            baseline with
            {
                Server = baseline.Server.SetItem("aof_enabled", "1"),
                Persistence = baseline.Persistence.Remove("aof_enabled")
            }
        };

        foreach (var snapshot in incompatible)
        {
            client.Snapshot = snapshot;
            await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(
                () => provider.NegotiateAsync(candidate));
        }

        client.Snapshot = baseline;
        client.FailServerInspection = true;
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(
            () => provider.NegotiateAsync(candidate));
    }

    [Fact]
    public async Task NegotiationRejectsExpiringKeysAndHashFields()
    {
        var (keyProvider, keyClient, keyOptions) = CreateProvider();
        var keyCandidate = Candidate(keyProvider, keyOptions);
        var projectKey = ProjectKey(keyOptions, "project-a");
        keyClient.SeedHash(projectKey, ("external", "value"));
        keyClient.SetKeyTtl(projectKey, TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(
            () => keyProvider.NegotiateAsync(keyCandidate));

        var (fieldProvider, fieldClient, fieldOptions) = CreateProvider();
        var fieldCandidate = Candidate(fieldProvider, fieldOptions);
        var fieldKey = ProjectKey(fieldOptions, "project-a");
        fieldClient.SeedHash(fieldKey, ("external", "value"));
        fieldClient.SetFieldTtl(fieldKey, "external", 1_000);
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(
            () => fieldProvider.NegotiateAsync(fieldCandidate));
    }

    [Fact]
    public async Task ColdStoreDetectsExpiringHashFieldsFromCurrentServerVersion()
    {
        var (provider, client, options) = CreateProvider();
        var key = ProjectKey(options, "project-a");
        client.SeedHash(key, ("external", "value"));
        client.SetFieldTtl(key, "external", 1_000);

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(
            () => provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a")));
    }

    [Fact]
    public async Task OperationalReadDetectsHashFieldExpiryAfterRedisUpgrade()
    {
        var (provider, client, options) = CreateProvider();
        client.Snapshot = client.Snapshot with
        {
            Server = client.Snapshot.Server.SetItem("redis_version", "7.0.0")
        };
        await provider.NegotiateAsync(Candidate(provider, options));

        var key = ProjectKey(options, "project-a");
        client.SeedHash(key, ("external", "value"));
        client.SetFieldTtl(key, "external", 1_000);
        client.Snapshot = client.Snapshot with
        {
            Server = client.Snapshot.Server.SetItem("redis_version", "7.4.0")
        };

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(
            () => provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a")));
    }

    [Fact]
    public void OptionsRequireTlsAndPairedCredentialsAndRedactSecrets()
    {
        var options = Options() with { UserName = "memory-user", Password = "memory-password" };
        options.Validate();

        var connection = options.CreateConnectionOptions();
        Assert.True(connection.Ssl);
        Assert.Equal(options.Endpoint.Host, connection.SslHost);
        Assert.Equal("memory-user", connection.User);
        Assert.Equal("memory-password", connection.Password);
        Assert.DoesNotContain("memory-user", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("memory-password", options.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            (options with { Endpoint = new Uri("redis://redis.example.test:6380/") }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (options with { Endpoint = new Uri("rediss://redis.example.test/") }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (options with { Password = null }).Validate());
    }

    [Fact]
    public async Task NegotiationRejectsMalformedStreamPayload()
    {
        var (provider, client, options) = CreateProvider();
        var key = ProjectKey(options, "project-a");
        var streamId = "knowledge/project-a/run-a";
        var streamHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(streamId)))
            .ToLowerInvariant();
        var documentId = $"stream:{streamHash}";
        var document = new KnowledgeMemoryDocument(
            documentId,
            "project-a",
            "stream",
            StreamId: streamId);
        client.SeedHash(
            key,
            ($"doc:{documentId}", JsonSerializer.Serialize(document, JsonOptions)),
            ($"etag:{documentId}", "etag"),
            ($"lease:{documentId}", "0"));

        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(
            () => provider.NegotiateAsync(Candidate(provider, options)));
    }

    [Fact]
    public async Task ProjectScanRejectsRecordWithoutRecordPayload()
    {
        var (provider, client, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        var documentId = $"record:{Guid.NewGuid():N}";
        var document = new KnowledgeMemoryDocument(documentId, "project-a", "record");
        client.SeedHash(
            ProjectKey(options, "project-a"),
            ($"doc:{documentId}", JsonSerializer.Serialize(document, JsonOptions)),
            ($"etag:{documentId}", "etag"),
            ($"lease:{documentId}", "0"));

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(
            () => provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a")));
    }

    [Fact]
    public async Task WriteBatchPrevalidatesConflictsBeforeItsSingleMutation()
    {
        var (provider, client, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        var store = new RedisMemoryDocumentStore(client, options);
        var key = ProjectKey(options, "project-a");
        var existing = new KnowledgeMemoryDocument("existing", "project-a", "record");
        client.SeedHash(
            key,
            ("doc:existing", JsonSerializer.Serialize(existing, JsonOptions)),
            ("etag:existing", "existing-etag"),
            ("lease:existing", "0"));

        var result = await store.ExecuteBatchAsync(
            "project-a",
            [
                new MemoryBatchOperation(
                    MemoryBatchOperationKind.Create,
                    new KnowledgeMemoryDocument("new", "project-a", "record")),
                new MemoryBatchOperation(MemoryBatchOperationKind.Create, existing)
            ],
            CancellationToken.None);

        Assert.Equal(MemoryBatchStatus.Conflict, result.Status);
        Assert.Null(client.ReadField(key, "doc:new"));
        Assert.Equal(1, client.LastBatchScript!.Split(
            "redis.call('HSET'", StringSplitOptions.None).Length - 1);
        var batchScript = client.LastBatchScript.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.EndsWith(
            "redis.call('HSET', KEYS[1], unpack(writes))\nreturn 1",
            batchScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostBatchReplyReplaysAfterProviderRestartAndKeepsProjectsScoped()
    {
        var (provider, client, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        var input = CreateInput("project-a", KnowledgeRecordKind.Memory, "remember me");
        client.DropNextBatchResponseAfterCommit = true;

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(
            () => provider.CreateAsync(input, "create-memory"));

        var restartedProvider = new RedisMemoryProvider(
            new RedisMemoryDocumentStore(client, options), options);
        await restartedProvider.NegotiateAsync(Candidate(restartedProvider, options));
        var replay = await restartedProvider.CreateAsync(input, "create-memory");

        Assert.Equal(KnowledgeWriteStatus.Created, replay.Status);
        Assert.True(replay.IsDuplicate);
        Assert.Null(await restartedProvider.ReadAsync("project-b", replay.Record!.RecordId));
        Assert.Equal(3, client.CountDocumentFields(ProjectKey(options, "project-a")));
        Assert.Single((await restartedProvider.SearchAsync(
            new KnowledgeRecordQuery("project-a", "agent-a"))).Items);
    }

    [Fact]
    public async Task DecisionLifecycleUsesPersistentRedisGraphAndAuditableRevisions()
    {
        var (provider, _, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        var proposal = await provider.CreateAsync(
            CreateInput("project-a", KnowledgeRecordKind.Proposal, "promote me"),
            "decision-lifecycle-proposal");
        var promotion = await provider.PromoteProposalAsync(
            "project-a",
            "run-a",
            proposal.Record!.RecordId,
            proposal.Record.Revision,
            ActorFingerprint,
            Authorization(),
            "decision-lifecycle-promotion");
        var decision = Assert.IsType<KnowledgeRecord>(promotion.Decision);

        var archived = await provider.UpdateAsync(
            DecisionUpdate(decision, KnowledgeRecordState.Archived),
            "decision-lifecycle-archive");
        var restored = await provider.RestoreAsync(
            new KnowledgeRecordRestore(
                "project-a",
                decision.RecordId,
                archived.Record!.Revision,
                1,
                ActorFingerprint,
                "restore decision"),
            "decision-lifecycle-restore");
        var approved = await provider.ApproveDecisionAsync(
            new KnowledgeDecisionApproval(
                "project-a",
                decision.RecordId,
                restored.Record!.Revision,
                ActorFingerprint,
                "approve restored decision"),
            "decision-lifecycle-approve");

        Assert.Equal(KnowledgeTrustState.Approved, approved.Record!.TrustState);
        var history = await provider.ReadRevisionsAsync("project-a", decision.RecordId, 1, 10);
        Assert.Equal(
            ["decision_approved", "decision_restored", "decision_archived", "created"],
            history.Items.Select(revision => revision.ChangeKind));
    }

    [Fact]
    public async Task RedisTransferPreservesHistoryAndReplaysImport()
    {
        var (source, _, options) = CreateProvider();
        await source.NegotiateAsync(Candidate(source, options));
        var created = await source.CreateAsync(
            CreateInput("project-a", KnowledgeRecordKind.Memory, "before transfer"),
            "transfer-create");
        var updated = await source.UpdateAsync(
            UpdateInput(created.Record!.RecordId, "after transfer"),
            "transfer-update");
        var bundle = await source.ExportAsync("project-a", "agent-a");

        var destinationClient = new FakeRedisMemoryCommandClient();
        var destination = new RedisMemoryProvider(
            new RedisMemoryDocumentStore(destinationClient, options), options);
        await destination.NegotiateAsync(Candidate(destination, options));
        var imported = await destination.ImportAsync(
            bundle, "run-import", ActorFingerprint, "transfer-import");
        var restarted = new RedisMemoryProvider(
            new RedisMemoryDocumentStore(destinationClient, options), options);
        await restarted.NegotiateAsync(Candidate(restarted, options));
        var replay = await restarted.ImportAsync(
            bundle, "run-import", ActorFingerprint, "transfer-import");
        var importedRecord = Assert.Single(imported.Records);
        var history = await restarted.ReadRevisionsAsync("project-a", importedRecord.RecordId, 1, 10);

        Assert.True(replay.IsDuplicate);
        Assert.Equal(updated.Record!.Content, importedRecord.Content);
        Assert.Equal(KnowledgeTrustState.Pending, importedRecord.TrustState);
        Assert.Equal(3, history.TotalCount);
        Assert.Equal("imported", history.Items[0].ChangeKind);
    }

    [Fact]
    public async Task ConcurrentExpectedRevisionWritesUseRedisCompareAndSwap()
    {
        var (provider, client, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        var created = await provider.CreateAsync(
            CreateInput("project-a", KnowledgeRecordKind.Memory, "original"),
            "create-memory");

        var first = provider.UpdateAsync(UpdateInput(created.Record!.RecordId, "first"), "update-first");
        var second = provider.UpdateAsync(UpdateInput(created.Record.RecordId, "second"), "update-second");
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Updated);
        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Stale);
        Assert.Equal(2, (await provider.ReadRevisionsAsync(
            "project-a", created.Record.RecordId, 1, 10)).TotalCount);
        Assert.Equal(5, client.CountDocumentFields(ProjectKey(options, "project-a")));
    }

    [Fact]
    public async Task AcknowledgmentUsesRedisServerTimeForLeaseFencing()
    {
        var localClock = new ManualTimeProvider(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (provider, client, options) = CreateProvider(localClock);
        await provider.NegotiateAsync(Candidate(provider, options));
        var proposal = await provider.CreateAsync(
            CreateInput("project-a", KnowledgeRecordKind.Proposal, "promote me"),
            "create-proposal");
        var promotion = await provider.PromoteProposalAsync(
            "project-a",
            "run-a",
            proposal.Record!.RecordId,
            1,
            ActorFingerprint,
            Authorization(),
            "promote-proposal");
        var receiptId = Assert.IsType<Guid>(promotion.OutboxEventId);
        var lease = await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-a", TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);

        client.AdvanceServerTime(TimeSpan.FromSeconds(61));
        Assert.False(await provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, lease.LeaseToken));
    }

    [Fact]
    public async Task BackendLossFailsExplicitlyInsteadOfFallingBack()
    {
        var (provider, client, options) = CreateProvider();
        await provider.NegotiateAsync(Candidate(provider, options));
        client.FailServerInspection = true;

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(
            () => provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a")));
    }

    private static (RedisMemoryProvider Provider, FakeRedisMemoryCommandClient Client, RedisMemoryOptions Options)
        CreateProvider(TimeProvider? timeProvider = null)
    {
        var options = Options();
        var client = new FakeRedisMemoryCommandClient();
        var store = new RedisMemoryDocumentStore(client, options);
        return (new RedisMemoryProvider(store, options, timeProvider), client, options);
    }

    private static RedisMemoryOptions Options() =>
        new(
            new Uri("rediss://redis.example.test:6380/"),
            "redis-memory",
            4,
            "redis-options-v1",
            RedisMemoryOptions.CurrentOptionsSchemaVersion,
            0,
            "agentweaver:test",
            null,
            null);

    private static ProviderCandidate Candidate(RedisMemoryProvider provider, RedisMemoryOptions options)
    {
        var result = ProviderCatalog.Create(
            [
                new ProviderRegistration(
                    provider.Descriptor,
                    true,
                    options.OptionsRevision,
                    options.OptionsSchemaVersion)
            ],
            [new ProviderSelection(ProviderSeam.Memory, RedisMemoryProvider.ProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Memory, RedisMemoryProvider.ProviderId)]);
        var catalog = Assert.IsType<ProviderCatalog>(result.Value);
        var resolution = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Memory,
            null,
            RedisMemoryProvider.AdapterVersion,
            options.OptionsSchemaVersion,
            MemoryProviderCapabilities.All));
        Assert.True(resolution.IsSuccess, resolution.Error?.Message);
        return resolution.Value!.Candidate!;
    }

    private static KnowledgeRecordCreate CreateInput(
        string projectId,
        KnowledgeRecordKind kind,
        string content) =>
        new(
            projectId,
            "agent-a",
            kind,
            "notes",
            "title",
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            kind == KnowledgeRecordKind.Proposal ? "run-a" : null,
            null,
            ActorFingerprint,
            kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created");

    private static KnowledgeRecordUpdate UpdateInput(Guid recordId, string content) =>
        new(
            "project-a",
            recordId,
            1,
            "notes",
            "title",
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            KnowledgeRecordState.Active,
            ActorFingerprint,
            "updated");

    private static KnowledgeRecordUpdate DecisionUpdate(
        KnowledgeRecord record,
        KnowledgeRecordState state) =>
        new(
            record.ProjectId,
            record.RecordId,
            record.Revision,
            record.Type,
            record.Title,
            record.Content,
            record.Rationale,
            record.Importance,
            record.Tags,
            state,
            ActorFingerprint,
            "decision state update");

    private static AcceptedEffectAuthorizationBounds Authorization() =>
        new(
            "https://identity.test/",
            "user-a",
            "tenant-a",
            "project-a",
            "run-a",
            ProjectAuthorityResourceType.Project,
            "project-a",
            1,
            1,
            1,
            1,
            "context-a");

    private static string ProjectKey(RedisMemoryOptions options, string projectId)
    {
        var projectHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(projectId)))
            .ToLowerInvariant();
        return $"{options.KeyPrefix}:project:{{{projectHash}}}";
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeRedisMemoryCommandClient : IRedisMemoryCommandClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TimeSpan> _keyTtls = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Key, string Field), long> _fieldTtls = [];
        private DateTimeOffset _serverTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public RedisMemoryServerSnapshot Snapshot { get; set; } = GoodSnapshot();
        public bool FailServerInspection { get; set; }
        public bool DropNextBatchResponseAfterCommit { get; set; }
        public string? LastBatchScript { get; private set; }

        public async Task<RedisMemoryServerSnapshot> ReadServerSnapshotAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (FailServerInspection)
                throw new IOException("Fake Redis server is unavailable.");
            return Snapshot;
        }

        public async Task<TimeSpan?> GetKeyTimeToLiveAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                return _keyTtls.TryGetValue(key, out var ttl) ? ttl : null;
        }

        public async Task<IReadOnlyList<string>> ScanKeysAsync(
            string pattern,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = pattern[..pattern.IndexOf('*')];
            lock (_gate)
            {
                var keys = _hashes.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray();
                return keys.Concat(keys).ToArray();
            }
        }

        public async Task<IReadOnlyList<RedisMemoryHashField>> ScanHashAsync(
            string key,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_hashes.TryGetValue(key, out var fields))
                    return [];
                return fields.Select(pair => new RedisMemoryHashField(pair.Key, pair.Value)).ToArray();
            }
        }

        public async Task<IReadOnlyList<string?>> ReadHashFieldsAsync(
            string key,
            IReadOnlyList<string> fields,
            bool checkFieldExpiration,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_hashes.TryGetValue(key, out var values))
                    return ["missing"];
                if (_keyTtls.ContainsKey(key) ||
                    (checkFieldExpiration && fields.Any(field =>
                        _fieldTtls.TryGetValue((key, field), out var ttl) && ttl >= 0)))
                    return ["invalid"];
                return new string?[] { "ok" }
                    .Concat(fields.Select(field => values.GetValueOrDefault(field)))
                    .ToArray();
            }
        }

        public async Task<IReadOnlyList<long>> ReadHashFieldTtlsAsync(
            string key,
            IReadOnlyList<string> fields,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                return fields.Select(field =>
                    _fieldTtls.TryGetValue((key, field), out var ttl)
                        ? ttl
                        : _hashes.TryGetValue(key, out var values) && values.ContainsKey(field) ? -1 : -2)
                    .ToArray();
        }

        public async Task<long> ExecuteBatchScriptAsync(
            string script,
            string key,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            LastBatchScript = script;
            lock (_gate)
            {
                if (_keyTtls.ContainsKey(key))
                    return -1;
                var operations = int.Parse(arguments[1], CultureInfo.InvariantCulture);
                var current = _hashes.GetValueOrDefault(key);
                var planned = new List<(string DocumentField, string DocumentJson, string ETagField, string ETag,
                    string LeaseField, string LeaseState)>(operations);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var now = _serverTime.ToUnixTimeMilliseconds();
                for (var index = 0; index < operations; index++)
                {
                    var offset = 2 + index * 10;
                    var kind = arguments[offset];
                    var documentField = arguments[offset + 1];
                    var etagField = arguments[offset + 2];
                    var leaseField = arguments[offset + 3];
                    var expectedEtag = arguments[offset + 4];
                    var nextEtag = arguments[offset + 5];
                    var documentJson = arguments[offset + 6];
                    var mutation = arguments[offset + 7];
                    var leaseToken = arguments[offset + 8];
                    var duration = long.Parse(arguments[offset + 9], CultureInfo.InvariantCulture);
                    if (!seen.Add(documentField))
                        return -1;
                    foreach (var field in new[] { documentField, etagField, leaseField })
                        if (arguments[0] == "1" &&
                            _fieldTtls.TryGetValue((key, field), out var fieldTtl) &&
                            fieldTtl >= 0)
                            return -1;

                    var currentDocument = current?.GetValueOrDefault(documentField);
                    var currentEtag = current?.GetValueOrDefault(etagField);
                    var currentLease = current?.GetValueOrDefault(leaseField);
                    if (kind == "C")
                    {
                        if (currentDocument is not null)
                            return 0;
                        if (currentEtag is not null || currentLease is not null)
                            return -1;
                    }
                    else if (kind == "R")
                    {
                        if (currentDocument is null)
                            return 0;
                        if (currentEtag is null || currentLease is null)
                            return -1;
                        if (!string.Equals(currentEtag, expectedEtag, StringComparison.Ordinal))
                            return 0;
                    }
                    else
                    {
                        return -1;
                    }

                    var nextLease = currentLease ?? "0";
                    if (mutation == "claim")
                    {
                        if (!TryReadLease(currentLease, out var deadline, out _) && currentLease != "0")
                            return -1;
                        if (currentLease != "0" && deadline > now)
                            return 0;
                        nextLease = checked(now + duration).ToString(CultureInfo.InvariantCulture) +
                            "|" + leaseToken;
                    }
                    else if (mutation is "acknowledge" or "release")
                    {
                        if (!TryReadLease(currentLease, out var deadline, out var currentToken) ||
                            !string.Equals(currentToken, leaseToken, StringComparison.Ordinal) ||
                            (mutation == "acknowledge" && deadline <= now))
                            return 0;
                        nextLease = "0";
                    }
                    else if (currentLease is not null && currentLease != "0")
                    {
                        return -1;
                    }

                    planned.Add((
                        documentField,
                        documentJson,
                        etagField,
                        nextEtag,
                        leaseField,
                        nextLease));
                }

                current ??= [];
                foreach (var operation in planned)
                {
                    current[operation.DocumentField] = operation.DocumentJson;
                    current[operation.ETagField] = operation.ETag;
                    current[operation.LeaseField] = operation.LeaseState;
                }
                _hashes[key] = current;
                if (DropNextBatchResponseAfterCommit)
                {
                    DropNextBatchResponseAfterCommit = false;
                    throw new IOException("The committed Redis response was lost.");
                }
                return 1;
            }
        }

        public void SeedHash(string key, params (string Field, string Value)[] entries)
        {
            lock (_gate)
                _hashes[key] = entries.ToDictionary(entry => entry.Field, entry => entry.Value, StringComparer.Ordinal);
        }

        public void SetKeyTtl(string key, TimeSpan ttl)
        {
            lock (_gate)
                _keyTtls[key] = ttl;
        }

        public void SetFieldTtl(string key, string field, long ttlMilliseconds)
        {
            lock (_gate)
                _fieldTtls[(key, field)] = ttlMilliseconds;
        }

        public string? ReadField(string key, string field)
        {
            lock (_gate)
                return _hashes.GetValueOrDefault(key)?.GetValueOrDefault(field);
        }

        public int CountDocumentFields(string key)
        {
            lock (_gate)
                return _hashes.GetValueOrDefault(key)?.Keys.Count(field =>
                    field.StartsWith("doc:", StringComparison.Ordinal)) ?? 0;
        }

        public void AdvanceServerTime(TimeSpan duration)
        {
            lock (_gate)
                _serverTime = _serverTime.Add(duration);
        }

        private static RedisMemoryServerSnapshot GoodSnapshot() =>
            new(
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("appendonly", "yes")
                    .Add("appendfsync", "always")
                    .Add("no-appendfsync-on-rewrite", "no")
                    .Add("maxmemory-policy", "noeviction"),
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("redis_version", "7.4.0"),
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("aof_enabled", "1")
                    .Add("aof_last_write_status", "ok"),
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("role", "master")
                    .Add("connected_slaves", "0"),
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("cluster_enabled", "0"));

        private static bool TryReadLease(string? value, out long deadline, out string token)
        {
            deadline = 0;
            token = string.Empty;
            if (value is null)
                return false;
            var separator = value.IndexOf('|');
            return separator > 0 &&
                long.TryParse(
                    value.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out deadline) &&
                (token = value[(separator + 1)..]).Length > 0;
        }
    }
}
