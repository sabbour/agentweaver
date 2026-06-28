using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Focused unit tests for the Feature 008 Phase 2 steering surface
/// (<see cref="CoordinatorSteeringService"/>). They exercise the real service against a real EF
/// <see cref="MemoryDbContext"/> (in-memory SQLite, no mocks — Principle VII) and assert the honest
/// directive lifecycle: <c>pause</c> is rejected, <c>stop</c> applies immediately (real
/// cancellation), and <c>redirect</c>/<c>amend</c> are queued for the next turn boundary.
/// </summary>
public sealed class CoordinatorSteeringServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunStreamStore _streamStore = new();
    private readonly RunWorkflowRegistry _registry = new();
    private readonly CoordinatorSteeringQueue _queue;
    private readonly CoordinatorSteeringService _sut;

    public CoordinatorSteeringServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _queue = new CoordinatorSteeringQueue(_scopeFactory);
        _sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance);
    }

    [Fact]
    public async Task Pause_IsRejected_AndNothingPersisted()
    {
        var act = async () => await _sut.SteerAsync("coord-1", "pause", null, "hold", "alice", default);

        (await act.Should().ThrowAsync<SteeringValidationException>())
            .Which.Message.Should().Contain("pause");

        (await CountDirectivesAsync()).Should().Be(0, "a rejected verb must not persist a directive");
    }

    [Theory]
    [InlineData("halt")]
    [InlineData("")]
    [InlineData("PAUSE ")] // normalized to pause -> still rejected
    public async Task UnsupportedOrDescopedVerb_IsRejected(string kind)
    {
        var act = async () => await _sut.SteerAsync("coord-1", kind, null, "do something", "alice", default);
        await act.Should().ThrowAsync<SteeringValidationException>();
        (await CountDirectivesAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("amend")]
    public async Task NextBoundaryVerb_RequiresInstruction(string kind)
    {
        var act = async () => await _sut.SteerAsync("coord-1", kind, "child-1", "   ", "alice", default);
        await act.Should().ThrowAsync<SteeringValidationException>();
        (await CountDirectivesAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("amend")]
    public async Task RedirectOrAmend_IsQueuedForNextTurnBoundary(string kind)
    {
        _streamStore.Create("coord-1", "alice");

        var view = await _sut.SteerAsync("coord-1", kind, "child-7", "use the v2 API", "alice", default);

        view.Kind.Should().Be(kind);
        view.Status.Should().Be(SteeringStatus.Queued, "redirect/amend never interrupt mid-turn; they queue");
        view.RelayedAt.Should().BeNull("a queued directive has not been relayed yet");
        view.TargetChildRunId.Should().Be("child-7");

        // Persisted as queued.
        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Status.Should().Be(SteeringStatus.Queued);
        persisted.CreatedBy.Should().Be("alice");

        // Parked in the durable (DB-backed) queue for the dispatch loop to drain at the boundary.
        var taken = await _queue.TryTakeForChildAsync("coord-1", "child-7");
        taken.Should().NotBeNull();
        taken!.DirectiveId.Should().Be(view.Id);
        taken.Instruction.Should().Be("use the v2 API");

        // A coordinator.steering event reflects the queued state.
        var events = _streamStore.Get("coord-1")!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorSteering);
    }

    [Fact]
    public async Task Stop_AppliesImmediately_AndDoesNotQueue()
    {
        _streamStore.Create("coord-1", "alice");

        // Register a real child run with a real CTS so we can assert true cancellation.
        var cts = new CancellationTokenSource();
        _streamStore.Create("child-9", "alice");
        _registry.Register("child-9", null!, cts);

        var view = await _sut.SteerAsync("coord-1", "stop", "child-9", "stop now", "alice", default);

        view.Kind.Should().Be(SteeringKind.Stop);
        view.Status.Should().Be(SteeringStatus.Applied, "stop collapses relayed->applied immediately");
        view.RelayedAt.Should().NotBeNull("an applied stop records when it was relayed");

        cts.IsCancellationRequested.Should().BeTrue("stop must really cancel the child run's token");

        // The child stream carries a terminal run.cancelled so the dispatch observer resolves it.
        var childEvents = _streamStore.Get("child-9")!.GetSnapshotSince(0).Events;
        childEvents.Should().Contain(e => e.Type == EventTypes.RunCancelled);
        _streamStore.Get("child-9")!.IsCompleted.Should().BeTrue();

        // stop never goes through the next-turn-boundary queue.
        (await _queue.TryTakeForChildAsync("coord-1", "child-9")).Should().BeNull();

        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Status.Should().Be(SteeringStatus.Applied);
        persisted.RelayedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Stop_Broadcast_CancelsAllActiveChildren()
    {
        _streamStore.Create("coord-1", "alice");
        await SeedActiveChildAsync("coord-1", "child-A", SubtaskStatus.Running);
        await SeedActiveChildAsync("coord-1", "child-B", SubtaskStatus.Dispatched);

        var ctsA = new CancellationTokenSource();
        var ctsB = new CancellationTokenSource();
        _streamStore.Create("child-A", "alice");
        _streamStore.Create("child-B", "alice");
        _registry.Register("child-A", null!, ctsA);
        _registry.Register("child-B", null!, ctsB);

        var view = await _sut.SteerAsync("coord-1", "stop", targetChildRunId: null, "abort all", "alice", default);

        view.Status.Should().Be(SteeringStatus.Applied);
        view.TargetChildRunId.Should().BeNull("a broadcast stop targets every active child");
        ctsA.IsCancellationRequested.Should().BeTrue();
        ctsB.IsCancellationRequested.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // send — informational nudge, applied immediately, no plan change.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Send_AppliesImmediately_WithoutQueuingOrDispatchChange()
    {
        _streamStore.Create("coord-send", "alice");

        var view = await _sut.SteerAsync("coord-send", "send", null, "note for the operator", "alice", default);

        view.Kind.Should().Be(SteeringKind.Send);
        view.Status.Should().Be(SteeringStatus.Applied, "send collapses to applied immediately");
        view.RelayedAt.Should().NotBeNull("an applied send records the relay time");
        view.TargetChildRunId.Should().BeNull("send is coordinator-level, not child-targeted");

        // Nothing queued in the next-boundary queue.
        (await _queue.TryTakeForChildAsync("coord-send", "any-child")).Should().BeNull("send never goes through the steering queue");

        // Persisted as applied.
        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Status.Should().Be(SteeringStatus.Applied);

        // A coordinator.steering event is emitted on the run stream.
        var events = _streamStore.Get("coord-send")!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorSteering,
            "send must emit a coordinator.steering event for the timeline");
    }

    [Fact]
    public async Task Send_DoesNotAlterDispatch_SubtaskStatusUnchanged()
    {
        const string coord = "coord-send-nodisrupt";
        _streamStore.Create(coord, "alice");

        // Seed a subtask in running status — send must leave it unchanged.
        await SeedActiveChildAsync(coord, "child-send-1", SubtaskStatus.Running);

        var view = await _sut.SteerAsync(coord, "send", null, "context update", "alice", default);

        view.Status.Should().Be(SteeringStatus.Applied);

        // Verify the subtask was not reset.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtask = await db.Subtasks.FirstAsync();
        subtask.Status.Should().Be(SubtaskStatus.Running, "send must not alter any subtask status");
    }

    [Fact]
    public async Task Send_DoesNotRequireInstruction_AcceptsEmptyString()
    {
        _streamStore.Create("coord-send-empty", "alice");

        // send does not require a non-empty instruction (unlike redirect/amend)
        var view = await _sut.SteerAsync("coord-send-empty", "send", null, "", "alice", default);

        view.Status.Should().Be(SteeringStatus.Applied);
        (await CountDirectivesAsync()).Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // -----------------------------------------------------------------------
    // Redirect vs Amend on parked coordinator: distinct subtask-reset behavior.
    // These tests live in CoordinatorSteeringRecoveryTests (which has the full
    // SqliteRunStore + ICoordinatorDispatch DI wiring required by
    // TryResumeParkedCoordinatorAsync).
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // Redirect force-cancel: when targeting a specific in-progress child.
    // (Full dispatch/DI tests for redirect vs amend on parked coordinators are in
    // CoordinatorSteeringRecoveryTests which has the full SqliteRunStore wiring.)
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------

    private async Task SeedActiveChildAsync(string coordinatorRunId, string childRunId, string status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var plan = await db.WorkPlans.FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId);
        if (plan is null)
        {
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

            plan = new WorkPlan
            {
                OutcomeSpecId = spec.Id,
                ProjectId = "proj-1",
                CoordinatorRunId = coordinatorRunId,
                Status = WorkPlanStatus.Dispatching,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkPlans.Add(plan);
            await db.SaveChangesAsync();
        }

        db.Subtasks.Add(new Subtask
        {
            WorkPlanId = plan.Id,
            Title = "t",
            Scope = "s",
            AssignedAgent = "morpheus",
            SelectedModelId = "gpt",
            Phase = "execution",
            IsolationStrategy = "worktree",
            Status = status,
            ChildRunId = childRunId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------
    // Replica-safety: the queue is DB-backed, so a directive enqueued on one pod
    // (DbContext) is drained on another pod (a SEPARATE DbContext) exactly once.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QueuedDirective_IsDrainedExactlyOnce_AcrossSeparateDbContexts()
    {
        _streamStore.Create("coord-xpod", "alice");

        // Producer pod: persist a queued redirect via SteerAsync (its own scoped DbContext).
        var view = await _sut.SteerAsync("coord-xpod", "redirect", "child-x", "switch to v2", "alice", default);
        view.Status.Should().Be(SteeringStatus.Queued);

        // Consumer pod: a queue instance backed by a DIFFERENT scope factory / DbContext, simulating
        // the dispatch loop running on the pod that owns the coordinator run.
        var consumerQueue = NewQueueOnSeparateDbContext();

        var first = await consumerQueue.TryTakeForChildAsync("coord-xpod", "child-x");
        first.Should().NotBeNull("the directive persisted on the producer pod must be visible on the consumer pod");
        first!.DirectiveId.Should().Be(view.Id);
        first.Instruction.Should().Be("switch to v2");

        // The atomic queued->relayed claim means a second drain (a re-poll, or another pod) gets nothing.
        var second = await consumerQueue.TryTakeForChildAsync("coord-xpod", "child-x");
        second.Should().BeNull("an already-claimed directive must never be delivered twice (at-most-once)");

        // The persisted row reflects the claim.
        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Status.Should().Be(SteeringStatus.Relayed);
        persisted.RelayedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task QueuedDirectives_AreDrainedInFifoOrder()
    {
        _streamStore.Create("coord-fifo", "alice");

        var first = await _sut.SteerAsync("coord-fifo", "redirect", "child-f", "step one", "alice", default);
        var second = await _sut.SteerAsync("coord-fifo", "redirect", "child-f", "step two", "alice", default);

        var consumerQueue = NewQueueOnSeparateDbContext();

        var taken1 = await consumerQueue.TryTakeForChildAsync("coord-fifo", "child-f");
        var taken2 = await consumerQueue.TryTakeForChildAsync("coord-fifo", "child-f");

        taken1!.DirectiveId.Should().Be(first.Id, "FIFO: the oldest queued directive drains first");
        taken1.Instruction.Should().Be("step one");
        taken2!.DirectiveId.Should().Be(second.Id, "FIFO: the next-oldest directive drains second");
        taken2.Instruction.Should().Be("step two");

        (await consumerQueue.TryTakeForChildAsync("coord-fifo", "child-f"))
            .Should().BeNull("both directives have been drained");
    }

    /// <summary>
    /// Builds a <see cref="CoordinatorSteeringQueue"/> over a fresh <see cref="ServiceProvider"/> that
    /// shares the same SQLite connection (so it sees the same physical table) but uses a SEPARATE
    /// <see cref="IServiceScopeFactory"/>/<see cref="MemoryDbContext"/> — simulating the dispatch loop
    /// running on a different pod than the one that handled the <c>/steer</c> request.
    /// </summary>
    private CoordinatorSteeringQueue NewQueueOnSeparateDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();
        return new CoordinatorSteeringQueue(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private async Task<int> CountDirectivesAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.CountAsync();
    }

    private async Task<SteeringDirective?> GetDirectiveAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
