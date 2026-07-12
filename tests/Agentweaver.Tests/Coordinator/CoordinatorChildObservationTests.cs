using System.Text.Json;
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
/// Tests for push-based child observation via <see cref="IRunEventStream.SubscribeAsync"/>
/// (016-US2). Verifies that the coordinator's dispatch loop subscribes to child event streams
/// rather than polling with Task.Delay, that replay survives a simulated process restart, and
/// that the TTL-based stall signal fires when no events arrive within the configured window.
/// </summary>
public sealed class CoordinatorChildObservationTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _streamConfig;
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();

    public CoordinatorChildObservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aw-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        CreateRunEventsTable(Path.Combine(_tempDir, "memory.db"));

        _streamConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_tempDir, "agentweaver.db"),
            })
            .Build();

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
    // US2-AC1: coordinator subscribes via await foreach — no Task.Delay
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_ChildEventsPreAppended_AreDeliveredViaReplay_NoPolling()
    {
        // Pre-append events to the child's durable log (simulates the child running and
        // completing before the coordinator's observation starts — covered by replay).
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "doing work" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        const string coord = "obs-replay-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "replay delivers the terminal event so the subtask resolves to assemble_ready");
    }

    // -----------------------------------------------------------------------
    // US2-AC2: child process restart — replay resumes from persisted events
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_PersistedEventsPresent_AfterSimulatedRestart_ReplayedCorrectly()
    {
        // Phase 1: a "prior process" appends events durably (including a terminal).
        var priorStream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "step 1" }));
        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "step 2" }));
        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        // Phase 1 ends without completing the channel (process crashed)

        // Phase 2: a "new process" creates a fresh SqliteRunEventStream (no in-memory channel)
        // and the coordinator loop re-observes the child by replaying from the durable log.
        var newStream = new SqliteRunEventStream(_streamConfig);
        const string coord = "obs-restart-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(newStream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "replay on the new process instance delivers the persisted terminal event");
    }

    // -----------------------------------------------------------------------
    // US2-AC3: stall TTL — no events within timeout → stall signal emitted
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_NoEventsWithinStallTtl_SubtaskFailedAsStalled()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        // Child is InProgress but emits no events — the stall TTL fires.
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        const string coord = "obs-stall-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // Configure an extremely short stall timeout (0.001 min ≈ 60 ms).
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Failed,
            "a child that emits no events within the stall TTL is failed by the dispatch loop");
        subtask.RecoveryGuidance.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // #212: unresolved tool-approval gate is a legitimate wait, not a stall
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_UnresolvedToolApproval_PastStallTtl_NotClassifiedAsStalled()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // Child raises a tool-approval gate then goes silent (the operator is deciding). The gate
        // is never resolved and the stream is never completed, so the child stays pending PAST the
        // stall window. The coordinator must treat this as a human-paced wait, not agent_stall_timeout.
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.ToolApprovalRequired,
            new { requestId = "appr-212", toolName = "web_fetch", url = "https://example.com/data" }));

        const string coord = "obs-approval-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // Extremely short stall TTL (≈60 ms) so many windows elapse within the test window; then
        // cancel to end the (otherwise indefinite) approval wait.
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await sut.RunDispatchLoopAsync(Context(coord), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the guard keeps observing the pending approval until we cancel the loop.
        }

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Running,
            "an unresolved tool-approval gate is a legitimate human-paced wait, not a stall (#212)");

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorChildApprovalRequired,
            "the child's tool.approval_required must be bubbled onto the coordinator stream");
        coordEvents.Should().NotContain(e => e.Type == EventTypes.CoordinatorChildStallDetected,
            "the coordinator must NOT emit a stall signal while a tool approval is pending (#212)");
    }

    // -----------------------------------------------------------------------
    // #217: an AgentHost pod still being provisioned by Kubernetes (claim unbound) is a
    // legitimate wait, not a stall — the coordinator must not discard the run.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_SandboxProvisioningPending_PastStallTtl_NotClassifiedAsStalled()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // The child's AgentHost pod is still being scheduled by Kubernetes: the only event on its
        // stream is a sandbox.provisioning_pending heartbeat, and the stream is never completed, so
        // the child stays silent PAST the stall window. A Pending pod may wait for a node to free up
        // or the pool to autoscale — the coordinator must treat this as a legitimate wait, not
        // agent_stall_timeout (#217).
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.SandboxProvisioningPending,
            new { claimName = "agent-host-217", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") }));

        const string coord = "obs-provisioning-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // Extremely short stall TTL (≈60 ms) so many windows elapse within the test window; then
        // cancel to end the (otherwise indefinite) provisioning wait.
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await sut.RunDispatchLoopAsync(Context(coord), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the guard keeps observing the provisioning pod until we cancel the loop.
        }

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Running,
            "a pod still being provisioned (sandbox.provisioning_pending) is a legitimate wait, not a stall (#217)");

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().NotContain(e => e.Type == EventTypes.CoordinatorChildStallDetected,
            "the coordinator must NOT emit a stall signal while the AgentHost pod is still provisioning (#217)");
    }

    [Fact]
    public async Task ObserveChild_ApprovalGateExpiresThenSilence_IsClassifiedAsStalled_GuardDoesNotLatch()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // Child raises a tool-approval gate, then the gate SELF-EXPIRES (pod emits ONLY tool.error,
        // never tool.approval_resolved), after which the pod genuinely hangs (no further events).
        // The guard must clear on tool.error and NOT latch — so the ensuing silence past the stall
        // TTL is correctly classified as agent_stall_timeout (#212 review finding 1: no permanent
        // suppression).
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.ToolApprovalRequired,
            new { requestId = "appr-expire", toolName = "web_fetch", url = "https://example.com/data" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.ToolError,
            new { requestId = "appr-expire", message = "URL fetch approval expired." }));

        const string coord = "obs-approval-expire-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Failed,
            "after gate self-expiry (tool.error) the guard clears, so a hung pod IS caught as stalled (#212)");

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorChildStallDetected,
            "silence after the gate expiry must produce a stall signal — the guard must not latch forever");
    }

    // -----------------------------------------------------------------------
    // US2-AC4: terminal event → subscription ends cleanly
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_TerminalEventViaLiveChannel_SubscriptionEndsCleanly()
    {
        // Use a live channel: start the dispatch loop in the background, then inject events
        // concurrently so they arrive via the Channel path (not replay).
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);
        const string coord = "obs-live-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        using var loopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start the dispatch loop in the background so it begins subscribing to the child.
        var loopTask = Task.Run(() => sut.RunDispatchLoopAsync(Context(coord), loopCts.Token), loopCts.Token);

        // Give the loop a moment to start subscribing, then inject live events.
        await Task.Delay(150);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "live work" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        await loopTask;

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "the terminal event delivered via the live channel resolves the subtask cleanly");
    }

    // -----------------------------------------------------------------------
    // US2-AC4b: interaction bubbling — question event reaches coordinator stream
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_ChildQuestionEvent_IsBubbledOntoCoordinatorStream()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // Pre-append a question followed by a terminal event.
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentQuestionAsked,
            new { requestId = "req-42", question = "Shall I proceed?" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady,
            new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        const string coord = "obs-bubble-coord";
        await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorChildQuestion,
            "a child AgentQuestionAsked event must be bubbled onto the coordinator stream");
    }

    [Fact]
    public async Task RunDispatchLoop_CoordinatorStoppedAfterActiveChild_DoesNotDispatchRemainingPendingSubtasks()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord, RunStatus.Failed);
        var childRunId = await SeedChildRunAsync(RunStatus.Failed);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunCancelled, new { reason = "steering_stop" }));
        await stream.CompleteAsync(childRunId);

        var (_, ids) = await SeedPlanAsync(coord,
            [(SubtaskStatus.Running, childRunId), (SubtaskStatus.Pending, null)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Failed);
        var pending = await GetSubtaskAsync(ids[1]);
        pending.Status.Should().Be(SubtaskStatus.Pending,
            "a stopped coordinator must not launch new children after active stop cancellation is observed");
        pending.ChildRunId.Should().BeNull();
        _assembly.Started.Should().Be(0, "stopped dispatch must not hand off to assembly");
    }

    // -----------------------------------------------------------------------
    // MID-RUN STEERING drain (Feature 008 Phase 2; #226 mid-run counterpart).
    //
    // Ahmed's doubt: when a human messages a RUNNING coordinator between subtask
    // turns, does the directive get PICKED UP AND ACTED ON, or silently dropped the
    // way a steer at the assembly review gate was in #226 (queued into a void that
    // nothing drained)?
    //
    // These drive the FULL mid-run cycle deterministically through the REAL service +
    // REAL dispatch loop:
    //   (1) QUEUE  — CoordinatorSteeringService.SteerAsync (the exact code path
    //       POST /api/runs/{id}/steer calls) queues the directive for a live,
    //       non-parked, non-review-gate coordinator via QueueNextBoundaryAsync.
    //   (2) DRAIN  — RunDispatchLoopAsync, when the in-flight child reaches its next
    //       turn boundary, CLAIMS the queued directive at the TryTakeForChild* seam
    //       (CoordinatorDispatchService ~L417/432): queued -> relayed, and begins
    //       injecting it as a revised turn CARRYING the human's instruction.
    //
    // The boundary is driven deterministically by replaying the child's durable
    // terminal event (assemble_ready) — no timing/sleep race. The terminal `applied`
    // transition and the child agent actually re-executing require a live child
    // workflow (a real worktree + agent), which is out of scope for this hermetic
    // unit host; the queue -> drain (queued -> relayed, event carrying the
    // instruction) is the property that refutes "silently dropped".
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Steer_MidRun_RedirectQueuedThenDrainedAndInjectedAtNextBoundary()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // The child is mid-flight and reaches a clean turn boundary (assemble_ready) via replay.
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "working" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        // A non-GUID coordinator id keeps SteerAsync on the live-queue path (RunId.TryParse fails, so
        // the parked-coordinator resume short-circuits) — mirroring the lightweight steering unit tests.
        const string coord = "midrun-redirect-coord";
        await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // (1) QUEUE — a human redirect lands on the RUNNING coordinator.
        const string instruction = "switch the failing subtask to the v2 API endpoint";
        var steering = BuildSteering();
        var view = await steering.SteerAsync(coord, SteeringKind.Redirect, childRunId, instruction, "alice", default);

        view.Status.Should().Be(SteeringStatus.Queued,
            "a mid-run redirect never interrupts the turn; it queues for the child's next turn boundary");
        (await GetDirectiveAsync(view.Id))!.Status.Should().Be(SteeringStatus.Queued,
            "before the boundary the directive is persisted queued (the durable SteeringDirectives row IS the queue)");

        // (2) DRAIN + INJECT — run the LIVE dispatch loop.
        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        // The KEY anti-#226 property: the loop DRAINED the queued directive at the child's boundary —
        // it is no longer sitting queued in a void.
        var drained = await GetDirectiveAsync(view.Id);
        drained!.Status.Should().NotBe(SteeringStatus.Queued,
            "the live dispatch loop must drain the queued directive at the child's next turn boundary — never leave it queued into a void (#226)");
        drained.Status.Should().Be(SteeringStatus.Relayed,
            "TryTakeForChildAsync claims the directive queued -> relayed and TryInjectSteeringRevisionAsync begins injecting it");
        drained.RelayedAt.Should().NotBeNull("a drained directive records when it was relayed to the child's control seam");

        // The loop emits coordinator.steering{relayed} CARRYING the human instruction: it is injecting
        // the human's directive into the child's revised turn, not just observing it.
        var relayed = RelayedSteeringPayloads(coord);
        relayed.Should().ContainSingle("the boundary drain relays exactly the one queued directive");
        relayed[0]["kind"]!.GetValue<string>().Should().Be(SteeringKind.Redirect);
        relayed[0]["targetChildRunId"]!.GetValue<string>().Should().Be(childRunId,
            "the drained redirect is scoped to the child it targeted");
        relayed[0]["instruction"]!.GetValue<string>().Should().Be(instruction,
            "the drained directive re-dispatches the child carrying the human's steering instruction");
    }

    [Fact]
    public async Task Steer_MidRun_SendAdvisoryQueuedThenDrainedAtNextBoundary()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "working" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        const string coord = "midrun-send-coord";
        await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // A mid-run advisory 'send' (broadcast — no target child) must ALSO be drained at the next
        // boundary rather than dropped.
        const string note = "heads up: prefer structured logging in the remaining work";
        var steering = BuildSteering();
        var view = await steering.SteerAsync(coord, SteeringKind.Send, targetChildRunId: null, note, "alice", default);
        view.Status.Should().Be(SteeringStatus.Queued, "a mid-run advisory send queues for the next safe boundary");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var drained = await GetDirectiveAsync(view.Id);
        drained!.Status.Should().NotBe(SteeringStatus.Queued,
            "the live dispatch loop must also drain a mid-run advisory send at the child's next boundary (not drop it)");
        drained.Status.Should().Be(SteeringStatus.Relayed);

        var relayed = RelayedSteeringPayloads(coord);
        relayed.Should().ContainSingle();
        relayed[0]["kind"]!.GetValue<string>().Should().Be(SteeringKind.Send);
        relayed[0]["instruction"]!.GetValue<string>().Should().Be(note,
            "the drained send carries the operator's advisory note to the child's next turn");
    }

    // -----------------------------------------------------------------------
    // MID-RUN STEERING — coordinator-scoped AMEND, full-lifecycle regression lock.
    //
    // Mirrors the LIVE staging evidence Ahmed captured refuting "steering doesn't
    // work mid-run": directive id=9, kind=amend, targetChildRunId=null
    // (coordinator-scoped), sent to an in-flight coordinator run, observed on the
    // run stream going queued(seq460) -> relayed(seq461) -> applied(seq464) in
    // ~4s, every event carrying the exact human instruction.
    //
    // WHY THIS IS NEW COVERAGE (not a dup of the two tests above):
    //   - Steer_MidRun_Redirect...      : CHILD-scoped `redirect` (targetChildRunId set).
    //   - Steer_MidRun_SendAdvisory...  : broadcast `send`.
    //   Neither exercises a coordinator-scoped `amend` (kind=amend, target=null) —
    //   the EXACT directive shape from the live evidence — nor the anti-#226
    //   "never left queued" property for `amend`. `amend` also has distinct
    //   semantics from `redirect` (additive; NOT applied on a child FAILURE — see
    //   the Failed branch in RunDispatchLoopAsync that only claims a redirect), so
    //   locking its clean-boundary drain in is a separate, real oracle.
    //
    // TERMINAL `applied` — WHERE IT IS PROVEN, AND WHY IT IS NOT ASSERTED HERE:
    //   The dispatch loop reaches `applied` ONLY inside
    //   CoordinatorDispatchService.TryInjectSteeringRevisionAsync (~L1345-1367),
    //   and ONLY AFTER `_orchestrator.StartRevisionAsync(child, ...)` SUCCEEDS:
    //   relayed -> applied is strictly gated behind the revised child turn actually
    //   launching. StartRevisionAsync needs a real worktree + MAF workflow + live
    //   agent execution (it throws "has no worktree path" for the seeded child, and
    //   this hermetic host wires null workflow/registry/watch collaborators), so
    //   `applied` CANNOT be driven deterministically here without real sandbox/agent
    //   execution. Introducing a stub seam would require a PRODUCTION change, which
    //   is explicitly out of scope for this test-only regression lock. Therefore the
    //   terminal transition is proven at the OTHER two layers, and this test asserts
    //   the strongest deterministic property the hermetic unit host supports:
    //     * PROVEN LIVE (staging): seq460 queued -> seq461 relayed -> seq464 applied,
    //       all carrying the human instruction (the evidence this test guards against
    //       silently regressing).
    //     * PROVEN AT SERVICE LEVEL: UnifiedSteeringTests drives the relayed -> applied
    //       state machine (ProbeRevisionEffect / Decorator effect-confirmation and the
    //       synchronous applied paths) deterministically.
    //     * PROVEN HERE (this test): a coordinator-scoped `amend` queued on a RUNNING
    //       coordinator is DRAINED at the child's next turn boundary (queued -> relayed),
    //       records RelayedAt, and is RELAYED carrying the human instruction — i.e. it is
    //       ACTED ON, never silently dropped / left queued into a void (#226).
    //   Layer honesty: this test owns queued->relayed for the coordinator-scoped amend;
    //   live + UnifiedSteeringTests own relayed->applied.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Steer_MidRun_CoordinatorScopedAmend_QueuedThenDrainedAndRelayed_CarriesInstruction()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // The child is mid-flight and reaches a clean turn boundary (assemble_ready) via replay —
        // the deterministic boundary at which a queued next-boundary directive is drained.
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "working" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        // A non-GUID coordinator id keeps SteerAsync on the live next-turn-boundary queue path
        // (RunId.TryParse fails, so TryResumeParkedCoordinatorAsync short-circuits) — the running,
        // non-parked, non-review-gate coordinator case POST /steer hits.
        const string coord = "midrun-amend-coord";
        await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // (1) QUEUE — a human COORDINATOR-SCOPED amend (targetChildRunId=null) lands on the RUNNING
        // coordinator, mirroring the live id=9 directive.
        const string instruction = "also add integration tests for the new endpoint before finishing";
        var steering = BuildSteering();
        var view = await steering.SteerAsync(coord, SteeringKind.Amend, targetChildRunId: null, instruction, "ahmed", default);

        view.Status.Should().Be(SteeringStatus.Queued,
            "a mid-run amend never interrupts the turn; it queues for the child's next turn boundary");
        view.Kind.Should().Be(SteeringKind.Amend);
        view.TargetChildRunId.Should().BeNull(
            "the amend is coordinator-scoped (broadcast, no specific child) — mirroring the live id=9 directive");
        (await GetDirectiveAsync(view.Id))!.Status.Should().Be(SteeringStatus.Queued,
            "before the boundary the durable SteeringDirectives row IS the queue (persisted queued)");

        // (2) DRAIN + RELAY — run the LIVE dispatch loop; the in-flight child hits its next boundary.
        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var drained = await GetDirectiveAsync(view.Id);

        // Anti-#226 (the property Ahmed doubted): the loop MUST drain the queued amend at the child's
        // next turn boundary — it is NEVER left sitting queued into a void that nothing reads.
        drained!.Status.Should().NotBe(SteeringStatus.Queued,
            "the live dispatch loop must drain the queued coordinator-scoped amend at the child's next boundary — never leave it queued into a void (#226)");

        // NEW coverage: a coordinator-scoped (null-target) amend is CLAIMED by the child-boundary drain
        // (broadcast match in CoordinatorSteeringQueue.TryTakeForChildAsync) and RELAYED — the
        // coordinator is ACTING on the human's directive, not merely observing it.
        drained.Status.Should().Be(SteeringStatus.Relayed,
            "TryTakeForChildAsync claims the null-target amend (broadcast) queued -> relayed and TryInjectSteeringRevisionAsync begins injecting it");
        drained.RelayedAt.Should().NotBeNull(
            "a drained directive records when it was relayed to the child's control seam");

        // The loop emits coordinator.steering{relayed} CARRYING the human instruction, kind=amend,
        // coordinator-scoped (null target) — the shape observed live at seq461.
        var relayed = RelayedSteeringPayloads(coord);
        relayed.Should().ContainSingle("the boundary drain relays exactly the one queued amend");
        relayed[0]["kind"]!.GetValue<string>().Should().Be(SteeringKind.Amend);
        relayed[0]["targetChildRunId"].Should().BeNull(
            "the drained amend is coordinator-scoped — the relayed event preserves the null (broadcast) target");
        relayed[0]["instruction"]!.GetValue<string>().Should().Be(instruction,
            "the drained amend re-dispatches the child carrying the human's steering instruction (the same instruction proven to reach `applied` live at seq464)");
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the real steering surface over the shared stream store + scope factory. No run store or
    /// review gate is wired, so — combined with a non-GUID coordinator id — SteerAsync stays on the
    /// live next-turn-boundary queue path (QueueNextBoundaryAsync), exactly as POST /steer does for a
    /// running, non-parked, non-review-gate coordinator.
    /// </summary>
    private CoordinatorSteeringService BuildSteering() => new(
        _streamStore,
        new RunWorkflowRegistry(),
        _scopeFactory,
        NullLogger<CoordinatorSteeringService>.Instance);

    /// <summary>The <c>coordinator.steering</c> event payloads on the coordinator stream whose status is <c>relayed</c>.</summary>
    private List<System.Text.Json.Nodes.JsonObject> RelayedSteeringPayloads(string coordinatorRunId) =>
        _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteering)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .Where(p => p["status"]!.GetValue<string>() == SteeringStatus.Relayed)
            .ToList();

    private async Task<SteeringDirective?> GetDirectiveAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    private CoordinatorDispatchService BuildDispatch(
        IRunEventStream eventStream,
        double stallTimeoutMinutes = 5)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:SubtaskStallTimeoutMinutes"] =
                    stallTimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            runOptions: null, autopilot: null, configuration: config, eventStream: eventStream);
    }

    private static CoordinatorDispatchContext Context(string coord) =>
        new(coord, "repo", "main", "owner", null);

    private async Task<string> SeedChildRunAsync(RunStatus status, DateTimeOffset? startedAt = null)
    {
        var id = RunId.New();
        var run = new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child subtask",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "0",
        };
        await _runStore.InsertAsync(run);
        if (status != RunStatus.InProgress)
            await _runStore.UpdateStatusAsync(id, status, DateTimeOffset.UtcNow);
        return id.ToString();
    }

    private async Task<(int PlanId, List<int> SubtaskIds)> SeedPlanAsync(
        string coordinatorRunId,
        (string Status, string? ChildRunId)[] subtasks)
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        foreach (var (status, childRunId) in subtasks)
        {
            var subtask = new Subtask
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
            };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            ids.Add(subtask.Id);
        }

        return (plan.Id, ids);
    }

    private async Task<Subtask> GetSubtaskAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private async Task SeedCoordinatorRunAsync(string coordinatorRunId, RunStatus status)
    {
        var run = new Run
        {
            Id = RunId.Parse(coordinatorRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "coordinate",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
        };
        await _runStore.InsertAsync(run);
        if (status != RunStatus.InProgress)
            await _runStore.UpdateStatusAsync(run.Id, status, DateTimeOffset.UtcNow);
    }

    private static void CreateRunEventsTable(string memoryDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={memoryDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
            """;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();

        await Task.Delay(50);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class RecordingAssembly : ICoordinatorAssembly
    {
        public int Started { get; private set; }
        public void StartAssembly(CoordinatorDispatchContext context) => Started++;
        public void EnsureFinalScribe(Run coordinatorRun) { }
        public bool IsAssemblyActive(string coordinatorRunId) => false;
        public void AbandonStaleReview(CoordinatorDispatchContext context) { }
        public void FailAssembly(CoordinatorDispatchContext context, string reason) { }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
