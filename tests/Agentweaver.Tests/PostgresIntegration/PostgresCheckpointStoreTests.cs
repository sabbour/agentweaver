using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// Integration tests for <see cref="PostgresJsonCheckpointStore"/> — the shared, concurrency-safe MAF
/// checkpoint store that replaces the per-pod file store on Postgres. Requires a running postgres:16
/// container via Testcontainers (same fixture as the other PostgresIntegration tests).
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresCheckpointStoreTests(PostgresFixture pg)
{
    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task CreateThenRetrieve_RoundTrips_Payload()
    {
        var store = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var session = $"run-{Guid.NewGuid():n}";

        var info = await store.CreateCheckpointAsync(session, Json("""{"step":1,"name":"alpha"}"""));
        var roundTrip = await store.RetrieveCheckpointAsync(session, info);

        roundTrip.GetProperty("step").GetInt32().Should().Be(1);
        roundTrip.GetProperty("name").GetString().Should().Be("alpha");
    }

    [Fact]
    public async Task TwoWriters_ShareTheSameStore_CrossPodResume()
    {
        // Two independent store instances over the same factory simulate two API replicas.
        var replicaA = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var replicaB = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var session = $"run-{Guid.NewGuid():n}";

        // Replica A writes a checkpoint...
        var infoA = await replicaA.CreateCheckpointAsync(session, Json("""{"by":"A"}"""));

        // ...and replica B must immediately SEE and READ it (genuine cross-pod resume).
        var indexFromB = (await replicaB.RetrieveIndexAsync(session)).ToList();
        indexFromB.Should().ContainSingle(c => c.CheckpointId == infoA.CheckpointId);

        var payloadFromB = await replicaB.RetrieveCheckpointAsync(session, infoA);
        payloadFromB.GetProperty("by").GetString().Should().Be("A");
    }

    [Fact]
    public async Task ConcurrentWrites_FromTwoWriters_AreAllVisible_NoContention()
    {
        var replicaA = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var replicaB = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var session = $"run-{Guid.NewGuid():n}";

        // 10 concurrent writes from each replica — no exclusive lock, so none should fail.
        var writes = new List<Task<CheckpointInfo>>();
        for (var i = 0; i < 10; i++)
        {
            var n = i;
            writes.Add(replicaA.CreateCheckpointAsync(session, Json($$"""{"w":"A","i":{{n}}}""")).AsTask());
            writes.Add(replicaB.CreateCheckpointAsync(session, Json($$"""{"w":"B","i":{{n}}}""")).AsTask());
        }
        await Task.WhenAll(writes);

        var index = (await replicaA.RetrieveIndexAsync(session)).ToList();
        index.Should().HaveCount(20);
        index.Select(c => c.CheckpointId).Distinct().Should().HaveCount(20);
    }

    [Fact]
    public async Task RetrieveIndex_WithParent_FiltersLikeFileStore()
    {
        var store = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var session = $"run-{Guid.NewGuid():n}";

        var root = await store.CreateCheckpointAsync(session, Json("""{"n":"root"}"""));
        var child = await store.CreateCheckpointAsync(session, Json("""{"n":"child"}"""), parent: root);

        // Unfiltered index returns everything.
        var all = (await store.RetrieveIndexAsync(session)).Select(c => c.CheckpointId).ToList();
        all.Should().Contain(new[] { root.CheckpointId, child.CheckpointId });

        // Parent-scoped index returns the child (parent matches) but not the root (parent is null).
        var scoped = (await store.RetrieveIndexAsync(session, withParent: root)).Select(c => c.CheckpointId).ToList();
        scoped.Should().Contain(child.CheckpointId);
        scoped.Should().NotContain(root.CheckpointId);
    }

    [Fact]
    public async Task DifferentStoreNames_AreIsolated()
    {
        var runs = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var coordinator = new PostgresJsonCheckpointStore(pg.Factory, "coordinator");
        var session = $"shared-{Guid.NewGuid():n}";

        await runs.CreateCheckpointAsync(session, Json("""{"store":"runs"}"""));

        // The coordinator store must not see the runs store's checkpoints even for the same session id.
        (await coordinator.RetrieveIndexAsync(session)).Should().BeEmpty();
        (await runs.RetrieveIndexAsync(session)).Should().ContainSingle();
    }

    [Fact]
    public async Task RetrieveCheckpoint_Missing_ThrowsKeyNotFound()
    {
        var store = new PostgresJsonCheckpointStore(pg.Factory, "runs");
        var session = $"run-{Guid.NewGuid():n}";
        var missing = new CheckpointInfo(session, "does-not-exist");

        var act = async () => await store.RetrieveCheckpointAsync(session, missing);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PurgeTerminalAsync_DeletesOnlyTerminalSessions()
    {
        var factory = new PostgresCheckpointStoreFactory(pg.Factory);
        var store = (PostgresJsonCheckpointStore)factory.Create("runs", fallbackFileDir: "", logger: null!);

        var terminal = $"run-term-{Guid.NewGuid():n}";
        var live = $"run-live-{Guid.NewGuid():n}";
        await store.CreateCheckpointAsync(terminal, Json("""{"s":"done"}"""));
        await store.CreateCheckpointAsync(live, Json("""{"s":"running"}"""));

        var deleted = await factory.PurgeTerminalAsync(
            "runs",
            (session, _) => new ValueTask<bool>(session == terminal),
            CancellationToken.None);

        deleted.Should().Be(1);
        (await store.RetrieveIndexAsync(terminal)).Should().BeEmpty();
        (await store.RetrieveIndexAsync(live)).Should().ContainSingle();
    }

    [Fact]
    public async Task Migration_Created_WorkflowCheckpointsTable()
    {
        await using var db = await pg.CreateDbContextAsync();
        var exists = await db.WorkflowCheckpoints.AsNoTracking().AnyAsync(c => c.StoreName == "__never__");
        exists.Should().BeFalse(); // query succeeds => table exists and is mapped
    }
}
