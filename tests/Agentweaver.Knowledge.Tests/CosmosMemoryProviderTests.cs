using System.Collections.Immutable;
using System.Net;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

public sealed class CosmosMemoryProviderTests
{
    private const string ActorFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task NegotiationValidatesConfiguredDatabaseContainerAndPartitionKey()
    {
        var options = Options();
        var store = new FakeCosmosMemoryStore(options);
        var provider = new CosmosMemoryProvider(store, options);
        var candidate = Candidate(provider, options);

        var negotiation = await provider.NegotiateAsync(candidate);

        Assert.Equal(CosmosMemoryProvider.ProviderId, negotiation.Resource.ProviderId);
        Assert.Equal(options.ResourceId, negotiation.Resource.ResourceId);
        Assert.Equal(options.ResourceGeneration, negotiation.Resource.Generation);
        Assert.Equal("/projectId", store.Identity.PartitionKeyPaths.Single());
        var incompatibleStore = new FakeCosmosMemoryStore(options)
        {
            Identity = new CosmosMemoryContainerIdentity(
                options.DatabaseId,
                options.ContainerId,
                ImmutableArray<string>.Empty,
                DefaultTimeToLiveSeconds: null,
                HasRequiredSearchCompositeIndex: true)
        };
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(async () =>
            await new CosmosMemoryProvider(incompatibleStore, options).NegotiateAsync(candidate));
    }

    [Fact]
    public async Task NegotiationRejectsExpiringOrUnindexedContainers()
    {
        var (provider, store, options) = CreateProvider();
        var candidate = Candidate(provider, options);
        store.Identity = store.Identity with { DefaultTimeToLiveSeconds = 3600 };
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(() =>
            provider.NegotiateAsync(candidate));

        store.Identity = store.Identity with
        {
            DefaultTimeToLiveSeconds = null,
            HasRequiredSearchCompositeIndex = false
        };
        await Assert.ThrowsAsync<KnowledgeProviderUnavailableException>(() =>
            provider.NegotiateAsync(candidate));
    }

    [Fact]
    public async Task CreateIsIdempotentAndRecordsAreProjectScoped()
    {
        var (provider, _, _) = CreateProvider();
        var input = CreateInput("memory", "remember this");

        var created = await provider.CreateAsync(input, "create-memory");
        var duplicate = await provider.CreateAsync(input, "create-memory");

        Assert.Equal(KnowledgeWriteStatus.Created, created.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(created.Record!.RecordId, duplicate.Record!.RecordId);
        Assert.Null(await provider.ReadAsync("project-b", created.Record.RecordId));
        Assert.Equal(created.Record.RecordId,
            (await provider.ReadAsync("project-a", created.Record.RecordId))!.RecordId);
        var revisions = await provider.ReadRevisionsAsync("project-a", created.Record.RecordId, 1, 10);
        Assert.Equal(1, revisions.TotalCount);
        Assert.Equal("created", revisions.Items[0].Reason);
        var search = await provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a"));
        Assert.Single(search.Items);
    }

    [Fact]
    public async Task ReusingIdempotencyKeyForDifferentContentConflicts()
    {
        var (provider, _, _) = CreateProvider();
        await provider.CreateAsync(CreateInput("memory", "original"), "create-memory");

        var exception = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            provider.CreateAsync(CreateInput("memory", "different"), "create-memory"));

        Assert.Equal("idempotency_conflict", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task ConcurrentExpectedRevisionUpdatesProduceOneNewRevision()
    {
        var (provider, _, _) = CreateProvider();
        var created = await provider.CreateAsync(CreateInput("memory", "original"), "create-memory");
        var recordId = created.Record!.RecordId;
        var first = provider.UpdateAsync(UpdateInput(recordId, "first"), "update-first");
        var second = provider.UpdateAsync(UpdateInput(recordId, "second"), "update-second");

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Updated);
        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Stale);
        var current = await provider.ReadAsync("project-a", recordId);
        Assert.Equal(2, current!.Revision);
        var revisions = await provider.ReadRevisionsAsync("project-a", recordId, 1, 10);
        Assert.Equal(2, revisions.TotalCount);
    }

    [Fact]
    public async Task ConcurrentIdenticalRetriesReplayCommittedUpdateRejectAndPromotion()
    {
        var (provider, store, _) = CreateProvider();
        var created = await provider.CreateAsync(CreateInput("memory", "original"), "create-memory");
        var updated = await RaceIdenticalRequestsAsync(
            store,
            () => provider.UpdateAsync(UpdateInput(created.Record!.RecordId, "updated"), "update-same"));
        Assert.Equal(KnowledgeWriteStatus.Updated, updated.Committed.Status);
        Assert.True(updated.Retry.IsDuplicate);
        Assert.Equal(updated.Committed.Record!.RevisionId, updated.Retry.Record!.RevisionId);

        var rejectedProposal = await provider.CreateAsync(
            CreateInput("proposal", "reject concurrently"), "create-reject-proposal");
        var rejected = await RaceIdenticalRequestsAsync(
            store,
            () => provider.RejectProposalAsync(
                "project-a", "run-a", rejectedProposal.Record!.RecordId, 1, ActorFingerprint, "reject-same"));
        Assert.Equal(KnowledgeWriteStatus.Updated, rejected.Committed.Status);
        Assert.True(rejected.Retry.IsDuplicate);
        Assert.Equal(rejected.Committed.Record!.RevisionId, rejected.Retry.Record!.RevisionId);

        var promotionProposal = await provider.CreateAsync(
            CreateInput("proposal", "promote concurrently"), "create-promote-proposal");
        var promotion = Promotion(promotionProposal.Record!.RecordId);
        var promoted = await RaceIdenticalRequestsAsync(
            store,
            () => provider.PromoteProposalAsync(
                promotion.ProjectId,
                promotion.RunId,
                promotionProposal.Record.RecordId,
                promotion.ExpectedRevision,
                ActorFingerprint,
                Authorization(),
                "promote-same"));
        Assert.Equal(KnowledgeWriteStatus.Updated, promoted.Committed.Status);
        Assert.True(promoted.Retry.IsDuplicate);
        Assert.Equal(promoted.Committed.OutboxEventId, promoted.Retry.OutboxEventId);
    }

    [Fact]
    public async Task PromotionAtomicallyStoresDecisionReceiptAndIntentAcrossRestart()
    {
        var (provider, store, options) = CreateProvider();
        var proposalResult = await provider.CreateAsync(
            CreateInput("proposal", "proposal content"), "create-proposal");
        var proposalId = proposalResult.Record!.RecordId;
        var promotion = Promotion(proposalId);
        store.FailNextBatch = true;

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(() =>
            provider.PromoteProposalAsync(promotion.ProjectId, promotion.RunId, proposalId,
                promotion.ExpectedRevision, ActorFingerprint, Authorization(), "promote-proposal"));
        var unchanged = await provider.ReadAsync("project-a", proposalId);
        Assert.Equal(KnowledgeRecordState.Pending, unchanged!.State);

        var promoted = await provider.PromoteProposalAsync(
            promotion.ProjectId,
            promotion.RunId,
            proposalId,
            promotion.ExpectedRevision,
            ActorFingerprint,
            Authorization(),
            "promote-proposal");
        Assert.Equal(KnowledgeWriteStatus.Updated, promoted.Status);
        Assert.NotNull(promoted.Decision);
        Assert.NotNull(promoted.OutboxEventId);

        var restartedProvider = new CosmosMemoryProvider(store, options);
        var duplicate = await restartedProvider.PromoteProposalAsync(
            promotion.ProjectId,
            promotion.RunId,
            proposalId,
            promotion.ExpectedRevision,
            ActorFingerprint,
            Authorization(),
            "promote-proposal");
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(promoted.OutboxEventId, duplicate.OutboxEventId);

        var receiptId = promoted.OutboxEventId!.Value;
        var state = await restartedProvider.ReadAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId);
        Assert.NotNull(state);
        Assert.False(state.IsDelivered);
        Assert.Equal("project-a", state.Receipt.ProjectId);
        Assert.Equal("run-a", state.Receipt.RunId);
        var lease = await restartedProvider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-1", TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);
        Assert.False(await restartedProvider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-b", "run-a", receiptId, lease.LeaseToken));
        Assert.True(await restartedProvider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, lease.LeaseToken));
        Assert.True((await restartedProvider.ReadAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId))!.IsDelivered);
    }

    [Fact]
    public async Task DeliveryClaimsPreserveProjectRunOrder()
    {
        var (provider, _, _) = CreateProvider();
        var first = await CreateProposalAsync(provider, "first");
        var second = await CreateProposalAsync(provider, "second");
        var firstResult = await PromoteAsync(provider, first);
        var secondResult = await PromoteAsync(provider, second);
        var firstId = firstResult.OutboxEventId!.Value;
        var secondId = secondResult.OutboxEventId!.Value;

        Assert.Null(await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", secondId, "worker-2", TimeSpan.FromSeconds(30)));
        var firstLease = await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", firstId, "worker-1", TimeSpan.FromSeconds(30));
        Assert.NotNull(firstLease);
        Assert.True(await provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", firstId, firstLease.LeaseToken));
        Assert.NotNull(await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", secondId, "worker-2", TimeSpan.FromSeconds(30)));
    }

    private static async Task<Guid> CreateProposalAsync(CosmosMemoryProvider provider, string key)
    {
        var result = await provider.CreateAsync(CreateInput("proposal", key), $"create-{key}");
        return result.Record!.RecordId;
    }

    private static Task<KnowledgeProposalPromotionResult> PromoteAsync(
        CosmosMemoryProvider provider,
        Guid proposalId) =>
        provider.PromoteProposalAsync(
            "project-a",
            "run-a",
            proposalId,
            1,
            ActorFingerprint,
            Authorization(),
            $"promote-{proposalId:N}");

    private static (CosmosMemoryProvider Provider, FakeCosmosMemoryStore Store, CosmosMemoryOptions Options)
        CreateProvider()
    {
        var options = Options();
        var store = new FakeCosmosMemoryStore(options);
        return (new CosmosMemoryProvider(store, options), store, options);
    }

    private static CosmosMemoryOptions Options() =>
        new(
            new Uri("https://memory.documents.azure.com/"),
            "agentweaver",
            "knowledge",
            "cosmos-memory",
            3,
            "cosmos-options-v1",
            CosmosMemoryOptions.CurrentOptionsSchemaVersion);

    private static ProviderCandidate Candidate(
        CosmosMemoryProvider provider,
        CosmosMemoryOptions options)
    {
        var catalogResult = ProviderCatalog.Create(
            [
                new ProviderRegistration(
                    provider.Descriptor,
                    true,
                    options.OptionsRevision,
                    options.OptionsSchemaVersion)
            ],
            [new ProviderSelection(ProviderSeam.Memory, CosmosMemoryProvider.ProviderId)],
            [new ProviderOverridePermission(ProviderSeam.Memory, CosmosMemoryProvider.ProviderId)]);
        var catalog = Assert.IsType<ProviderCatalog>(catalogResult.Value);
        var resolution = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Memory,
            null,
            CosmosMemoryProvider.AdapterVersion,
            options.OptionsSchemaVersion,
            MemoryProviderCapabilities.All));
        Assert.True(resolution.IsSuccess, resolution.Error?.Message);
        return resolution.Value!.Candidate!;
    }

    private static KnowledgeRecordCreate CreateInput(string kind, string content) =>
        new(
            "project-a",
            "agent-a",
            kind == "proposal" ? KnowledgeRecordKind.Proposal : KnowledgeRecordKind.Memory,
            "notes",
            "title",
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            kind == "proposal" ? "run-a" : null,
            null,
            ActorFingerprint,
            kind == "proposal" ? "proposal_created" : "created");

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

    private static (string ProjectId, string RunId, int ExpectedRevision) Promotion(Guid proposalId) =>
        ("project-a", "run-a", 1);

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

    private static async Task<(T Committed, T Retry)> RaceIdenticalRequestsAsync<T>(
        FakeCosmosMemoryStore store,
        Func<Task<T>> operation)
    {
        store.PauseNextRecordRead();
        var delayed = operation();
        T committed;
        try
        {
            await store.PausedRecordRead.WaitAsync(TimeSpan.FromSeconds(10));
            committed = await operation();
        }
        finally
        {
            store.ResumePausedRecordRead();
        }

        return (committed, await delayed);
    }

    private sealed class FakeCosmosMemoryStore(CosmosMemoryOptions options) : ICosmosMemoryDocumentStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string ProjectId, string Id), CosmosMemoryStoredDocument> _documents = [];
        private long _etag;
        private int _pauseNextRecordRead;
        private TaskCompletionSource? _pausedRecordRead;
        private TaskCompletionSource? _resumeRecordRead;

        public CosmosMemoryContainerIdentity Identity { get; set; } = new(
            options.DatabaseId,
            options.ContainerId,
            [CosmosMemoryOptions.PartitionKeyPath],
            DefaultTimeToLiveSeconds: null,
            HasRequiredSearchCompositeIndex: true);

        public bool FailNextBatch { get; set; }
        public Task PausedRecordRead =>
            _pausedRecordRead?.Task ?? throw new InvalidOperationException("No record read is paused.");

        public void PauseNextRecordRead()
        {
            _pausedRecordRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeRecordRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pauseNextRecordRead, 1);
        }

        public void ResumePausedRecordRead() => _resumeRecordRead?.TrySetResult();

        public Task<CosmosMemoryContainerIdentity> ReadContainerIdentityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Identity);
        }

        public async Task<CosmosMemoryStoredDocument?> ReadAsync(
            string projectId,
            string documentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documentId.StartsWith("record:", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _pauseNextRecordRead, 0) == 1)
            {
                _pausedRecordRead!.TrySetResult();
                await _resumeRecordRead!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            lock (_gate)
                return _documents.GetValueOrDefault((projectId, documentId));
        }

        public Task<IReadOnlyList<CosmosMemoryDocument>> FindAcceptedEffectAsync(
            Guid receiptId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<CosmosMemoryDocument> matches = _documents.Values
                    .Select(item => item.Document)
                    .Where(item => item.DocumentType == "accepted-effect" &&
                        item.Receipt?.ReceiptId == receiptId)
                    .Take(2)
                    .ToArray();
                return Task.FromResult(matches);
            }
        }

        public Task<KnowledgeRecordPage> SearchAsync(
            KnowledgeRecordQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var items = _documents.Values
                    .Select(item => item.Document)
                    .Where(item => item.DocumentType == "record" && item.Record is not null)
                    .Select(item => item.Record!)
                    .Where(record => record.ProjectId == query.ProjectId && record.AgentId == query.AgentId)
                    .Where(record => query.Kind is null || record.Kind == query.Kind)
                    .Where(record => query.IncludeInactive || record.State == KnowledgeRecordState.Active)
                    .Where(record => string.IsNullOrWhiteSpace(query.Query) ||
                        SearchableText(record).Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => record.UpdatedAt)
                    .ThenBy(record => record.RecordId)
                    .ToArray();
                var page = items.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToImmutableArray();
                return Task.FromResult(new KnowledgeRecordPage(
                    page, items.Length, query.Page, query.PageSize));
            }
        }

        public Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
            string projectId,
            Guid recordId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var revisions = _documents.Values
                    .Select(item => item.Document)
                    .Where(item => item.DocumentType == "revision" &&
                        item.RecordId == recordId &&
                        item.RecordRevision?.RecordId == recordId &&
                        item.ProjectId == projectId)
                    .Select(item => item.RecordRevision!)
                    .OrderByDescending(item => item.Revision)
                    .ToArray();
                return Task.FromResult(new KnowledgeRecordRevisionPage(
                    revisions.Skip((page - 1) * pageSize).Take(pageSize).ToImmutableArray(),
                    revisions.Length,
                    page,
                    pageSize));
            }
        }

        public Task<IReadOnlyList<KnowledgeRecord>> ReadContextCandidatesAsync(
            string projectId,
            string agentId,
            string runId,
            int maximumRecords,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<KnowledgeRecord> records = _documents.Values
                    .Select(item => item.Document)
                    .Where(item => item.DocumentType == "record" && item.Record is not null)
                    .Select(item => item.Record!)
                    .Where(record => record.ProjectId == projectId &&
                        ((record.Kind == KnowledgeRecordKind.Decision &&
                            record.State == KnowledgeRecordState.Active &&
                            record.TrustState == KnowledgeTrustState.Approved) ||
                         (record.Kind == KnowledgeRecordKind.Memory &&
                            record.State == KnowledgeRecordState.Active &&
                            (record.TrustState == KnowledgeTrustState.Pending ||
                                record.TrustState == KnowledgeTrustState.Approved) &&
                            (record.AgentId == agentId ||
                                (record.TrustState == KnowledgeTrustState.Approved &&
                                 record.Tags.Any(tag => tag.Equals(
                                     "cross-team", StringComparison.OrdinalIgnoreCase))))) ||
                         (record.Kind == KnowledgeRecordKind.SessionContext &&
                            record.State == KnowledgeRecordState.Active &&
                            record.TrustState == KnowledgeTrustState.Approved &&
                            record.AgentId == agentId &&
                            record.SourceRunId == runId)))
                    .Take(maximumRecords + 1)
                    .ToArray();
                return Task.FromResult(records);
            }
        }

        public Task<bool> HasUndeliveredPredecessorAsync(
            string projectId,
            string streamId,
            long sequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                return Task.FromResult(_documents.Values.Any(item =>
                    item.Document.ProjectId == projectId &&
                    item.Document.DocumentType == "accepted-effect" &&
                    item.Document.StreamId == streamId &&
                    item.Document.Sequence < sequence &&
                    !item.Document.IsDelivered));
        }

        public Task<CosmosMemoryBatchResult> ExecuteBatchAsync(
            string projectId,
            IReadOnlyList<CosmosMemoryBatchOperation> operations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailNextBatch)
                {
                    FailNextBatch = false;
                    return Task.FromResult(new CosmosMemoryBatchResult(HttpStatusCode.ServiceUnavailable));
                }
                foreach (var operation in operations)
                {
                    var key = (projectId, operation.Document.Id);
                    var existing = _documents.GetValueOrDefault(key);
                    if (operation.Kind == CosmosMemoryBatchOperationKind.Create && existing is not null)
                        return Task.FromResult(new CosmosMemoryBatchResult(HttpStatusCode.Conflict));
                    if (operation.Kind == CosmosMemoryBatchOperationKind.Replace &&
                        (existing is null || !string.Equals(existing.ETag, operation.ETag, StringComparison.Ordinal)))
                        return Task.FromResult(new CosmosMemoryBatchResult(
                            existing is null ? HttpStatusCode.NotFound : HttpStatusCode.PreconditionFailed));
                }
                foreach (var operation in operations)
                    _documents[(projectId, operation.Document.Id)] = new CosmosMemoryStoredDocument(
                        operation.Document, Interlocked.Increment(ref _etag).ToString());
                return Task.FromResult(new CosmosMemoryBatchResult(HttpStatusCode.OK));
            }
        }

        private static string SearchableText(KnowledgeRecord record) =>
            string.Join(' ', record.Type, record.Title, record.Content, string.Join(' ', record.Tags));
    }
}
