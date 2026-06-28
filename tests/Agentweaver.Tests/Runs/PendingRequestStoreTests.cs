using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;

namespace Agentweaver.Tests.Runs;

/// <summary>
/// Replica-safety tests for the EF-backed <see cref="PendingRequestStore"/>.
///
/// The HITL review/confirm gate is armed by the background watch loop on one pod and consumed by a
/// later HTTP request that may land on a DIFFERENT pod. These tests use a shared in-memory SQLite
/// database and give EACH store instance its OWN root service provider (and therefore its OWN
/// <see cref="MemoryDbContext"/> connections via its scope factory) to simulate distinct API
/// replicas:
///   • a gate armed on one "replica" is visible and consumable on a DIFFERENT "replica";
///   • consume is atomic and single-use — a second consume of the same run returns null;
///   • re-arm (upsert) overwrites the previous gate for the same run id.
/// </summary>
public sealed class PendingRequestStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly List<ServiceProvider> _providers = [];

    public PendingRequestStoreTests()
    {
        _connectionString = $"DataSource=file:pending-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        // Materialize the schema once on the shared database.
        var sp = NewReplicaServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    // Each service provider simulates a distinct replica: its own scope factory over the shared DB.
    private ServiceProvider NewReplicaServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connectionString));
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        return sp;
    }

    private PendingRequestStore NewStoreOnSeparateReplica() =>
        new(NewReplicaServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static ExternalRequest NewRequest(string requestId)
    {
        var portInfo = new RequestPortInfo(
            new TypeId("Test.Asm", "Test.RequestType"),
            new TypeId("Test.Asm", "Test.ResponseType"),
            "review-port");
        return new ExternalRequest(portInfo, requestId, new PortableValue(requestId));
    }

    [Fact]
    public async Task GateArmedOnOneReplica_IsConsumableOnAnother_ThenSingleUse()
    {
        const string runId = "run-cross-replica";

        // Replica A arms the gate (persisted to the shared DB).
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-A"), "octocat");

        // Replica B (a SEPARATE store/scope factory) reads it without consuming.
        var peek = await NewStoreOnSeparateReplica().GetAsync(runId);
        peek.Should().NotBeNull("the gate must be visible from any replica");
        peek!.OwnerUser.Should().Be("octocat");
        peek.Request.RequestId.Should().Be("req-A");
        peek.Request.PortInfo.PortId.Should().Be("review-port");

        // Replica B consumes it atomically.
        var consumed = await NewStoreOnSeparateReplica().TryRemoveAsync(runId);
        consumed.Should().NotBeNull("the armed gate must be consumable cross-replica");
        consumed!.Request.RequestId.Should().Be("req-A");

        // Replica C tries to consume the same run again → already consumed (at-most-once).
        var second = await NewStoreOnSeparateReplica().TryRemoveAsync(runId);
        second.Should().BeNull("a gate can be consumed at most once across all replicas");

        // And a plain read now sees nothing.
        (await NewStoreOnSeparateReplica().GetAsync(runId)).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentConsume_AcrossReplicas_ExactlyOneSucceeds()
    {
        const string runId = "run-concurrent";
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-X"), "octocat");

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => NewStoreOnSeparateReplica().TryRemoveAsync(runId)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r is not null).Should().Be(1,
            "the pending gate is single-consume even when many replicas race");
    }

    [Fact]
    public async Task SetAsync_ReArmsSameRun_OverwritesPreviousGate()
    {
        const string runId = "run-rearm";
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-old"), "octocat");
        await NewStoreOnSeparateReplica().SetAsync(runId, NewRequest("req-new"), "hubot");

        var peek = await NewStoreOnSeparateReplica().GetAsync(runId);
        peek.Should().NotBeNull();
        peek!.Request.RequestId.Should().Be("req-new", "re-arming must upsert in place by run id");
        peek.OwnerUser.Should().Be("hubot");

        // Still exactly one row (no duplicate gate for the run).
        using var scope = NewReplicaServiceProvider().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.PendingRequests.CountAsync(p => p.RunId == runId)).Should().Be(1);
    }

    public void Dispose()
    {
        foreach (var sp in _providers)
            sp.Dispose();
        _keepAlive.Dispose();
    }
}
