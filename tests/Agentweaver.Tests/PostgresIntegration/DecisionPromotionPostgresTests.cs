using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class DecisionPromotionPostgresTests(PostgresFixture pg)
{
    [PostgresRequiredFact]
    public async Task PromoteEntryAsync_OneHundredConcurrentReplicas_ReturnSameDecision()
    {
        var projectId = "promotion-" + Guid.NewGuid().ToString("N");
        int entryId;
        await using (var setup = await pg.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            var seededEntry = new DecisionInboxEntry
            {
                ProjectId = projectId,
                AgentName = "scribe",
                Slug = "concurrent-promotion",
                Type = "learning",
                Title = "Concurrent promotion",
                Content = "Promote this inbox entry once.",
                Status = "pending",
                SourceKind = MemorySourceKinds.Run,
                SourceIdentity = "run:promotion",
                SourceRunId = "promotion",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.DecisionInbox.Add(seededEntry);
            await setup.SaveChangesAsync();
            entryId = seededEntry.Id;
        }

        const int replicaCount = 100;
        using var connectionReadinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connectedCount = 0;
        var connectionsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<DecisionPromotionResult?> PromoteAsync()
        {
            await using var db = await pg.CreateDbContextAsync();
            await db.Database.OpenConnectionAsync(connectionReadinessTimeout.Token);
            if (Interlocked.Increment(ref connectedCount) == replicaCount)
                connectionsReady.TrySetResult();
            await start.Task.WaitAsync(connectionReadinessTimeout.Token);
            return await DecisionPromotion.PromoteEntryAsync(
                db,
                projectId,
                entryId,
                DateTimeOffset.UtcNow,
                "scribe:promotion",
                CancellationToken.None);
        }

        var promotions = Enumerable.Range(0, replicaCount).Select(_ => PromoteAsync()).ToArray();

        try
        {
            await connectionsReady.Task.WaitAsync(connectionReadinessTimeout.Token);
            connectionReadinessTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch
        {
            connectionReadinessTimeout.Cancel();
            throw;
        }

        await using var lockHolder = await pg.CreateDbContextAsync();
        await using var lockTransaction = await lockHolder.Database.BeginTransactionAsync();
        await lockHolder.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            [$"decision-inbox:{projectId}:{entryId}"]);

        start.TrySetResult();
        await WaitForAdvisoryLockWaitersAsync(lockHolder, replicaCount);
        promotions.Should().OnlyContain(promotion => !promotion.IsCompleted,
            "all 100 physical replicas must be waiting on the held promotion lock before it is released");
        await lockTransaction.CommitAsync();

        var results = await Task.WhenAll(promotions);
        results.All(result => result is not null).Should().BeTrue();
        results.Select(result => result!.Decision.Id).Distinct().Should().ContainSingle();
        results.Count(result => result!.Promoted).Should().Be(1);

        await using var verify = await pg.CreateDbContextAsync();
        var promotedEntry = await verify.DecisionInbox.AsNoTracking().SingleAsync(candidate => candidate.Id == entryId);
        promotedEntry.Status.Should().Be("merged");
        promotedEntry.DecisionId.Should().Be(results[0]!.Decision.Id);
        (await verify.Decisions.CountAsync(decision => decision.ProjectId == projectId)).Should().Be(1);
    }

    [PostgresRequiredFact]
    public async Task RejectEntryAsync_WaitsForPromotionLock_AndCannotOverwriteMergedEntry()
    {
        var projectId = "promotion-rejection-" + Guid.NewGuid().ToString("N");
        var entryId = await SeedPendingEntryAsync(projectId);

        await using var gate = await pg.CreateDbContextAsync();
        await using var gateTransaction = await gate.Database.BeginTransactionAsync();
        await gate.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            [$"decision-inbox:{projectId}:{entryId}"]);

        async Task<DecisionPromotionResult?> PromoteAsync()
        {
            await using var db = await pg.CreateDbContextAsync();
            return await DecisionPromotion.PromoteEntryAsync(
                db, projectId, entryId, DateTimeOffset.UtcNow, "scribe:promotion");
        }

        async Task<DecisionInboxEntry?> RejectAsync()
        {
            await using var db = await pg.CreateDbContextAsync();
            return await DecisionPromotion.RejectEntryAsync(
                db, projectId, entryId, DateTimeOffset.UtcNow);
        }

        var promotion = PromoteAsync();
        var rejection = RejectAsync();
        await WaitForAdvisoryLockWaitersAsync(gate, 2);
        rejection.IsCompleted.Should().BeFalse();

        await gateTransaction.CommitAsync();
        var promotionResult = await promotion;
        var rejectionResult = await rejection;

        await using var verify = await pg.CreateDbContextAsync();
        var entry = await verify.DecisionInbox.AsNoTracking().SingleAsync(candidate => candidate.Id == entryId);
        if (entry.Status == "merged")
        {
            promotionResult.Should().NotBeNull();
            promotionResult!.Promoted.Should().BeTrue();
            rejectionResult.Should().BeNull();
            (await verify.Decisions.CountAsync(decision => decision.ProjectId == projectId)).Should().Be(1);
        }
        else
        {
            entry.Status.Should().Be("rejected");
            promotionResult.Should().BeNull();
            rejectionResult.Should().NotBeNull();
            (await verify.Decisions.CountAsync(decision => decision.ProjectId == projectId)).Should().Be(0);
        }
    }

    private async Task<int> SeedPendingEntryAsync(string projectId)
    {
        await using var setup = await pg.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var entry = new DecisionInboxEntry
        {
            ProjectId = projectId,
            AgentName = "scribe",
            Slug = "concurrent-promotion",
            Type = "learning",
            Title = "Concurrent promotion",
            Content = "Promote this inbox entry once.",
            Status = "pending",
            SourceKind = MemorySourceKinds.Run,
            SourceIdentity = "run:promotion",
            SourceRunId = "promotion",
            CreatedAt = now,
            UpdatedAt = now,
        };
        setup.DecisionInbox.Add(entry);
        await setup.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task WaitForAdvisoryLockWaitersAsync(MemoryDbContext db, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var waiters = await db.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS \"Value\" FROM pg_stat_activity WHERE "
                    + "datname = current_database() AND wait_event = 'advisory'")
                .SingleAsync(timeout.Token);
            if (waiters >= expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
