using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Sandbox;

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
        await SeedActiveChildAsync("coord-1", "child-7", SubtaskStatus.Running);

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
    // #350 — steering `stop` must reliably tear down the remote AgentHost pod, not just cancel
    // the local CancellationTokenSource (which has no effect on a pod-per-run sandbox process).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Stop_WhenPodPerRun_ReleasesTargetedChildsAgentHostPod()
    {
        var lifecycle = new TrackingPodLifecycle();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            podLifecycle: lifecycle,
            sandboxRuntime: Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" }));

        _streamStore.Create("coord-pod-1", "alice");
        var cts = new CancellationTokenSource();
        _streamStore.Create("child-pod-1", "alice");
        _registry.Register("child-pod-1", null!, cts);

        await sut.SteerAsync("coord-pod-1", "stop", "child-pod-1", "stop now", "alice", default);

        lifecycle.ReleasedRunIds.Should().Contain("child-pod-1",
            "a steering stop must reliably tear down the remote AgentHost pod, not just cancel the local token");
    }

    [Fact]
    public async Task Stop_Broadcast_WhenPodPerRun_ReleasesEveryChildsPod_AndCoordinatorsOwnPod()
    {
        var lifecycle = new TrackingPodLifecycle();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            runStore: new AlwaysSucceedsRunStore(),
            podLifecycle: lifecycle,
            sandboxRuntime: Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" }));

        // StopCoordinatorRunAsync (invoked for a broadcast stop) requires a GUID-parseable run id —
        // RunId.TryParse gates it — so unlike the plain-string ids used elsewhere in this file, the
        // coordinator id here must be a real GUID for that branch to actually execute.
        var coordinatorRunId = Guid.NewGuid().ToString();
        _streamStore.Create(coordinatorRunId, "alice");
        await SeedActiveChildAsync(coordinatorRunId, "child-pod-A", SubtaskStatus.Running);
        await SeedActiveChildAsync(coordinatorRunId, "child-pod-B", SubtaskStatus.Dispatched);
        _streamStore.Create("child-pod-A", "alice");
        _streamStore.Create("child-pod-B", "alice");
        _registry.Register("child-pod-A", null!, new CancellationTokenSource());
        _registry.Register("child-pod-B", null!, new CancellationTokenSource());

        await sut.SteerAsync(coordinatorRunId, "stop", targetChildRunId: null, "abort all", "alice", default);

        lifecycle.ReleasedRunIds.Should().Contain(["child-pod-A", "child-pod-B", coordinatorRunId],
            "a broadcast stop must release every active child's pod AND the coordinator's own pod");
    }

    [Fact]
    public async Task Stop_WhenInApiMode_DoesNotCallReleaseAgentHostPod()
    {
        var lifecycle = new TrackingPodLifecycle();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            podLifecycle: lifecycle,
            sandboxRuntime: Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "in-api" }));

        _streamStore.Create("coord-pod-inapi", "alice");
        _streamStore.Create("child-pod-inapi", "alice");
        _registry.Register("child-pod-inapi", null!, new CancellationTokenSource());

        await sut.SteerAsync("coord-pod-inapi", "stop", "child-pod-inapi", "stop now", "alice", default);

        lifecycle.ReleasedRunIds.Should().BeEmpty(
            "in-api mode has no remote pod to release");
    }

    [Fact]
    public async Task Stop_WhenPodLifecycleIsNull_DoesNotThrow()
    {
        // No podLifecycle wired (matches production when not running in Kubernetes) — must be a
        // silent no-op, never an exception that could break the steering directive.
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            sandboxRuntime: Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" }));

        _streamStore.Create("coord-pod-null", "alice");
        _streamStore.Create("child-pod-null", "alice");
        _registry.Register("child-pod-null", null!, new CancellationTokenSource());

        var act = async () => await sut.SteerAsync("coord-pod-null", "stop", "child-pod-null", "stop now", "alice", default);

        await act.Should().NotThrowAsync("a null podLifecycle must be a silent no-op");
    }

    // -----------------------------------------------------------------------
    // send — informational nudge, queued for the owning coordinator loop, no plan change.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Send_QueuesForSafeBoundary_WithoutDispatchChange()
    {
        _streamStore.Create("coord-send", "alice");
        await SeedActiveChildAsync("coord-send", "child-send", SubtaskStatus.Running);

        var view = await _sut.SteerAsync("coord-send", "send", null, "note for the operator", "alice", default);

        view.Kind.Should().Be(SteeringKind.Send);
        view.Status.Should().Be(SteeringStatus.Queued, "send waits for the coordinator-owned safe boundary");
        view.RelayedAt.Should().BeNull("a queued send has not been relayed yet");
        view.TargetChildRunId.Should().BeNull("send is coordinator-level, not child-targeted");

        // Queued in the next-boundary queue as a broadcast send.
        var taken = await _queue.TryTakeForChildAsync("coord-send", "child-send");
        taken.Should().NotBeNull();
        taken!.Kind.Should().Be(SteeringKind.Send);
        taken.DirectiveId.Should().Be(view.Id);

        // Persisted as relayed once the owning coordinator loop claims it.
        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Status.Should().Be(SteeringStatus.Relayed);

        // A coordinator.steering event is emitted on the run stream for the queued state.
        var events = _streamStore.Get("coord-send")!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorSteering,
            "send must emit a coordinator.steering event for the timeline");
    }

    [Fact]
    public async Task Send_TargetedChild_PreservesTargetInResponseEventAndQueue()
    {
        const string coord = "coord-send-targeted";
        _streamStore.Create(coord, "alice");
        await SeedActiveChildAsync(coord, "child-target", SubtaskStatus.Running);
        await SeedActiveChildAsync(coord, "child-other", SubtaskStatus.Running);

        var view = await _sut.SteerAsync(coord, "send", "child-target", "context for only this child", "alice", default);

        view.Kind.Should().Be(SteeringKind.Send);
        view.Status.Should().Be(SteeringStatus.Queued);
        view.TargetChildRunId.Should().Be("child-target",
            "a selected-child composer message must round-trip its target instead of looking like a whole-run broadcast");

        (await _queue.TryTakeForChildAsync(coord, "child-other")).Should().BeNull(
            "targeted child messages must not drain on sibling child boundaries");

        var taken = await _queue.TryTakeForChildAsync(coord, "child-target");
        taken.Should().NotBeNull();
        taken!.DirectiveId.Should().Be(view.Id);
        taken.TargetChildRunId.Should().Be("child-target");
        taken.Instruction.Should().Be("context for only this child");

        var evt = _streamStore.Get(coord)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.CoordinatorSteering);
        var payload = JsonSerializer.SerializeToNode(evt.Payload)!.AsObject();
        payload["targetChildRunId"]!.GetValue<string>().Should().Be("child-target",
            "Trinity's UI can attribute the accepted message to the selected child context");
    }

    [Fact]
    public async Task Send_DoesNotAlterDispatch_SubtaskStatusUnchanged()
    {
        const string coord = "coord-send-nodisrupt";
        _streamStore.Create(coord, "alice");

        // Seed a subtask in running status — send must leave it unchanged.
        await SeedActiveChildAsync(coord, "child-send-1", SubtaskStatus.Running);

        var view = await _sut.SteerAsync(coord, "send", null, "context update", "alice", default);

        view.Status.Should().Be(SteeringStatus.Queued);

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

        view.Status.Should().Be(SteeringStatus.Queued);
        (await CountDirectivesAsync()).Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Cross-pod event surfacing (regression, #lost-coordinator-messages).
    // When the /steer POST lands on a replica that does NOT own the coordinator's
    // in-memory stream (RunStreamStore.Get -> null), the coordinator.steering event
    // must still be surfaced by falling back to the durable IRunEventStream. Before
    // the fix the `entry?.RecordNext(...)` null-conditional silently dropped the event
    // at replicas:2, so a steered message never appeared in the operator's session.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Send_WhenReplicaDoesNotOwnStream_SurfacesEventViaDurableEventStream()
    {
        var durable = new RecordingEventStream();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            eventStream: durable);

        // NOTE: intentionally do NOT call _streamStore.Create — this replica does not own the
        // coordinator run's in-memory stream (the dispatch/assembly loop runs on another pod).
        _streamStore.Get("coord-xpod-send").Should().BeNull("precondition: this replica is not the stream owner");

        var view = await sut.SteerAsync("coord-xpod-send", "send", null, "cross-pod nudge", "alice", default);

        view.Status.Should().Be(SteeringStatus.Queued);

        // The event must have been appended to the durable (cross-replica) stream so the operator's
        // timeline — served by whichever replica owns the SSE connection — still surfaces it.
        durable.Appended.Should().ContainSingle(e =>
            e.RunId == "coord-xpod-send" && e.Event.Type == EventTypes.CoordinatorSteering,
            "a send that lands on a non-owner replica must fall back to the durable event stream");
    }

    [Fact]
    public async Task RedirectQueued_WhenReplicaDoesNotOwnStream_SurfacesQueuedEventViaDurableEventStream()
    {
        var durable = new RecordingEventStream();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            eventStream: durable);

        // No _streamStore.Create — non-owner replica.
        var view = await sut.SteerAsync("coord-xpod-redirect", "redirect", "child-x", "switch to v2", "alice", default);

        view.Status.Should().Be(SteeringStatus.Queued);
        durable.Appended.Should().ContainSingle(e =>
            e.RunId == "coord-xpod-redirect" && e.Event.Type == EventTypes.CoordinatorSteering,
            "a queued redirect on a non-owner replica must still surface its queued state durably");
    }

    [Fact]
    public async Task Send_WhenReplicaOwnsStream_UsesInMemoryStream_NotDurableFallback()
    {
        var durable = new RecordingEventStream();
        var sut = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            eventStream: durable);

        // This replica owns the in-memory stream.
        _streamStore.Create("coord-owner-send", "alice");

        await sut.SteerAsync("coord-owner-send", "send", null, "owned nudge", "alice", default);

        // Owner path records in-memory (which the RunStreamStore itself mirrors durably when wired);
        // the service's own durable fallback must NOT fire (no double-append from this seam).
        durable.Appended.Should().NotContain(e => e.RunId == "coord-owner-send",
            "when this replica owns the stream, the service records in-memory and does not double-append via its fallback");
        _streamStore.Get("coord-owner-send")!.GetSnapshotSince(0).Events
            .Should().Contain(e => e.Type == EventTypes.CoordinatorSteering);
    }

    /// <summary>
    /// A minimal recording <see cref="IRunEventStream"/> test double (a fake, not a mock framework)
    /// that captures every <see cref="AppendAsync"/> so cross-pod durable-fallback surfacing can be
    /// asserted deterministically without a file-backed stream.
    /// </summary>
    private sealed class RecordingEventStream : IRunEventStream
    {
        private readonly List<(string RunId, RunEvent Event)> _appended = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<(string RunId, RunEvent Event)> Appended
        {
            get { lock (_lock) return _appended.ToList(); }
        }

        public ValueTask AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            lock (_lock) _appended.Add((runId, evt));
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<RunEvent> SubscribeAsync(
            string runId, int fromSequence = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal <see cref="IRunStore"/> fake (a fake, not a mock framework) used only so
    /// <c>StopCoordinatorRunAsync</c> (gated on <c>_runStore is not null</c>) actually runs during
    /// #350 pod-teardown tests. Every member beyond <see cref="TrySetTerminalStatusAsync"/> throws —
    /// none are expected to be called on the broadcast-stop path under test.
    /// </summary>
    private sealed class AlwaysSucceedsRunStore : IRunStore
    {
        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => Task.FromResult<Run?>(null);
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Run>>(Array.Empty<Run>());
        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Run>>(Array.Empty<Run>());
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
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
        await SeedActiveChildAsync("coord-xpod", "child-x", SubtaskStatus.Running);

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
        await SeedActiveChildAsync("coord-fifo", "child-f", SubtaskStatus.Running);

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
