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
    // (d) A transient per-tick error does not stop the heartbeat: a later tick renews the lease.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LeaseHeartbeat_TransientTickError_DoesNotStopHeartbeat_RenewsOnNextTick()
    {
        const string coord = "coord-hb-flaky";
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var planId = await SeedDispatchingPlanAsync(coord, OwnerPod, staleUpdatedAt);

        // The first heartbeat tick fails (a transient scope/DB blip); every later tick succeeds.
        var flaky = new FlakyScopeFactory(_scopeFactory, failFirstN: 1);
        var sut = BuildDispatch(OwnerPod, flaky, heartbeatSeconds: 1);

        using var stop = new CancellationTokenSource();
        var task = sut.RunLeaseHeartbeatAsync(planId, coord, perRunCts: null, stop.Token);

        // Wait until the lease is renewed. This can only happen if the loop survived the failing first tick.
        var renewed = await WaitUntilAsync(
            async () => (await GetPlanLeaseAsync(planId)).UpdatedAt > staleUpdatedAt,
            timeout: TimeSpan.FromSeconds(8));

        stop.Cancel();
        await task;

        renewed.Should().BeTrue("a transient failing tick must not stop the heartbeat; a later tick renews the lease");
        flaky.Calls.Should().BeGreaterThan(1, "the first tick failed and at least one subsequent tick ran");
        (await GetPlanLeaseAsync(planId)).PodId.Should().Be(OwnerPod, "ownership is unchanged by the transient blip");
    }

    // -----------------------------------------------------------------------
    // (e) #239 root-cause: a DEDICATED per-run assembly lease heartbeat renews UpdatedAt during the
    //     long Assembling/AssemblySteering phases (which the Dispatching-only heartbeat never covers),
    //     so a healthy owner's lease can never go stale and get reclaimed by a peer mid-assembly.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AssemblyHeartbeatTick_RenewsUpdatedAt_OnBusyAssemblingPlan_OwnedByThisPod()
    {
        const string coord = "coord-asm-renew";
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-100);
        var planId = await SeedPlanAsync(coord, OwnerPod, staleUpdatedAt, WorkPlanStatus.Assembling);

        var sut = BuildAssembly(OwnerPod);
        var tick = await sut.AssemblyHeartbeatTickAsync(planId, ct: default);

        tick.Should().Be(CoordinatorAssemblyService.AssemblyLeaseTick.Renewed);
        var (podId, status, updatedAt) = await GetPlanLeaseAsync(planId);
        podId.Should().Be(OwnerPod, "the renew must not change ownership");
        status.Should().Be(WorkPlanStatus.Assembling);
        updatedAt.Should().BeAfter(staleUpdatedAt, "the heartbeat bumped UpdatedAt to keep the assembly lease fresh");
        updatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AssemblyHeartbeatTick_Renews_OnAssemblySteeringPlan()
    {
        const string coord = "coord-asm-steer-renew";
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-100);
        var planId = await SeedPlanAsync(coord, OwnerPod, staleUpdatedAt, WorkPlanStatus.AssemblySteering);

        var sut = BuildAssembly(OwnerPod);
        var tick = await sut.AssemblyHeartbeatTickAsync(planId, ct: default);

        tick.Should().Be(CoordinatorAssemblyService.AssemblyLeaseTick.Renewed);
        var (podId, status, updatedAt) = await GetPlanLeaseAsync(planId);
        podId.Should().Be(OwnerPod, "the renew must not change ownership");
        status.Should().Be(WorkPlanStatus.AssemblySteering);
        updatedAt.Should().BeAfter(staleUpdatedAt, "the steering decision window is also kept fresh by the heartbeat");
        updatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AssemblyHeartbeatTick_DoesNotRenew_InReview_PreservingAbandonTimer()
    {
        const string coord = "coord-asm-inreview";
        // in_review is deliberately OUTSIDE the heartbeat's status set: renewing it would keep bumping
        // UpdatedAt and defeat the reconciler's 24h now-UpdatedAt stale-review abandon backstop.
        var reviewSince = DateTimeOffset.UtcNow.AddSeconds(-100);
        var planId = await SeedPlanAsync(coord, OwnerPod, reviewSince, WorkPlanStatus.InReview);

        var sut = BuildAssembly(OwnerPod);
        var tick = await sut.AssemblyHeartbeatTickAsync(planId, ct: default);

        tick.Should().Be(CoordinatorAssemblyService.AssemblyLeaseTick.Idle);
        var (podId, status, updatedAt) = await GetPlanLeaseAsync(planId);
        podId.Should().Be(OwnerPod, "ownership is untouched");
        status.Should().Be(WorkPlanStatus.InReview);
        updatedAt.Should().BeCloseTo(reviewSince, TimeSpan.FromSeconds(1),
            "in_review must NOT be renewed so the stale-review abandon timer keeps counting from the review start");
    }

    [Fact]
    public async Task PeerReconciler_DoesNotReclaim_FreshlyRenewedAssemblingPlan_ButReclaimsWhenStale()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        // Plan is owned by OwnerPod but its lease has gone stale (a long assembly with no heartbeat).
        var planId = await SeedPlanAsync(coord, OwnerPod, DateTimeOffset.UtcNow.AddMinutes(-5), WorkPlanStatus.Assembling);

        var owner = BuildAssembly(OwnerPod);
        var peerReconciler = BuildReconciler(PeerPod);

        // The owner's assembly heartbeat renews the lease -> a peer sweep must NOT re-arm the busy plan.
        (await owner.AssemblyHeartbeatTickAsync(planId, ct: default))
            .Should().Be(CoordinatorAssemblyService.AssemblyLeaseTick.Renewed);
        (await peerReconciler.SweepAsync(default)).Should().Be(0,
            "a freshly-renewed Assembling lease owned by a live peer must not be re-armed");
        (await GetPlanLeaseAsync(planId)).PodId.Should().Be(OwnerPod, "ownership stayed with the live owner");
        _assembly.Started.Should().BeEmpty("no re-arm happened while the lease was fresh");

        // Simulate the owner going away (no more heartbeats): the lease goes stale and the peer re-arms.
        await ForceLeaseStaleAsync(planId, DateTimeOffset.UtcNow.AddMinutes(-5));
        (await peerReconciler.SweepAsync(default)).Should().Be(1,
            "once the Assembling lease is stale the peer reconciler re-arms the orphaned assembly");
        _assembly.Started.Should().ContainSingle(c => c.CoordinatorRunId == coord,
            "the stale assembly was re-armed via StartAssembly on the peer");
    }

    [Fact]
    public async Task AssemblyHeartbeatTick_StopsWhenPeerOwnsRow()
    {
        const string coord = "coord-asm-peer";
        // The row is Assembling but a PEER already owns it (this owner was superseded).
        var planId = await SeedPlanAsync(coord, PeerPod, DateTimeOffset.UtcNow.AddSeconds(-100), WorkPlanStatus.Assembling);

        var sut = BuildAssembly(OwnerPod);
        var tick = await sut.AssemblyHeartbeatTickAsync(planId, ct: default);

        tick.Should().Be(CoordinatorAssemblyService.AssemblyLeaseTick.PeerOwned);
        var (podId, _, updatedAt) = await GetPlanLeaseAsync(planId);
        podId.Should().Be(PeerPod, "the peer's ownership is untouched");
        updatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(-100), TimeSpan.FromSeconds(2),
            "the owner pod must never renew a row that a peer owns");
    }

    [Fact]
    public async Task AssemblyLeaseHeartbeat_TransientTickError_DoesNotStopHeartbeat_RenewsOnNextTick()
    {
        const string coord = "coord-asm-flaky";
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var planId = await SeedPlanAsync(coord, OwnerPod, staleUpdatedAt, WorkPlanStatus.Assembling);

        // The first heartbeat tick fails (a transient scope/DB blip); every later tick succeeds.
        var flaky = new FlakyScopeFactory(_scopeFactory, failFirstN: 1);
        var sut = BuildAssembly(OwnerPod, flaky, heartbeatSeconds: 1);

        using var stop = new CancellationTokenSource();
        var task = sut.RunAssemblyLeaseHeartbeatAsync(planId, coord, stop.Token);

        // Wait until the lease is renewed. This can only happen if the loop survived the failing first tick.
        var renewed = await WaitUntilAsync(
            async () => (await GetPlanLeaseAsync(planId)).UpdatedAt > staleUpdatedAt,
            timeout: TimeSpan.FromSeconds(8));

        stop.Cancel();
        await task;

        renewed.Should().BeTrue("a transient failing tick must not stop the assembly heartbeat; a later tick renews the lease");
        flaky.Calls.Should().BeGreaterThan(1, "the first tick failed and at least one subsequent tick ran");
        (await GetPlanLeaseAsync(planId)).PodId.Should().Be(OwnerPod, "ownership is unchanged by the transient blip");
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return true;
            await Task.Delay(100);
        }
        return await predicate();
    }

    // A scope factory that throws on its first N CreateScope calls, then delegates to the real one.
    // Models a transient DB/SMB blip on a heartbeat tick without touching production code paths.
    private sealed class FlakyScopeFactory(IServiceScopeFactory inner, int failFirstN) : IServiceScopeFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IServiceScope CreateScope()
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= failFirstN)
                throw new InvalidOperationException("transient scope failure (test)");
            return inner.CreateScope();
        }
    }

    private CoordinatorDispatchService BuildDispatch(
        string podId, IServiceScopeFactory? scopeFactory = null, int? heartbeatSeconds = null)
    {
        var settings = new Dictionary<string, string?> { ["App:PodId"] = podId };
        if (heartbeatSeconds is { } hb)
            settings["Coordinator:PodLeaseHeartbeatSeconds"] =
                hb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var orchestrator = new RunOrchestrator(
            _runStore, _streamStore,
            worktreeManager: null!, workflowFactory: null!, registry: null!, watchLoop: null!,
            _scopeFactory, configuration: null!, NullLogger<RunOrchestrator>.Instance);

        return new CoordinatorDispatchService(
            _runStore, _streamStore, orchestrator, null!, new CoordinatorSteeringQueue(_scopeFactory), _assembly,
            scopeFactory ?? _scopeFactory, new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance,
            runOptions: null, autopilot: null, configuration: config);
    }

    private CoordinatorAssemblyService BuildAssembly(
        string podId, IServiceScopeFactory? scopeFactory = null, int? heartbeatSeconds = null)
    {
        var settings = new Dictionary<string, string?> { ["App:PodId"] = podId };
        if (heartbeatSeconds is { } hb)
            settings["Coordinator:PodLeaseHeartbeatSeconds"] =
                hb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // Only the lease-heartbeat surface is exercised (AssemblyHeartbeatTickAsync /
        // RunAssemblyLeaseHeartbeatAsync), which needs just the scope factory, pod id + interval, and
        // logger; the heavy assembly-pipeline collaborators are never touched here so they stay null.
        return new CoordinatorAssemblyService(
            runStore: _runStore,
            streamStore: _streamStore,
            assemblyStore: null!,
            reviewGate: null!,
            pipeline: null!,
            scopeFactory: scopeFactory ?? _scopeFactory,
            serviceProvider: _provider,
            lifetime: new TestHostApplicationLifetime(),
            logger: NullLogger<CoordinatorAssemblyService>.Instance,
            configuration: config);
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

    private Task<int> SeedDispatchingPlanAsync(string coordinatorRunId, string podId, DateTimeOffset updatedAt) =>
        SeedPlanAsync(coordinatorRunId, podId, updatedAt, WorkPlanStatus.Dispatching);

    private async Task<int> SeedPlanAsync(
        string coordinatorRunId, string podId, DateTimeOffset updatedAt, string status)
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
            Status = status,
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
