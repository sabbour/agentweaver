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
    public async Task DecisionSupersessionChecksCyclesAndRecordsTheReplacement()
    {
        var (provider, _, _) = CreateProvider();
        var first = await CreateDecisionAsync(provider, "decision-first");
        var replacement = await CreateDecisionAsync(provider, "decision-replacement");
        var cycle = await provider.UpdateAsync(
            DecisionUpdate(first, KnowledgeRecordState.Superseded, first.RecordId),
            "decision-self-cycle");
        Assert.Equal(KnowledgeWriteStatus.ReplacementCycle, cycle.Status);

        var superseded = await provider.UpdateAsync(
            DecisionUpdate(first, KnowledgeRecordState.Superseded, replacement.RecordId),
            "decision-supersede");
        Assert.Equal(KnowledgeWriteStatus.Updated, superseded.Status);
        Assert.Equal(KnowledgeRecordState.Superseded, superseded.Record!.State);
        Assert.Equal(replacement.RecordId, superseded.Record.SupersededByRecordId);

        var reverseCycle = await provider.UpdateAsync(
            DecisionUpdate(replacement, KnowledgeRecordState.Superseded, first.RecordId),
            "decision-reverse-cycle");
        Assert.Equal(KnowledgeWriteStatus.ReplacementCycle, reverseCycle.Status);
        Assert.Equal(KnowledgeRecordState.Active,
            (await provider.ReadAsync("project-a", replacement.RecordId))!.State);

        var revisions = await provider.ReadRevisionsAsync("project-a", first.RecordId, 1, 10);
        Assert.Equal(2, revisions.TotalCount);
        Assert.Equal("decision_superseded", revisions.Items[0].ChangeKind);
        Assert.Equal(ActorFingerprint, revisions.Items[0].ActorFingerprint);
        Assert.Equal(replacement.RecordId, revisions.Items[0].SupersededByRecordId);
    }

    [Fact]
    public async Task ConcurrentDecisionSupersessionCannotCommitACycle()
    {
        var (provider, store, _) = CreateProvider();
        var first = await CreateDecisionAsync(provider, "concurrent-first");
        var second = await CreateDecisionAsync(provider, "concurrent-second");
        store.PauseNextBatch();
        var delayed = provider.UpdateAsync(
            DecisionUpdate(first, KnowledgeRecordState.Superseded, second.RecordId),
            "concurrent-first-supersede");
        KnowledgeRecordWriteResult committed;
        try
        {
            await store.PausedBatch.WaitAsync(TimeSpan.FromSeconds(10));
            committed = await provider.UpdateAsync(
                DecisionUpdate(second, KnowledgeRecordState.Superseded, first.RecordId),
                "concurrent-second-supersede");
        }
        finally
        {
            store.ResumePausedBatch();
        }

        Assert.Equal(KnowledgeWriteStatus.Updated, committed.Status);
        Assert.Equal(KnowledgeWriteStatus.ReplacementCycle, (await delayed).Status);
        Assert.Equal(KnowledgeRecordState.Active,
            (await provider.ReadAsync("project-a", first.RecordId))!.State);
        Assert.Equal(KnowledgeRecordState.Superseded,
            (await provider.ReadAsync("project-a", second.RecordId))!.State);
    }

    [Fact]
    public async Task DecisionSupersessionRejectsForeignAgentTargetsThroughoutTheChain()
    {
        var (provider, store, _) = CreateProvider();
        async Task<KnowledgeRecord> CreateDecisionInAgentAsync(string agentId, string key)
        {
            var proposal = await provider.CreateAsync(
                CreateInput("proposal", key) with { AgentId = agentId }, $"create-{key}");
            var promoted = await PromoteAsync(provider, proposal.Record!.RecordId);
            return promoted.Decision!;
        }

        var source = await CreateDecisionInAgentAsync("agent-a", "foreign-direct-source");
        var foreignTarget = await CreateDecisionInAgentAsync("agent-b", "foreign-direct-target");
        var direct = await provider.UpdateAsync(
            DecisionUpdate(source, KnowledgeRecordState.Superseded, foreignTarget.RecordId),
            "foreign-direct-replacement");
        Assert.Equal(KnowledgeWriteStatus.InvalidReplacement, direct.Status);

        var transitiveSource = await CreateDecisionInAgentAsync("agent-a", "foreign-chain-source");
        var sameAgentTarget = await CreateDecisionInAgentAsync("agent-a", "foreign-chain-target");
        var foreignDownstream = await CreateDecisionInAgentAsync("agent-b", "foreign-chain-downstream");
        store.SetRecord(sameAgentTarget with
        {
            State = KnowledgeRecordState.Superseded,
            SupersededByRecordId = foreignDownstream.RecordId
        });

        var transitive = await provider.UpdateAsync(
            DecisionUpdate(transitiveSource, KnowledgeRecordState.Superseded, sameAgentTarget.RecordId),
            "foreign-transitive-replacement");

        Assert.Equal(KnowledgeWriteStatus.InvalidReplacement, transitive.Status);
        Assert.Equal(KnowledgeRecordState.Active,
            (await provider.ReadAsync("project-a", transitiveSource.RecordId))!.State);
    }

    [Fact]
    public async Task DecisionArchiveRestoreAndApprovalAppendAuditableRevisions()
    {
        var (provider, store, options) = CreateProvider();
        var decision = await CreateDecisionAsync(provider, "decision-restore");
        var archived = await provider.UpdateAsync(
            DecisionUpdate(decision, KnowledgeRecordState.Archived, null),
            "decision-archive");
        Assert.Equal(KnowledgeRecordState.Archived, archived.Record!.State);

        var restoreInput = new KnowledgeRecordRestore(
            "project-a", decision.RecordId, archived.Record.Revision, 1, ActorFingerprint, "restore original");
        var restored = await provider.RestoreAsync(restoreInput, "decision-restore");
        var restarted = new CosmosMemoryProvider(store, options);
        var duplicateRestore = await restarted.RestoreAsync(restoreInput, "decision-restore");
        Assert.Equal(KnowledgeWriteStatus.Updated, restored.Status);
        Assert.Equal(KnowledgeRecordState.Active, restored.Record!.State);
        Assert.Equal(KnowledgeTrustState.Pending, restored.Record.TrustState);
        Assert.True(duplicateRestore.IsDuplicate);
        Assert.Equal(restored.Record.RevisionId, duplicateRestore.Record!.RevisionId);

        var approved = await restarted.ApproveDecisionAsync(
            new KnowledgeDecisionApproval(
                "project-a", decision.RecordId, restored.Record.Revision, ActorFingerprint, "approve restored"),
            "decision-approve");
        Assert.Equal(KnowledgeTrustState.Approved, approved.Record!.TrustState);
        var history = await restarted.ReadRevisionsAsync("project-a", decision.RecordId, 1, 10);
        Assert.Equal(4, history.TotalCount);
        Assert.Equal("decision_approved", history.Items[0].ChangeKind);
        Assert.Equal("decision_restored", history.Items[1].ChangeKind);
        Assert.Equal("decision_archived", history.Items[2].ChangeKind);
        Assert.Equal(ActorFingerprint, history.Items[1].ActorFingerprint);
        Assert.Equal("restore original", history.Items[1].Reason);
    }

    [Fact]
    public async Task TransferPreservesHistoryAndImportReplaysAfterProviderRestart()
    {
        var (source, _, options) = CreateProvider();
        var memory = await source.CreateAsync(CreateInput("memory", "before transfer"), "transfer-memory");
        var updated = await source.UpdateAsync(
            UpdateInput(memory.Record!.RecordId, "after transfer"), "transfer-memory-update");
        var decision = await CreateDecisionAsync(source, "transfer-decision");
        var replacement = await CreateDecisionAsync(source, "transfer-replacement");
        var superseded = await source.UpdateAsync(
            DecisionUpdate(decision, KnowledgeRecordState.Superseded, replacement.RecordId),
            "transfer-decision-supersede");
        var foreignProposal = await source.CreateAsync(
            CreateInput("proposal", "foreign-agent-decision") with { AgentId = "agent-b" },
            "foreign-agent-proposal");
        var foreignDecision = await source.PromoteProposalAsync(
            "project-a",
            "run-a",
            foreignProposal.Record!.RecordId,
            1,
            ActorFingerprint,
            Authorization(),
            "foreign-agent-promotion");
        Assert.NotNull(foreignDecision.Decision);
        var bundle = await source.ExportAsync("project-a", "agent-a");
        Assert.Equal(KnowledgeRecordTransferContract.Format, bundle.Format);
        Assert.Equal(3, bundle.Records.Length);
        Assert.Equal(KnowledgeRecordState.Superseded, superseded.Record!.State);
        Assert.Equal(2, bundle.Records.Single(entry => entry.Record.Kind == KnowledgeRecordKind.Memory)
            .Revisions.Length);

        var destinationStore = new FakeCosmosMemoryStore(options);
        var destination = new CosmosMemoryProvider(destinationStore, options);
        var mismatchedScope = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            destination.ImportAsync(
                bundle with { AgentId = "agent-b" }, "run-import", ActorFingerprint, "wrong-transfer-scope"));
        Assert.Equal("invalid_knowledge_transfer", mismatchedScope.Code);
        var imported = await destination.ImportAsync(bundle, "run-import", ActorFingerprint, "transfer-import");
        var restarted = new CosmosMemoryProvider(destinationStore, options);
        var duplicate = await restarted.ImportAsync(
            bundle, "run-import", ActorFingerprint, "transfer-import");
        Assert.Equal(3, imported.Records.Length);
        Assert.True(duplicate.IsDuplicate);
        Assert.All(imported.Records, record =>
        {
            Assert.Equal(KnowledgeRecordState.Active, record.State);
            Assert.Equal(KnowledgeTrustState.Pending, record.TrustState);
            Assert.Null(record.SupersededByRecordId);
        });
        var importedMemory = imported.Records.Single(record => record.Kind == KnowledgeRecordKind.Memory);
        Assert.Equal(updated.Record!.Content, importedMemory.Content);
        Assert.Equal("run-import", importedMemory.SourceRunId);
        Assert.Equal(KnowledgeTrustState.Pending, importedMemory.TrustState);
        var history = await restarted.ReadRevisionsAsync("project-a", importedMemory.RecordId, 1, 10);
        Assert.Equal(3, history.TotalCount);
        Assert.Equal(bundle.Records.Single(entry => entry.Record.Kind == KnowledgeRecordKind.Memory)
            .Revisions.Select(revision => revision.RevisionId), history.Items
            .Where(revision => revision.ChangeKind != "imported")
            .OrderBy(revision => revision.Revision)
            .Select(revision => revision.RevisionId));
        Assert.Equal("imported", history.Items[0].ChangeKind);
        Assert.Equal(ActorFingerprint, history.Items[0].ActorFingerprint);
        var importedDecision = imported.Records.Single(record => record.RecordId == decision.RecordId);
        var decisionHistory = await restarted.ReadRevisionsAsync(
            "project-a", importedDecision.RecordId, 1, 10);
        Assert.Contains(decisionHistory.Items, revision =>
            revision.ChangeKind == "decision_superseded" &&
            revision.SupersededByRecordId == replacement.RecordId);
        var reexport = await restarted.ExportAsync("project-a", "agent-a");
        Assert.Equal(3, reexport.Records.Length);
        Assert.Equal("agent-a", reexport.AgentId);
    }

    [Fact]
    public async Task TransferExportAndImportAcceptLegacyRevisionsWithoutSourceProvenance()
    {
        var (source, store, options) = CreateProvider();
        var created = await source.CreateAsync(
            CreateInput("memory", "legacy Cosmos revision") with { SourceRunId = "run-a" },
            "legacy-transfer-memory");
        store.ClearRevisionProvenance("project-a", created.Record!.RecordId);

        var bundle = await source.ExportAsync("project-a", "agent-a");
        var entry = Assert.Single(bundle.Records);
        Assert.Equal("run-a", entry.Record.SourceRunId);
        Assert.Null(entry.Revisions[^1].SourceRunId);
        Assert.Null(entry.Revisions[^1].SourceSessionId);

        var destination = new CosmosMemoryProvider(new FakeCosmosMemoryStore(options), options);
        var imported = await destination.ImportAsync(
            bundle, "run-import", ActorFingerprint, "legacy-transfer-import");

        var record = Assert.Single(imported.Records);
        Assert.Equal(KnowledgeRecordState.Active, record.State);
        Assert.Equal(KnowledgeTrustState.Pending, record.TrustState);
        Assert.Equal("run-import", record.SourceRunId);
    }

    [Fact]
    public async Task TransferRejectsNullCollectionsAndMalformedHistoricalSnapshots()
    {
        var (source, _, options) = CreateProvider();
        var created = await source.CreateAsync(
            CreateInput("memory", "before transfer"), "malformed-transfer-memory");
        await source.UpdateAsync(
            UpdateInput(created.Record!.RecordId, "current head"), "malformed-transfer-update");
        var bundle = await source.ExportAsync("project-a", "agent-a");
        var entry = Assert.Single(bundle.Records);
        var destination = new CosmosMemoryProvider(new FakeCosmosMemoryStore(options), options);

        async Task AssertInvalidAsync(KnowledgeRecordTransferBundle input, string key)
        {
            var exception = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
                destination.ImportAsync(input, "run-import", ActorFingerprint, key));
            Assert.Equal("invalid_knowledge_transfer", exception.Code);
            Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        }

        await AssertInvalidAsync(bundle with { Records = default }, "transfer-missing-records");
        await AssertInvalidAsync(
            bundle with
            {
                Records = bundle.Records.SetItem(
                    0,
                    entry with { Revisions = entry.Revisions.SetItem(entry.Revisions.Length - 1, null!) })
            },
            "transfer-null-revision");

        var firstRevision = entry.Revisions[0];
        var invalidSnapshots = new[]
        {
            firstRevision with { Content = null! },
            firstRevision with { Type = new string('x', 65) },
            firstRevision with { Importance = "critical" }
        };
        for (var index = 0; index < invalidSnapshots.Length; index++)
        {
            var malformedEntry = entry with
            {
                Revisions = entry.Revisions.SetItem(0, invalidSnapshots[index])
            };
            await AssertInvalidAsync(
                bundle with { Records = bundle.Records.SetItem(0, malformedEntry) },
                $"transfer-invalid-history-{index}");
        }
    }

    [Fact]
    public async Task TransferRejectsSelfSupersessionInHistoricalRevision()
    {
        var (source, _, options) = CreateProvider();
        var decision = await CreateDecisionAsync(source, "self-cycle-transfer");
        var bundle = await source.ExportAsync("project-a", "agent-a");
        var original = Assert.Single(bundle.Records);
        var superseded = SupersedeTransferEntry(original, original.Record.RecordId);
        var previous = superseded.Revisions[^1];
        var restoredRevision = previous with
        {
            Revision = previous.Revision + 1,
            RevisionId = Guid.NewGuid(),
            PreviousRevisionId = previous.RevisionId,
            State = KnowledgeRecordState.Active,
            TrustState = KnowledgeTrustState.Pending,
            SupersededByRecordId = null,
            ChangeKind = "decision_restored",
            Reason = "restored"
        };
        var restored = superseded with
        {
            Record = superseded.Record with
            {
                Revision = restoredRevision.Revision,
                PreviousRevisionId = previous.RevisionId,
                RevisionId = restoredRevision.RevisionId,
                State = KnowledgeRecordState.Active,
                TrustState = KnowledgeTrustState.Pending,
                SupersededByRecordId = null
            },
            Revisions = superseded.Revisions.Add(restoredRevision)
        };
        var invalidBundle = bundle with { Records = bundle.Records.Replace(original, restored) };
        var destination = new CosmosMemoryProvider(new FakeCosmosMemoryStore(options), options);

        var exception = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            destination.ImportAsync(invalidBundle, "run-import", ActorFingerprint, "transfer-self-cycle"));

        Assert.Equal("invalid_knowledge_transfer", exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal(KnowledgeRecordState.Active, decision.State);
    }

    [Fact]
    public async Task TransferRejectsSupersessionTargetsFromAnotherAgent()
    {
        var (source, _, options) = CreateProvider();
        var decision = await CreateDecisionAsync(source, "foreign-transfer-source");
        var bundle = await source.ExportAsync("project-a", "agent-a");
        var original = Assert.Single(bundle.Records);
        var destination = new CosmosMemoryProvider(new FakeCosmosMemoryStore(options), options);
        var foreignProposal = await destination.CreateAsync(
            CreateInput("proposal", "foreign target") with { AgentId = "agent-b" },
            "foreign-transfer-target");
        var foreignDecision = await destination.PromoteProposalAsync(
            "project-a",
            "run-a",
            foreignProposal.Record!.RecordId,
            1,
            ActorFingerprint,
            Authorization(),
            "foreign-transfer-promotion");
        var foreignReference = SupersedeTransferEntry(original, foreignDecision.Decision!.RecordId);
        var foreignBundle = bundle with { Records = bundle.Records.Replace(original, foreignReference) };

        var exception = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            destination.ImportAsync(foreignBundle, "run-import", ActorFingerprint, "transfer-foreign-target"));

        Assert.Equal("invalid_replacement", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(KnowledgeRecordState.Active, decision.State);
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

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task BatchTimeoutLimitAndThrottleResponsesFailClosed(HttpStatusCode statusCode)
    {
        var (provider, store, _) = CreateProvider();
        store.FailNextBatchStatus = statusCode;

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(() =>
            provider.CreateAsync(CreateInput("memory", "must not be stored"), "failed-batch"));

        Assert.Empty((await provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a"))).Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PromotionBatchTimeoutLimitAndThrottleResponsesLeaveProposalUnchanged(
        HttpStatusCode statusCode)
    {
        var (provider, store, _) = CreateProvider();
        var proposalId = await CreateProposalAsync(provider, "promotion-failure");
        store.FailNextBatchStatus = statusCode;

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(() =>
            PromoteAsync(provider, proposalId));

        var proposal = await provider.ReadAsync("project-a", proposalId);
        Assert.Equal(KnowledgeRecordState.Pending, proposal!.State);
        var revisions = await provider.ReadRevisionsAsync("project-a", proposalId, 1, 10);
        Assert.Equal(1, revisions.TotalCount);
        Assert.Equal(1, revisions.Items[0].Revision);
        var decisions = await provider.SearchAsync(new KnowledgeRecordQuery(
            "project-a", "agent-a", Kind: KnowledgeRecordKind.Decision, IncludeInactive: true));
        Assert.Empty(decisions.Items);
        Assert.Equal(0, store.CountDocuments("accepted-effect"));
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

    [Fact]
    public async Task ExpiredLeaseIsRejectedReclaimedAndFencesPreviousWorkerAfterRestart()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var (provider, store, options) = CreateProvider(clock);
        var proposalId = await CreateProposalAsync(provider, "lease-expiry");
        var receiptId = (await PromoteAsync(provider, proposalId)).OutboxEventId!.Value;
        var originalLease = await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-old", TimeSpan.FromSeconds(30));
        Assert.NotNull(originalLease);

        var restartedProvider = new CosmosMemoryProvider(store, options, clock);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await restartedProvider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, originalLease.LeaseToken));

        var reclaimedLease = await restartedProvider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-new", TimeSpan.FromSeconds(30));
        Assert.NotNull(reclaimedLease);
        Assert.NotEqual(originalLease.LeaseToken, reclaimedLease.LeaseToken);
        Assert.False(await restartedProvider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, originalLease.LeaseToken));
        Assert.False(await restartedProvider.ReleaseAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, originalLease.LeaseToken));
        Assert.True(await restartedProvider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, reclaimedLease.LeaseToken));
    }

    [Fact]
    public async Task ConcurrentDeliveryClaimsUseEtagCasToFenceTheLosingWorker()
    {
        var (provider, store, _) = CreateProvider();
        var proposalId = await CreateProposalAsync(provider, "claim-race");
        var receiptId = (await PromoteAsync(provider, proposalId)).OutboxEventId!.Value;
        store.PauseNextBatch();
        var delayedClaim = provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-delayed", TimeSpan.FromSeconds(30));
        AcceptedEffectDeliveryLease? winningLease;
        try
        {
            await store.PausedBatch.WaitAsync(TimeSpan.FromSeconds(10));
            winningLease = await provider.ClaimAcceptedEffectDeliveryAsync(
                "project-a", "run-a", receiptId, "worker-winning", TimeSpan.FromSeconds(30));
            Assert.NotNull(winningLease);
        }
        finally
        {
            store.ResumePausedBatch();
        }

        Assert.Null(await delayedClaim);
        Assert.Null(await provider.ClaimAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, "worker-third", TimeSpan.FromSeconds(30)));
        Assert.True(await provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "project-a", "run-a", receiptId, winningLease!.LeaseToken));
    }

    private static async Task<Guid> CreateProposalAsync(CosmosMemoryProvider provider, string key)
    {
        var result = await provider.CreateAsync(CreateInput("proposal", key), $"create-{key}");
        return result.Record!.RecordId;
    }

    private static async Task<KnowledgeRecord> CreateDecisionAsync(CosmosMemoryProvider provider, string key)
    {
        var proposalId = await CreateProposalAsync(provider, key);
        var result = await PromoteAsync(provider, proposalId);
        return result.Decision!;
    }

    private static KnowledgeRecordUpdate DecisionUpdate(
        KnowledgeRecord record,
        KnowledgeRecordState state,
        Guid? supersededByRecordId) =>
        new(
            "project-a",
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
            state == KnowledgeRecordState.Superseded ? "superseded" : state.ToString(),
            supersededByRecordId);

    private static KnowledgeRecordTransferEntry SupersedeTransferEntry(
        KnowledgeRecordTransferEntry entry,
        Guid replacementId)
    {
        var previous = entry.Revisions[^1];
        var revision = previous with
        {
            Revision = previous.Revision + 1,
            RevisionId = Guid.NewGuid(),
            PreviousRevisionId = previous.RevisionId,
            State = KnowledgeRecordState.Superseded,
            SupersededByRecordId = replacementId,
            ChangeKind = "decision_superseded",
            Reason = "superseded"
        };
        return entry with
        {
            Record = entry.Record with
            {
                Revision = revision.Revision,
                PreviousRevisionId = previous.RevisionId,
                RevisionId = revision.RevisionId,
                State = KnowledgeRecordState.Superseded,
                SupersededByRecordId = replacementId
            },
            Revisions = entry.Revisions.Add(revision)
        };
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
        CreateProvider(TimeProvider? timeProvider = null)
    {
        var options = Options();
        var store = new FakeCosmosMemoryStore(options);
        return (new CosmosMemoryProvider(store, options, timeProvider), store, options);
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
        private readonly Dictionary<(string ProjectId, string Id), MemoryStoredDocument> _documents = [];
        private long _etag;
        private int _pauseNextRecordRead;
        private int _pauseNextBatch;
        private TaskCompletionSource? _pausedRecordRead;
        private TaskCompletionSource? _resumeRecordRead;
        private TaskCompletionSource? _pausedBatch;
        private TaskCompletionSource? _resumeBatch;

        public CosmosMemoryContainerIdentity Identity { get; set; } = new(
            options.DatabaseId,
            options.ContainerId,
            [CosmosMemoryOptions.PartitionKeyPath],
            DefaultTimeToLiveSeconds: null,
            HasRequiredSearchCompositeIndex: true);

        public bool FailNextBatch { get; set; }
        public HttpStatusCode? FailNextBatchStatus { get; set; }
        public Task PausedRecordRead =>
            _pausedRecordRead?.Task ?? throw new InvalidOperationException("No record read is paused.");
        public Task PausedBatch =>
            _pausedBatch?.Task ?? throw new InvalidOperationException("No batch is paused.");

        public void PauseNextRecordRead()
        {
            _pausedRecordRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeRecordRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pauseNextRecordRead, 1);
        }

        public void ResumePausedRecordRead() => _resumeRecordRead?.TrySetResult();

        public void PauseNextBatch()
        {
            _pausedBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pauseNextBatch, 1);
        }

        public void ResumePausedBatch() => _resumeBatch?.TrySetResult();

        public int CountDocuments(string documentType)
        {
            lock (_gate)
                return _documents.Values.Count(item => item.Document.DocumentType == documentType);
        }

        public void ClearRevisionProvenance(string projectId, Guid recordId)
        {
            lock (_gate)
            {
                foreach (var (key, stored) in _documents.ToArray())
                {
                    if (key.ProjectId != projectId ||
                        stored.Document.RecordId != recordId ||
                        stored.Document.RecordRevision is not { } revision)
                        continue;
                    _documents[key] = stored with
                    {
                        Document = stored.Document with
                        {
                            RecordRevision = revision with { SourceRunId = null, SourceSessionId = null }
                        }
                    };
                }
            }
        }

        public void SetRecord(KnowledgeRecord record)
        {
            lock (_gate)
            {
                var key = (record.ProjectId, $"record:{record.RecordId:N}");
                var stored = _documents[key];
                _documents[key] = stored with
                {
                    Document = stored.Document with { Record = record },
                    ETag = Interlocked.Increment(ref _etag).ToString()
                };
            }
        }

        public Task<CosmosMemoryContainerIdentity> ReadContainerIdentityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Identity);
        }

        public async Task<MemoryStoredDocument?> ReadAsync(
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

        public Task<IReadOnlyList<KnowledgeMemoryDocument>> FindAcceptedEffectAsync(
            Guid receiptId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<KnowledgeMemoryDocument> matches = _documents.Values
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

        public Task<IReadOnlyList<KnowledgeRecord>> ReadTransferCandidatesAsync(
            string projectId,
            string agentId,
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
                        record.AgentId == agentId &&
                        (record.Kind == KnowledgeRecordKind.Decision ||
                         record.Kind == KnowledgeRecordKind.Memory))
                    .OrderByDescending(record => record.UpdatedAt)
                    .ThenBy(record => record.RecordId)
                    .Take(maximumRecords + 1)
                    .ToArray();
                return Task.FromResult(records);
            }
        }

        public Task<IReadOnlyCollection<Guid>> FindRevisionIdsAsync(
            string projectId,
            IReadOnlyCollection<Guid> revisionIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyCollection<Guid> found = _documents.Values
                    .Select(item => item.Document)
                    .Where(item => item.ProjectId == projectId &&
                        item.DocumentType == "revision" &&
                        item.RecordRevision is not null &&
                        revisionIds.Contains(item.RecordRevision.RevisionId))
                    .Select(item => item.RecordRevision!.RevisionId)
                    .ToHashSet();
                return Task.FromResult(found);
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

        public Task<MemoryBatchResult> ExecuteBatchAsync(
            string projectId,
            IReadOnlyList<MemoryBatchOperation> operations,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExecuteBatchCoreAsync(projectId, operations, cancellationToken);
        }

        private async Task<MemoryBatchResult> ExecuteBatchCoreAsync(
            string projectId,
            IReadOnlyList<MemoryBatchOperation> operations,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _pauseNextBatch, 0) == 1)
            {
                _pausedBatch!.TrySetResult();
                await _resumeBatch!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (FailNextBatch)
                {
                    FailNextBatch = false;
                    return new MemoryBatchResult(MemoryBatchStatus.Failed);
                }
                if (FailNextBatchStatus is { } failureStatus)
                {
                    FailNextBatchStatus = null;
                    return new MemoryBatchResult(ToBatchStatus(failureStatus));
                }
                foreach (var operation in operations)
                {
                    var key = (projectId, operation.Document.Id);
                    var existing = _documents.GetValueOrDefault(key);
                    if (operation.Kind == MemoryBatchOperationKind.Create && existing is not null)
                        return new MemoryBatchResult(MemoryBatchStatus.Conflict);
                    if (operation.Kind == MemoryBatchOperationKind.Replace &&
                        (existing is null || !string.Equals(existing.ETag, operation.ETag, StringComparison.Ordinal)))
                        return new MemoryBatchResult(MemoryBatchStatus.Conflict);
                }
                foreach (var operation in operations)
                    _documents[(projectId, operation.Document.Id)] = new MemoryStoredDocument(
                        operation.Document, Interlocked.Increment(ref _etag).ToString());
                return new MemoryBatchResult(MemoryBatchStatus.Succeeded);
            }
        }

        private static MemoryBatchStatus ToBatchStatus(HttpStatusCode statusCode) =>
            statusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound
                ? MemoryBatchStatus.Conflict
                : (int)statusCode is >= 200 and < 300
                    ? MemoryBatchStatus.Succeeded
                    : MemoryBatchStatus.Failed;

        private static string SearchableText(KnowledgeRecord record) =>
            string.Join(' ', record.Type, record.Title, record.Content, string.Join(' ', record.Tags));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
