using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the D4 exactly-once compare-and-swap in <see cref="CoordinatorAssemblyStore"/>.
/// <c>TryStartAssemblyAsync</c> must transition <c>awaiting_assembly → assembling</c> for exactly one
/// caller and return false for all others, even under concurrent contention. Real EF + in-memory SQLite.
/// </summary>
public sealed class CoordinatorAssemblyStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoordinatorAssemblyStore _sut;

    public CoordinatorAssemblyStoreTests()
    {
        // Shared-cache in-memory DB so each scope opens its OWN connection (real concurrency); the
        // keep-alive connection keeps the shared in-memory database alive for the test's lifetime.
        // Microsoft.Data.Sqlite's built-in busy retry serializes concurrent writers without throwing.
        var connectionString = $"DataSource=file:assemblycas-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(connectionString));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _sut = new CoordinatorAssemblyStore(_scopeFactory);
    }

    [Fact]
    public async Task TryStartAssembly_ReturnsTrueOnce_FalseOnSecondCall()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.AwaitingAssembly);

        var first = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);
        var second = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);

        first.Should().BeTrue();
        second.Should().BeFalse();

        var state = await _sut.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.Assembling);
        state.IntegrationBranch.Should().Be("agentweaver/integration/x");
    }

    [Fact]
    public async Task TryStartAssembly_ReturnsFalse_WhenNotAwaitingAssembly()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.Dispatching);

        var result = await _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryStartAssembly_ConcurrentCallers_ExactlyOneWins()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.AwaitingAssembly);

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _sut.TryStartAssemblyAsync(workPlanId, "agentweaver/integration/x", default)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "exactly one caller may claim the assembly");
    }

    [Fact]
    public async Task SupersededAttemptOnSameReplicaCannotChangePlanState()
    {
        var workPlanId = await SeedPlanAsync(WorkPlanStatus.AwaitingAssembly);
        const string firstAttempt = "api-0:assembly:first";
        const string successorAttempt = "api-0:assembly:successor";

        (await _sut.TryStartAssemblyAsync(
            workPlanId,
            "agentweaver/integration/x/attempt-1",
            successorAttempt,
            default)).Should().BeTrue();

        await _sut.SetStageAsync(
            workPlanId, AssemblyStage.Scribe, default, firstAttempt);
        await _sut.SetStatusAndStageAsync(
            workPlanId, WorkPlanStatus.InReview, AssemblyStage.Review, default, firstAttempt);
        await _sut.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, "stale", default, firstAttempt);

        var current = await _sut.GetAsync(workPlanId, default);
        current!.Status.Should().Be(WorkPlanStatus.Assembling);
        current.AssemblyStage.Should().BeNull();
        current.AssemblyStatusReason.Should().BeNull();

        await _sut.SetTerminalStatusAsync(
            workPlanId, WorkPlanStatus.AssemblyFailed, "current", default, successorAttempt);
        current = await _sut.GetAsync(workPlanId, default);
        current!.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
        current.AssemblyStatusReason.Should().Be("current");
    }

    [Fact]
    public async Task StaleLeaseTokenCannotReclaimOrRestartPlanAfterTakeover()
    {
        const string staleOwner = "api-0:assembly:stale";
        const string currentOwner = "api-0:assembly:current";
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.Assembling,
            assemblyStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            coordinatorPodId: staleOwner,
            assemblyFencingToken: 4);
        var staleBefore = DateTimeOffset.UtcNow.AddSeconds(-120);

        (await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId,
            staleBefore,
            staleOwner,
            fencingToken: 4,
            "agentweaver/integration/x/attempt-4",
            default)).Should().BeFalse();

        (await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId,
            staleBefore,
            currentOwner,
            fencingToken: 5,
            "agentweaver/integration/x/attempt-5",
            default)).Should().BeTrue();

        var claimed = await _sut.GetAsync(workPlanId, default);
        claimed!.Status.Should().Be(WorkPlanStatus.AwaitingAssembly);
        claimed.AssemblyFencingToken.Should().Be(5);
        claimed.CoordinatorPodId.Should().Be(currentOwner);
        claimed.IntegrationBranch.Should().Be("agentweaver/integration/x/attempt-5");

        (await _sut.TryStartAssemblyAsync(
            workPlanId,
            "agentweaver/integration/x/attempt-4",
            staleOwner,
            fencingToken: 4,
            default)).Should().BeFalse();
        (await _sut.TryStartAssemblyAsync(
            workPlanId,
            "agentweaver/integration/x/attempt-5",
            currentOwner,
            fencingToken: 5,
            default)).Should().BeTrue();
    }

    [Fact]
    public async Task TryReclaimStaleAssembly_FreshClaim_ReturnsFalse_LeavesAssembling()
    {
        // A fresh claim = another replica is actively building the integration branch right now.
        // Reclaiming would let a second pod race the git merge, so it must be refused.
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.Assembling,
            assemblyStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow);

        var reclaimed = await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId, staleBefore: DateTimeOffset.UtcNow.AddSeconds(-120), default);

        reclaimed.Should().BeFalse("a fresh assembling claim is owned by a live loop");
        (await _sut.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Assembling);
    }

    [Fact]
    public async Task TryReclaimStaleAssembly_StaleClaim_ReturnsTrue_ResetsToAwaiting()
    {
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.Assembling,
            assemblyStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var reclaimed = await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId, staleBefore: DateTimeOffset.UtcNow.AddSeconds(-120), default);

        reclaimed.Should().BeTrue("a stale assembling claim (owner likely dead) is reclaimable");
        (await _sut.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.AwaitingAssembly);
    }

    [Fact]
    public async Task TryReclaimStaleAssembly_NullStartedAtWithStaleHeartbeat_ReturnsTrue()
    {
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.Assembling,
            assemblyStartedAt: null,
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var reclaimed = await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId, staleBefore: DateTimeOffset.UtcNow.AddSeconds(-120), default);

        reclaimed.Should().BeTrue("the renewable UpdatedAt heartbeat, not the one-time start timestamp, determines staleness");
    }

    [Fact]
    public async Task TryReclaimStaleAssembly_NotAssembling_ReturnsFalse()
    {
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.InReview, assemblyStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var reclaimed = await _sut.TryReclaimStaleAssemblyAsync(
            workPlanId, staleBefore: DateTimeOffset.UtcNow.AddSeconds(-120), default);

        reclaimed.Should().BeFalse("only an assembling plan is reclaimable");
    }

    [Fact]
    public async Task TryReclaimStaleAssembly_ConcurrentCallers_ExactlyOneReclaims()
    {
        var workPlanId = await SeedPlanAsync(
            WorkPlanStatus.Assembling,
            assemblyStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _sut.TryReclaimStaleAssemblyAsync(
                workPlanId, DateTimeOffset.UtcNow.AddSeconds(-120), default)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "exactly one caller may reclaim the stale assembly");
    }

    private async Task<int> SeedPlanAsync(
        string status,
        DateTimeOffset? assemblyStartedAt = null,
        DateTimeOffset? updatedAt = null,
        string? coordinatorPodId = null,
        long assemblyFencingToken = 0)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1",
            CoordinatorRunId = "coord-cas-" + Guid.NewGuid().ToString("N"),
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
            CoordinatorRunId = spec.CoordinatorRunId,
            Status = status,
            AssemblyStartedAt = assemblyStartedAt,
            CoordinatorPodId = coordinatorPodId,
            AssemblyFencingToken = assemblyFencingToken,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _keepAlive.Dispose();
    }
}
