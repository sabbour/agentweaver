using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisMemoryNativeIntegrationCollection
{
    public const string Name = "Redis Memory native integration";
}

[Collection(RedisMemoryNativeIntegrationCollection.Name)]
public sealed class RedisMemoryNativeIntegrationTests
{
    private const string ActorFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [RedisMemoryNativeFact]
    public async Task LuaBatchValidationDoesNotPartiallyWrite()
    {
        var settings = RedisMemoryNativeSettings.Read();
        using var context = await settings.OpenContextAsync(NewKeyPrefix());
        await context.Provider.NegotiateAsync(context.Candidate);

        var existing = new KnowledgeMemoryDocument(
            $"native-{Guid.NewGuid():N}", "native-project", "record");
        var uncommitted = new KnowledgeMemoryDocument(
            $"native-{Guid.NewGuid():N}", "native-project", "record");
        var first = await context.Store.ExecuteBatchAsync(
            "native-project",
            [new MemoryBatchOperation(MemoryBatchOperationKind.Create, existing)],
            CancellationToken.None);
        Assert.Equal(MemoryBatchStatus.Succeeded, first.Status);

        var failed = await context.Store.ExecuteBatchAsync(
            "native-project",
            [
                new MemoryBatchOperation(MemoryBatchOperationKind.Create, uncommitted),
                new MemoryBatchOperation(MemoryBatchOperationKind.Create, existing)
            ],
            CancellationToken.None);

        Assert.Equal(MemoryBatchStatus.Conflict, failed.Status);
        var key = Assert.Single(await context.Client.ScanKeysAsync(
            $"{context.Options.KeyPrefix}:project:*", CancellationToken.None));
        var fields = await context.Client.ScanHashAsync(key, CancellationToken.None);
        Assert.Contains(fields, field => field.Name == $"doc:{existing.Id}");
        Assert.DoesNotContain(fields, field => field.Name == $"doc:{uncommitted.Id}");
    }

    [RedisMemoryNativeFact]
    public async Task ConcurrentExpectedRevisionWritesUseRedisCompareAndSwap()
    {
        var settings = RedisMemoryNativeSettings.Read();
        using var context = await settings.OpenContextAsync(NewKeyPrefix());
        await context.Provider.NegotiateAsync(context.Candidate);
        var created = await context.Provider.CreateAsync(
            CreateInput("native-project", KnowledgeRecordKind.Memory, "original"),
            $"native-create-{Guid.NewGuid():N}");
        var record = Assert.IsType<KnowledgeRecord>(created.Record);

        var first = context.Provider.UpdateAsync(
            UpdateInput(record.RecordId, "first"), $"native-update-{Guid.NewGuid():N}");
        var second = context.Provider.UpdateAsync(
            UpdateInput(record.RecordId, "second"), $"native-update-{Guid.NewGuid():N}");
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Updated);
        Assert.Single(results, result => result.Status == KnowledgeWriteStatus.Stale);
        var current = await context.Provider.ReadAsync("native-project", record.RecordId);
        Assert.True(current!.Content is "first" or "second");
        var revisions = await context.Provider.ReadRevisionsAsync(
            "native-project", record.RecordId, 1, 10);
        Assert.Equal(2, revisions.TotalCount);
    }

    [RedisMemoryNativeFact]
    public async Task PromotionReceiptAndLeaseFenceSurviveOwnedAofRestart()
    {
        var settings = RedisMemoryNativeSettings.Read();
        var keyPrefix = NewKeyPrefix();
        var fixedClock = new FixedTimeProvider(
            new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Guid receiptId;
        Guid firstLeaseToken;
        var proposalId = Guid.NewGuid();
        const string promotionKey = "native-promotion-replay";

        using (var beforeRestart = await settings.OpenContextAsync(keyPrefix, fixedClock))
        {
            await beforeRestart.Provider.NegotiateAsync(beforeRestart.Candidate);
            var proposal = await beforeRestart.Provider.CreateAsync(
                CreateInput("native-project", KnowledgeRecordKind.Proposal, "persist this proposal"),
                $"native-proposal-{Guid.NewGuid():N}");
            proposalId = Assert.IsType<KnowledgeRecord>(proposal.Record).RecordId;

            var promoted = await PromoteAsync(beforeRestart.Provider, proposalId, promotionKey);
            Assert.Equal(KnowledgeWriteStatus.Updated, promoted.Status);
            receiptId = Assert.IsType<Guid>(promoted.OutboxEventId);
            var lease = Assert.IsType<AcceptedEffectDeliveryLease>(
                await beforeRestart.Provider.ClaimAcceptedEffectDeliveryAsync(
                    "native-project", "native-run", receiptId, "native-worker-a",
                    TimeSpan.FromSeconds(2)));
            firstLeaseToken = lease.LeaseToken;
        }

        await settings.RestartOwnedInstanceAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(2_200));

        using var afterRestart = await settings.OpenContextAsync(keyPrefix, fixedClock);
        await afterRestart.Provider.NegotiateAsync(afterRestart.Candidate);
        var persisted = await afterRestart.Provider.ReadAcceptedEffectDeliveryAsync(
            "native-project", "native-run", receiptId);
        Assert.NotNull(persisted);
        Assert.Equal(receiptId, persisted.Receipt.ReceiptId);
        Assert.False(persisted.IsDelivered);

        var replay = await PromoteAsync(afterRestart.Provider, proposalId, promotionKey);
        Assert.Equal(KnowledgeWriteStatus.Updated, replay.Status);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(receiptId, replay.OutboxEventId);

        Assert.False(await afterRestart.Provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "native-project", "native-run", receiptId, firstLeaseToken));
        var reclaimed = Assert.IsType<AcceptedEffectDeliveryLease>(
            await afterRestart.Provider.ClaimAcceptedEffectDeliveryAsync(
                "native-project", "native-run", receiptId, "native-worker-b",
                TimeSpan.FromSeconds(2)));
        Assert.NotEqual(firstLeaseToken, reclaimed.LeaseToken);
        Assert.False(await afterRestart.Provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "native-project", "native-run", receiptId, firstLeaseToken));
        Assert.True(await afterRestart.Provider.AcknowledgeAcceptedEffectDeliveryAsync(
            "native-project", "native-run", receiptId, reclaimed.LeaseToken));
    }

    private static string NewKeyPrefix() => $"agentweaver:redis-native:{Guid.NewGuid():N}";

    private static KnowledgeRecordCreate CreateInput(
        string projectId,
        KnowledgeRecordKind kind,
        string content) =>
        new(
            projectId,
            "native-agent",
            kind,
            "notes",
            "native integration",
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            kind == KnowledgeRecordKind.Proposal ? "native-run" : null,
            null,
            ActorFingerprint,
            kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created");

    private static KnowledgeRecordUpdate UpdateInput(Guid recordId, string content) =>
        new(
            "native-project",
            recordId,
            1,
            "notes",
            "native integration",
            content,
            null,
            "medium",
            ImmutableArray<string>.Empty,
            KnowledgeRecordState.Active,
            ActorFingerprint,
            "native update");

    private static Task<KnowledgeProposalPromotionResult> PromoteAsync(
        RedisMemoryProvider provider,
        Guid proposalId,
        string idempotencyKey) =>
        provider.PromoteProposalAsync(
            "native-project",
            "native-run",
            proposalId,
            1,
            ActorFingerprint,
            new AcceptedEffectAuthorizationBounds(
                "https://identity.native-test/",
                "native-user",
                "native-tenant",
                "native-project",
                "native-run",
                ProjectAuthorityResourceType.Project,
                "native-project",
                1,
                1,
                1,
                1,
                "native-context"),
            idempotencyKey);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

public sealed class RedisMemoryNativeFactAttribute : FactAttribute
{
    public RedisMemoryNativeFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RedisMemoryNativeSettings.OptInVariable),
                "1",
                StringComparison.Ordinal))
            Skip = $"Set {RedisMemoryNativeSettings.OptInVariable}=1 to run dedicated Redis native tests.";
    }
}
