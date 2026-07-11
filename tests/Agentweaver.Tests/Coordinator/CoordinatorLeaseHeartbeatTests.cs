using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the issue #218 fix: the coordinator lease heartbeat (item 1), ownership-based fencing
/// (item 2, R1/R2), and the per-project integration-build lock (item 3). All exercised against REAL
/// components (EF <see cref="MemoryDbContext"/> on in-memory SQLite, real <see cref="SqliteRunStore"/>
/// / <see cref="RunStreamStore"/>; Constitution VII, no mocks).
/// </summary>
public sealed class CoordinatorLeaseHeartbeatTests : IAsyncDisposable
{
    private const string OwnerPod = "pod-owner";
    private const string PeerPod = "pod-peer";

    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();

    public CoordinatorLeaseHeartbeatTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    // -----------------------------------------------------------------------
    // (a) Heartbeat renews UpdatedAt while a loop is active, so the reconciler does NOT re-claim a
    //     fresh-but-busy Dispatching plan.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HeartbeatTick_RenewsUpdatedAt_OnBusyDispatchingPlan_OwnedByThisPod()
    {
        const string coord = "coord-hb-renew";
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-100);
        var planId = await SeedDispatchingPlanAsync(coord, OwnerPod, staleUpdatedAt);

        var sut = BuildDispatch(OwnerPod);
        var tick = await sut.HeartbeatTickAsync(planId, perRunCts: null, ct: default);

        tick.Should().Be(CoordinatorDispatchService.LeaseHeartbeatTick.Renewed);
        var (podId, status, updatedAt) = await GetPlanLeaseAsync(planId);
        podId.Should().Be(OwnerPod, "the renew must not change ownership");
        status.Should().Be(WorkPlanStatus.Dispatching);
        updatedAt.Should().BeAfter(staleUpdatedAt, "the heartbeat bumped UpdatedAt to keep the lease fresh");
        updatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PeerReconciler_DoesNotReclaim_FreshlyRenewedDispatchingPlan_ButReclaimsWhenStale()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        // Plan is owned by OwnerPod but its lease has gone stale (a long child turn with no heartbeat).
        var planId = await SeedDispatchingPlanAsync(coord, OwnerPod, DateTimeOffset.UtcNow.AddMinutes(-5));

        var owner = BuildDispatch(OwnerPod);
        var peerReconciler = BuildReconciler(PeerPod);

        // The owner's heartbeat renews the lease -> a peer sweep must NOT reclaim the busy plan.
        (await owner.HeartbeatTickAsync(planId, perRunCts: null, ct: default))
            .Should().Be(CoordinatorDispatchService.LeaseHeartbeatTick.Renewed);
        (await peerReconciler.SweepAsync(default)).Should().Be(0,
            "a freshly-renewed Dispatching lease owned by a live peer must not be stolen");
        (await GetPlanLeaseAsync(planId)).PodId.Should().Be(OwnerPod, "ownership stayed with the live owner");

        // Simulate the owner going away (no more heartbeats): the lease goes stale and the peer reclaims.
        await ForceLeaseStaleAsync(planId, DateTimeOffset.UtcNow.AddMinutes(-5));
        (await peerReconciler.SweepAsync(default)).Should().Be(1,
            "once the lease is stale the peer reconciler reclaims and re-arms the orphan");
        (await GetPlanLeaseAsync(planId)).PodId.Should().Be(PeerPod, "the peer claimed the stale lease");
        _assembly.Started.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // (b) Fencing: a lost lease (peer now owns it) cancels the per-run CTS; a benign 0-row (status
    //     advanced, still mine) does NOT cancel.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HeartbeatTick_Fences_WhenPeerTookOverOwnership()
    {
        const string coord = "coord-hb-fence";
        // The peer already stole the lease: the row is Dispatching but owned by PeerPod.
        var planId = await SeedDispatchingPlanAsync(coord, PeerPod, DateTimeOffset.UtcNow);

        var sut = BuildDispatch(OwnerPod);
        using var perRunCts = new CancellationTokenSource();

        var tick = await sut.HeartbeatTickAsync(planId, perRunCts, ct: default);

        tick.Should().Be(CoordinatorDispatchService.LeaseHeartbeatTick.Fenced);
        perRunCts.IsCancellationRequested.Should().BeTrue(
            "losing the lease to a peer fences (cancels) this pod's dispatch loop");
    }

    [Fact]
    public async Task HeartbeatTick_BenignRelease_WhenStillMineButStatusAdvanced_DoesNotFence()
    {
        const string coord = "coord-hb-handoff";
        // The loop itself advanced the plan past dispatching (normal Dispatching -> AwaitingAssembly
        // hand-off); ownership is still this pod. The renew affects 0 rows but this must NOT fence.
        var planId = await SeedDispatchingPlanAsync(coord, OwnerPod, DateTimeOffset.UtcNow);
        await ForceStatusAsync(planId, WorkPlanStatus.AwaitingAssembly);

        var sut = BuildDispatch(OwnerPod);
        using var perRunCts = new CancellationTokenSource();

        var tick = await sut.HeartbeatTickAsync(planId, perRunCts, ct: default);

        tick.Should().Be(CoordinatorDispatchService.LeaseHeartbeatTick.Released);
        perRunCts.IsCancellationRequested.Should().BeFalse(
            "a benign hand-off (still owned by this pod, status advanced) must never self-fence the loop");
    }

    // -----------------------------------------------------------------------
    // (c) The per-project integration-build lock serializes concurrent builds for the same projectId.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IntegrationBuildLock_SerializesSameProject_AllowsDifferentProject_AndReclaimsStale()
    {
        var lockSvc = BuildIntegrationBuildLock(OwnerPod);
        var shortTimeout = TimeSpan.FromMilliseconds(300);
        var longTimeout = TimeSpan.FromSeconds(2);

        // First acquire for a project succeeds.
        var held = await lockSvc.TryAcquireAsync("proj-x", longTimeout, default);
        held.Should().NotBeNull();

        // A second acquire for the SAME project is blocked (fresh holder) and times out.
        var blocked = await lockSvc.TryAcquireAsync("proj-x", shortTimeout, default);
        blocked.Should().BeNull("a fresh lock on the same project serializes a concurrent builder");

        // A DIFFERENT project is not blocked.
        var otherProject = await lockSvc.TryAcquireAsync("proj-y", longTimeout, default);
        otherProject.Should().NotBeNull("the lock is per-project; a different repo never blocks");
        await otherProject!.DisposeAsync();

        // Releasing the holder frees the same-project lock for the next builder.
        await held!.DisposeAsync();
        var reacquired = await lockSvc.TryAcquireAsync("proj-x", longTimeout, default);
        reacquired.Should().NotBeNull("the lock is free once the holder releases it");

        // A crashed holder never deadlocks the project: an entry older than the stale TTL is reclaimable.
        await ForceLockStaleAsync("proj-x", DateTimeOffset.UtcNow.AddSeconds(-60));
        var reclaimed = await lockSvc.TryAcquireAsync("proj-x", shortTimeout, default);
        reclaimed.Should().NotBeNull("a lock older than the stale TTL is reclaimed rather than deadlocking");

        // The stale original holder cannot release the reclaimed lock (token-fenced): it is still held.
        await reacquired!.DisposeAsync();
        var stillHeld = await lockSvc.TryAcquireAsync("proj-x", shortTimeout, default);
        stillHeld.Should().BeNull("release is fenced by the per-acquisition token; the reclaimer keeps the lock");

        await reclaimed!.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private CoordinatorDispatchService BuildDispatch(string podId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PodId"] = podId,
            })
            .Build();

        var orchestrator = new RunOrchestrator(
            _runStore, _streamStore,
            worktreeManager: null!, workflowFactory: null!, registry: null!, watchLoop: null!,
            _scopeFactory, configuration: null!, NullLogger<RunOrchestrator>.Instance);

        return new CoordinatorDispatchService(
            _runStore, _streamStore, orchestrator, null!, new CoordinatorSteeringQueue(_scopeFactory), _assembly,
            _scopeFactory, new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance,
            runOptions: null, autopilot: null, configuration: config);
    }

    private CoordinatorReconciler BuildReconciler(string podId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PodId"] = podId })
            .Build();
        return new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: config, assembly: _assembly);
    }

    private IntegrationBuildLock BuildIntegrationBuildLock(string podId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PodId"] = podId,
                ["Coordinator:IntegrationBuildLockStaleTtlSeconds"] = "30",
            })
            .Build();
        return new IntegrationBuildLock(_scopeFactory, NullLogger<IntegrationBuildLock>.Instance, config);
    }

    private async Task<int> SeedDispatchingPlanAsync(string coordinatorRunId, string podId, DateTimeOffset updatedAt)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Status = WorkPlanStatus.Dispatching,
            CoordinatorPodId = podId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task SeedCoordinatorRunAsync(string coordinatorRunId)
    {
        var run = new Run
        {
            Id = RunId.Parse(coordinatorRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "coordinate the work",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
        };
        await _runStore.InsertAsync(run);
    }

    private async Task<(string? PodId, string Status, DateTimeOffset UpdatedAt)> GetPlanLeaseAsync(int planId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == planId);
        return (row.CoordinatorPodId, row.Status, row.UpdatedAt);
    }

    private async Task ForceLeaseStaleAsync(int planId, DateTimeOffset updatedAt)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.WorkPlans.FirstAsync(w => w.Id == planId);
        row.UpdatedAt = updatedAt;
        await db.SaveChangesAsync();
    }

    private async Task ForceStatusAsync(int planId, string status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.WorkPlans.FirstAsync(w => w.Id == planId);
        row.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task ForceLockStaleAsync(string projectId, DateTimeOffset acquiredAt)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.IntegrationBuildLocks.FirstAsync(l => l.ProjectId == projectId);
        row.AcquiredAt = acquiredAt;
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }

    private sealed class RecordingAssembly : ICoordinatorAssembly
    {
        public List<CoordinatorDispatchContext> Started { get; } = [];

        public void MarkActive(string coordinatorRunId) { }
        public void StartAssembly(CoordinatorDispatchContext context) => Started.Add(context);
        public void EnsureFinalScribe(Run coordinatorRun) { }
        public bool IsAssemblyActive(string coordinatorRunId) => false;
        public void AbandonStaleReview(CoordinatorDispatchContext context) { }
        public void FailAssembly(CoordinatorDispatchContext context, string reason) { }
    }

    private sealed class RecordingDispatch : ICoordinatorDispatch
    {
        public void StartDispatch(CoordinatorDispatchContext context) { }
        public bool IsDispatchActive(string coordinatorRunId) => false;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
