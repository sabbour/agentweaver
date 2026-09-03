using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Run = Agentweaver.Domain.Run;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for issue #78: partial-success + child stall + dependency cascade +
/// assembly-blocked state.
///
/// Covers:
/// <list type="bullet">
/// <item>Git integration branch lock contention: <see cref="WorktreeManager.TryCleanIntegrationLockFiles"/>
/// removes stale lock files so a retry of <see cref="WorktreeManager.BuildIntegrationBranch"/>
/// succeeds.</item>
/// <item>Stall cascade: a subtask stalled by TTL causes dependent subtasks to enter
/// <see cref="SubtaskStatus.Blocked"/> (not <see cref="SubtaskStatus.Failed"/>) and emits the
/// <see cref="EventTypes.CoordinatorChildStallDetected"/> diagnostic event.</item>
/// <item>Assembly blocked: the <see cref="EventTypes.CoordinatorAssemblyBlocked"/> event payload
/// includes the ineligible subtask IDs and status for actionable diagnostics.</item>
/// <item>Partial-success + stall + cascade end-to-end: 3 assemble_ready, 1 stalled, 2 blocked
/// dependents yields assembly_blocked with all 3 ineligible subtasks named.</item>
/// </list>
/// </summary>
public sealed class StallCascadeAndLockRetryTests : IAsyncDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly string _tempDir;
    private readonly IConfiguration _streamConfig;
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();

    public StallCascadeAndLockRetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aw-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempDirs.Add(_tempDir);
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
        // Dispatching a child run now resolves the effective model provider through the shared
        // resolver, so the scope this fixture hands the dispatch service must be able to supply one.
        services.AddScoped(sp => AutomationTestServices.CreateModelProviderResolver(
            sp.GetRequiredService<MemoryDbContext>()));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    // -----------------------------------------------------------------------
    // Lock contention: TryCleanIntegrationLockFiles removes stale lock files
    // -----------------------------------------------------------------------

    [Fact]
    public void TryCleanIntegrationLockFiles_RemovesRefLockFile_BuildSucceedsOnRetry()
    {
        var repoPath = CreateTempGitRepo();
        var manager = new WorktreeManager(
            new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);
        const string integrationBranch = "agentweaver/integration/test-run-id";

        // Build the integration branch once so the ref exists.
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha", "child a");
        var first = manager.BuildIntegrationBranch(repoPath, "main", integrationBranch, ["agentweaver/child-a"]);
        first.Outcome.Should().Be(IntegrationBranchOutcome.Built);

        // Simulate a stale lock file left by a crashed process.
        var gitDir = Path.Combine(repoPath, ".git");
        var refRelPath = integrationBranch.Replace('/', Path.DirectorySeparatorChar);
        var refLockPath = Path.Combine(gitDir, "refs", "heads", refRelPath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(refLockPath)!);
        File.WriteAllText(refLockPath, "stale lock");
        // Backdate so it is genuinely stale (older than the stale-lock threshold), not an
        // actively-held lock that a concurrent replica's git operation might still need.
        File.SetLastWriteTimeUtc(refLockPath, DateTime.UtcNow.AddMinutes(-5));

        // TryCleanIntegrationLockFiles must delete the lock file.
        manager.TryCleanIntegrationLockFiles(repoPath, integrationBranch);

        File.Exists(refLockPath).Should().BeFalse(
            "TryCleanIntegrationLockFiles must delete the stale ref lock file");

        // BuildIntegrationBranch must succeed after the cleanup.
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "beta.txt", "beta", "child b");
        var second = manager.BuildIntegrationBranch(
            repoPath, "main", integrationBranch, ["agentweaver/child-a", "agentweaver/child-b"]);
        second.Outcome.Should().Be(IntegrationBranchOutcome.Built,
            "build must succeed after the stale lock file is removed");
        second.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void TryCleanIntegrationLockFiles_RemovesPackedRefsLockFile_BestEffort()
    {
        var repoPath = CreateTempGitRepo();
        var manager = new WorktreeManager(
            new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

        var gitDir = Path.Combine(repoPath, ".git");
        var packedRefsLock = Path.Combine(gitDir, "packed-refs.lock");
        File.WriteAllText(packedRefsLock, "stale packed-refs lock");
        // Backdate so it is genuinely stale (older than the stale-lock threshold).
        File.SetLastWriteTimeUtc(packedRefsLock, DateTime.UtcNow.AddMinutes(-5));

        manager.TryCleanIntegrationLockFiles(repoPath, "agentweaver/integration/any-run");

        File.Exists(packedRefsLock).Should().BeFalse(
            "TryCleanIntegrationLockFiles must delete a stale packed-refs.lock");
    }

    [Fact]
    public void TryCleanIntegrationLockFiles_DoesNotDeleteFreshLock_HeldByActiveOperation()
    {
        // Multi-replica safety: a freshly-created lock file is likely held by another replica's
        // in-flight git operation. Deleting it caused the integration-merge race, so a lock younger
        // than the stale threshold must be LEFT ALONE.
        var repoPath = CreateTempGitRepo();
        var manager = new WorktreeManager(
            new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

        var gitDir = Path.Combine(repoPath, ".git");
        var integrationBranch = "agentweaver/integration/live-run";
        var refRelPath = integrationBranch.Replace('/', Path.DirectorySeparatorChar);
        var refLockPath = Path.Combine(gitDir, "refs", "heads", refRelPath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(refLockPath)!);
        File.WriteAllText(refLockPath, "fresh lock held by another replica");
        // Fresh (just written) → within the stale threshold.

        manager.TryCleanIntegrationLockFiles(repoPath, integrationBranch);

        File.Exists(refLockPath).Should().BeTrue(
            "a fresh lock file (likely actively held) must NOT be deleted");
    }

    [Fact]
    public void ClearStaleIndexLock_ClearsStaleLock_WhenOnlyAgeGateApplies_NoFalseLiveProcessRefusal()
    {
        // FIX-1 (Fix-A #1 regression guard): the stale index.lock clear must FIRE on the age gate
        // alone. The prior host-global `git` process check refused the clear whenever ANY unrelated
        // `git` process existed on the host — which, on a busy coordinator (our own `git worktree
        // add/prune` + agent git-tool subprocesses), is almost always. That re-wedged commit-retry in
        // exactly the concurrent scenario Fix-A targets. This test would fail under that old guard on
        // any host running a git process.
        var repoPath = CreateTempGitRepo();
        var manager = new WorktreeManager(
            new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

        var lockPath = Path.Combine(repoPath, ".git", "index.lock");
        File.WriteAllText(lockPath, "stale index lock from a crashed turn");
        // Backdate well beyond the default 15s stale threshold so ONLY the age gate is relevant.
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-5));

        var result = manager.ClearStaleIndexLock(repoPath);

        result.LockPresent.Should().BeTrue();
        result.Cleared.Should().BeTrue("a lock older than the stale threshold must be cleared on the age gate alone");
        result.LiveGitProcessDetected.Should().BeFalse("the host-global git-process guard was removed (age-only guard)");
        result.LockAgeSeconds.Should().BeGreaterThan(15);
        File.Exists(lockPath).Should().BeFalse("the stale index.lock file must be deleted");
    }

    [Fact]
    public void ClearStaleIndexLock_RefusesFreshLock_WithinStaleThreshold()
    {
        // The age gate is the SOLE guard, so a fresh lock (within threshold) must still be refused.
        var repoPath = CreateTempGitRepo();
        var manager = new WorktreeManager(
            new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

        var lockPath = Path.Combine(repoPath, ".git", "index.lock");
        File.WriteAllText(lockPath, "fresh lock held by an active in-process operation");
        // Freshly written → within the 15s stale threshold.

        var result = manager.ClearStaleIndexLock(repoPath);

        result.LockPresent.Should().BeTrue();
        result.Cleared.Should().BeFalse("a lock younger than the stale threshold is presumed actively held");
        result.Detail.Should().Be("lock_too_recent");
        File.Exists(lockPath).Should().BeTrue("a fresh lock must NOT be deleted");
    }

    // -----------------------------------------------------------------------
    // Stall cascade: stalled subtask marks dependents as blocked (not failed)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StallCascade_StalledSubtask_DependentsMarkedBlocked_NotFailed()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        // Subtask A is already running (and will stall). Subtask B is pending and depends on A.
        var stalledChildRunId = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        const string coord = "stall-cascade-coord";
        var (_, ids) = await SeedPlanAsync(coord,
            [(SubtaskStatus.Running, stalledChildRunId), (SubtaskStatus.Pending, null)],
            declaredDependency: true); // ids[1] depends on ids[0]
        _streamStore.Create(coord, "owner");

        // #241: this cascade asserts the GENUINE-terminal path — a stall AFTER the bounded recovery
        // budget is exhausted. Seed RecoveryAttempts at the cap so the stall dead-ends immediately
        // (no redispatch) and the dependent-blocking cascade is exercised exactly as before #241.
        await SetRecoveryAttemptsAsync(ids[0], CoordinatorSteeringService.MaxRecoveryAttempts);

        // Extremely short stall TTL so the test resolves quickly.
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var stalledSubtask = await GetSubtaskAsync(ids[0]);
        var blockedSubtask = await GetSubtaskAsync(ids[1]);

        stalledSubtask.Status.Should().Be(SubtaskStatus.Failed,
            "the stalled subtask itself is failed by the dispatch loop");
        stalledSubtask.RecoveryGuidance.Should().NotBeNull();

        blockedSubtask.Status.Should().Be(SubtaskStatus.Blocked,
            "a dependent of a stalled subtask must be marked blocked, not failed");
        blockedSubtask.RecoveryGuidance.Should().Contain("dependency_stalled",
            "recovery guidance must name the reason so operators know this is a cascade");
    }

    [Fact]
    public async Task StallCascade_EmitsCoordinatorChildStallDetectedEvent()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var stalledChildRunId = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        const string coord = "stall-event-coord";
        var (_, ids) = await SeedPlanAsync(coord,
            [(SubtaskStatus.Running, stalledChildRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorChildStallDetected,
            "a structured stall diagnostic event must be emitted on the coordinator stream");

        var stallEvent = coordEvents.First(e => e.Type == EventTypes.CoordinatorChildStallDetected);
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(stallEvent.Payload)!.AsObject();
        payload["childRunId"]!.GetValue<string>().Should().Be(stalledChildRunId);
        payload["subtaskId"]!.GetValue<int>().Should().Be(ids[0]);
        payload["stallTimeoutMinutes"]!.GetValue<double>().Should().BePositive();
    }

    // -----------------------------------------------------------------------
    // SubtaskStatus.Blocked: terminal but not assembly-eligible
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(SubtaskStatus.Blocked, false)]
    public void BlockedStatus_IsTerminal_ButNotEligibleForAssembly(string status, bool _)
    {
        SubtaskStatus.IsTerminal(status).Should().BeTrue(
            "blocked is a terminal state — the subtask can make no further progress");
        AssemblyPlanning.IsEligible(status).Should().BeFalse(
            "blocked is not assembly-eligible — the subtask never produced output");
    }

    // -----------------------------------------------------------------------
    // Assembly blocked: ineligible subtasks include recoveryGuidance
    // -----------------------------------------------------------------------

    [Fact]
    public void AssemblyBlocked_IneligibleSubtasksPayload_IncludesBlockedStatus()
    {
        // Purely exercises AssemblyPlanning.IneligibleSubtasks: blocked subtasks are ineligible.
        var statusById = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.AssembleReady,
            [2] = SubtaskStatus.Blocked,
            [3] = SubtaskStatus.Failed,
            [4] = SubtaskStatus.Completed,
        };

        var ineligible = AssemblyPlanning.IneligibleSubtasks(statusById);
        ineligible.Should().Equal(new[] { 2, 3 },
            "blocked and failed subtasks are both ineligible for assembly");

        AssemblyPlanning.AllEligible(statusById).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Full cascade: partial-success + stall + dependent propagation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FullCascade_PartialSuccess_PlusStalledDependency_ProducesBlockedDependents()
    {
        // Scenario mirrors incident run 60291447: 3 subtasks succeed, 1 stalls, 2 are cascaded.
        var stream = new SqliteRunEventStream(_streamConfig);

        // Subtasks 44, 45, 46 succeed (already assemble_ready — re-arm scenario).
        var childA = await SeedChildRunAsync(RunStatus.AssembleReady);
        var childB = await SeedChildRunAsync(RunStatus.AssembleReady);
        var childC = await SeedChildRunAsync(RunStatus.AssembleReady);
        // Subtask 47 stalls.
        var childStalled = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));

        await stream.AppendAsync(childA, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childA);
        await stream.AppendAsync(childB, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childB);
        await stream.AppendAsync(childC, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childC);
        // childStalled emits nothing — the stall TTL fires.

        const string coord = "full-cascade-coord";
        // Subtasks 48, 49 (indices 4 and 5) depend on the stalled subtask (index 3).
        var (_, ids) = await SeedPlanAsync(coord,
        [
            (SubtaskStatus.Running, childA),    // 0
            (SubtaskStatus.Running, childB),    // 1
            (SubtaskStatus.Running, childC),    // 2
            (SubtaskStatus.Running, childStalled), // 3 — will stall
            (SubtaskStatus.Pending, null),      // 4 — depends on 3
            (SubtaskStatus.Pending, null),      // 5 — depends on 4
        ],
        // 4 depends on 3; 5 depends on 4 (chain propagation).
        dependencyPairs: [(4, 3), (5, 4)]);
        _streamStore.Create(coord, "owner");

        // #241: exercise the genuine-terminal cascade — the stalled subtask has already exhausted its
        // recovery budget, so the stall dead-ends (no redispatch) and propagates to blocked dependents.
        await SetRecoveryAttemptsAsync(ids[3], CoordinatorSteeringService.MaxRecoveryAttempts);

        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        // 44,45,46 must be assemble_ready.
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady);
        (await GetSubtaskAsync(ids[1])).Status.Should().Be(SubtaskStatus.AssembleReady);
        (await GetSubtaskAsync(ids[2])).Status.Should().Be(SubtaskStatus.AssembleReady);

        // 47 (stalled) must be failed.
        (await GetSubtaskAsync(ids[3])).Status.Should().Be(SubtaskStatus.Failed);

        // 48, 49 (dependents of stalled) must be blocked, not failed.
        var dep1 = await GetSubtaskAsync(ids[4]);
        dep1.Status.Should().Be(SubtaskStatus.Blocked,
            "direct dependent of stalled subtask must be blocked (not failed)");
        dep1.RecoveryGuidance.Should().Contain("dependency_stalled");

        var dep2 = await GetSubtaskAsync(ids[5]);
        dep2.Status.Should().Be(SubtaskStatus.Blocked,
            "transitive dependent of stalled subtask must also be blocked");

        // Assembly is handed off.
        _assembly.Started.Should().Be(1, "dispatch must hand off to assembly after all subtasks are terminal");
    }

    // -----------------------------------------------------------------------
    // #241: a stalled subtask with recovery budget remaining is REDISPATCHED
    // (reset to pending for a fresh child) instead of dead-ending the whole run.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StallRedispatch_SubtaskWithBudget_ResetToPending_NotFailed()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var stalledChildRunId = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        const string coord = "stall-redispatch-coord";
        var (planId, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, stalledChildRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        var statusById = new Dictionary<int, string> { [ids[0]] = SubtaskStatus.Running };
        var seq = new CoordinatorDispatchService.SeqCounter();

        var redispatched = await sut.TryRedispatchStalledSubtaskAsync(
            Context(coord), planId, ids[0], stalledChildRunId, statusById, seq, default);

        redispatched.Should().BeTrue("a stalled subtask with RecoveryAttempts (0) < Max must be redispatched");

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Pending,
            "the redispatched subtask is reset to pending for a fresh child — NOT failed");
        subtask.ChildRunId.Should().BeNull("the stalled child is detached so the frontier launches a fresh one");
        subtask.PriorChildRunId.Should().Be(stalledChildRunId,
            "the stalled branch is recorded on PriorChildRunId for handoff/provenance");
        subtask.RecoveryAttempts.Should().Be(1, "the recovery attempt is consumed (monotonic, never reset)");
        statusById[ids[0]].Should().Be(SubtaskStatus.Pending, "in-memory frontier state must track the reset");

        // Pod-release / no-double-observation: the OLD child run is terminalized (agent_stall_timeout)
        // so it can never be re-observed as an active child — only the subtask is revived.
        var oldChild = await _runStore.GetAsync(RunId.Parse(stalledChildRunId));
        oldChild!.Status.Should().Be(RunStatus.Failed,
            "the stalled child run stays terminally failed; only the subtask is reset");

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched,
            "a structured redispatch diagnostic must be emitted on the coordinator stream");
        var evt = coordEvents.First(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched);
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(evt.Payload)!.AsObject();
        payload["subtaskId"]!.GetValue<int>().Should().Be(ids[0]);
        payload["priorChildRunId"]!.GetValue<string>().Should().Be(stalledChildRunId);
        payload["attempt"]!.GetValue<int>().Should().Be(1);
        payload["maxAttempts"]!.GetValue<int>().Should().Be(CoordinatorSteeringService.MaxRecoveryAttempts);
        payload["reason"]!.GetValue<string>().Should().Be("stall_redispatch");
    }

    [Fact]
    public async Task StallRedispatch_EndToEnd_FreshChildReachesAssembleReady_AssemblyAllEligible()
    {
        // End-to-end through the REAL dispatch loop: subtask stalls, is redispatched (reset to
        // pending), the frontier re-dispatches it, the fresh child reaches assemble_ready, and
        // finalization sees ALL subtasks eligible → hands off to assembly.
        var stream = new SqliteRunEventStream(_streamConfig);
        var stalledChildRunId = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        const string coord = "stall-redispatch-e2e-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, stalledChildRunId)]);

        // The FRESH child the redispatch will re-attach to (idempotency guard): an active child of
        // (coord, subtask) whose stream already carries a terminal assemble_ready outcome.
        var freshChildRunId = await SeedActiveChildForSubtaskAsync(coord, ids[0]);
        await stream.AppendAsync(freshChildRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(freshChildRunId);

        _streamStore.Create(coord, "owner");

        // Short stall TTL so the idle child is detected quickly; the fresh child resolves immediately
        // from its persisted terminal (assemble_ready) event regardless of the TTL.
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.AssembleReady,
            "after redispatch the fresh child reached assemble_ready");
        subtask.RecoveryAttempts.Should().Be(1, "exactly one recovery attempt was consumed (never reset)");

        var allStatuses = await GetAllSubtaskStatusesAsync(ids);
        AssemblyPlanning.AllEligible(allStatuses).Should().BeTrue(
            "with the redispatched subtask now assemble_ready, every subtask is eligible for assembly");
        _assembly.Started.Should().Be(1, "dispatch hands off to assembly once all subtasks are terminal-eligible");
    }

    [Fact]
    public async Task StallRedispatch_ExhaustsBudget_ThenFailsExactlyOnce_NeverResets()
    {
        // Drive the FULL bounded sequence deterministically: MaxRecoveryAttempts stall-redispatches,
        // each consuming exactly one recovery attempt (monotonic, never reset), then a genuine
        // terminal. Proves the loop cannot spin forever on a chronically stalling subtask.
        var stream = new SqliteRunEventStream(_streamConfig);
        var firstChild = await SeedChildRunAsync(
            RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        const string coord = "stall-redispatch-bound-coord";
        var (planId, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, firstChild)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        var statusById = new Dictionary<int, string> { [ids[0]] = SubtaskStatus.Running };
        var seq = new CoordinatorDispatchService.SeqCounter();

        var currentChild = firstChild;
        for (var attempt = 1; attempt <= CoordinatorSteeringService.MaxRecoveryAttempts; attempt++)
        {
            var ok = await sut.TryRedispatchStalledSubtaskAsync(
                Context(coord), planId, ids[0], currentChild, statusById, seq, default);
            ok.Should().BeTrue($"attempt {attempt} is within budget and must redispatch");

            var row = await GetSubtaskAsync(ids[0]);
            row.Status.Should().Be(SubtaskStatus.Pending);
            row.ChildRunId.Should().BeNull();
            row.PriorChildRunId.Should().Be(currentChild);
            row.RecoveryAttempts.Should().Be(attempt,
                "each redispatch consumes exactly one attempt (monotonic; never reset)");

            // Simulate the redispatched child stalling AGAIN for the next round.
            currentChild = await SeedChildRunAsync(
                RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-2));
            await SetSubtaskRunningWithChildAsync(ids[0], currentChild);
            statusById[ids[0]] = SubtaskStatus.Running;
        }

        // Budget now exhausted (RecoveryAttempts == Max): a further stall must NOT redispatch.
        var exhausted = await sut.TryRedispatchStalledSubtaskAsync(
            Context(coord), planId, ids[0], currentChild, statusById, seq, default);
        exhausted.Should().BeFalse("once RecoveryAttempts >= Max the stall is a genuine terminal");
        (await GetSubtaskAsync(ids[0])).RecoveryAttempts.Should().Be(
            CoordinatorSteeringService.MaxRecoveryAttempts, "the cap holds — never reset, never exceeded");

        // The genuine terminal: the real dispatch loop dead-ends the exhausted stalled subtask.
        await sut.RunDispatchLoopAsync(Context(coord), new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        var finalRow = await GetSubtaskAsync(ids[0]);
        finalRow.Status.Should().Be(SubtaskStatus.Failed, "after the budget is exhausted the stall fails the subtask");
        finalRow.RecoveryAttempts.Should().Be(CoordinatorSteeringService.MaxRecoveryAttempts,
            "RecoveryAttempts is capped and never reset");

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Count(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched)
            .Should().Be(CoordinatorSteeringService.MaxRecoveryAttempts, "exactly Max redispatches happened — no more");
        coordEvents.Count(e => e.Type == EventTypes.SubtaskFailed && SubtaskIdOf(e) == ids[0])
            .Should().Be(1, "the subtask goes Failed exactly once");
        _assembly.Started.Should().Be(1, "the exhausted run dead-ends and hands off (assembly then blocks)");
    }

    [Fact]
    public async Task RetryableInfrastructureFailure_RedispatchesFreshChild_ThenCanSucceed()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        const string coord = "infra-retry-success-coord";
        var failedChild = await SeedChildRunAsync(RunStatus.InProgress);
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, failedChild)]);
        _streamStore.Create(coord, "owner");

        await stream.AppendAsync(failedChild, new RunEvent(0, EventTypes.RunFailed, new
        {
            reason = "shell_execution_timeout",
            message = "Shell execution exceeded its hard deadline of 30 minutes and was terminated.",
            retryable = true,
        }));
        await stream.CompleteAsync(failedChild);
        await _runStore.UpdateStatusAsync(
            RunId.Parse(failedChild), RunStatus.Failed, DateTimeOffset.UtcNow);

        var sut = BuildDispatch(stream);
        string? successfulChild = null;
        sut.StartChildRunOverride = async (run, ct) =>
        {
            successfulChild = run.Id.ToString();
            await _runStore.InsertAsync(run, ct);
            await stream.AppendAsync(successfulChild, new RunEvent(
                0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }), ct);
            await stream.CompleteAsync(successfulChild, ct);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        successfulChild.Should().NotBeNull();
        successfulChild.Should().NotBe(failedChild,
            "an infrastructure retry must call StartChildRunAsync with a genuinely fresh run id");
        var row = await GetSubtaskAsync(ids[0]);
        row.Status.Should().Be(SubtaskStatus.AssembleReady);
        row.ChildRunId.Should().Be(successfulChild);
        row.PriorChildRunId.Should().Be(failedChild);
        row.InfrastructureRetryCount.Should().Be(1);
        row.InfrastructureRetryEligibleAt.Should().NotBeNull();
        _assembly.Started.Should().Be(1);

        var retry = _streamStore.Get(coord)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched);
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(retry.Payload)!.AsObject();
        payload["attempt"]!.GetValue<int>().Should().Be(1);
        payload["maxAttempts"]!.GetValue<int>().Should()
            .Be(CoordinatorDispatchService.MaxInfrastructureRetries);
        payload["infrastructureReason"]!.GetValue<string>().Should()
            .Be("shell_execution_timeout");
    }

    [Theory]
    [InlineData(1, 30, 60)]
    [InlineData(2, 60, 120)]
    public async Task RetryableInfrastructureFailure_PersistsExponentialBackoffWithJitter(
        int attempt,
        int minimumSeconds,
        int maximumSeconds)
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var coord = $"infra-backoff-{attempt}";
        var child = await SeedChildRunAsync(RunStatus.Failed);
        var (planId, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, child)]);
        _streamStore.Create(coord, "owner");
        await SetInfrastructureRetryCountAsync(ids[0], attempt - 1);

        var sut = BuildDispatch(stream, instantRetryBackoff: false);
        var before = DateTimeOffset.UtcNow;
        var retried = await sut.TryRedispatchRetryableFailureAsync(
            Context(coord), planId, ids[0], child, "shell_execution_timeout", "deadline",
            new Dictionary<int, string> { [ids[0]] = SubtaskStatus.Running }, default);
        var after = DateTimeOffset.UtcNow;

        retried.Should().BeTrue();
        var row = await GetSubtaskAsync(ids[0]);
        row.InfrastructureRetryEligibleAt.Should().NotBeNull();
        row.InfrastructureRetryEligibleAt!.Value.Should().BeOnOrAfter(before.AddSeconds(minimumSeconds));
        row.InfrastructureRetryEligibleAt.Value.Should().BeOnOrBefore(after.AddSeconds(maximumSeconds));
    }

    [Fact]
    public void InfrastructureRetryBackoff_ConfigurationCannotExceedTwoMinutes()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:InfrastructureRetryAttempt1MinSeconds"] = "900",
                ["Coordinator:InfrastructureRetryAttempt1MaxSeconds"] = "1800",
                ["Coordinator:InfrastructureRetryAttempt2MinSeconds"] = "900",
                ["Coordinator:InfrastructureRetryAttempt2MaxSeconds"] = "1800",
            })
            .Build();
        var sut = BuildDispatch(new SqliteRunEventStream(_streamConfig), configuration: config);

        sut.CalculateInfrastructureRetryBackoff(1).Should().Be(CoordinatorDispatchService.MaxInfrastructureRetryBackoff);
        sut.CalculateInfrastructureRetryBackoff(2).Should().Be(CoordinatorDispatchService.MaxInfrastructureRetryBackoff);
    }

    [Fact]
    public async Task Shutdown_RelinquishesDispatchLeaseForReplacementWorker()
    {
        var coordinatorRunId = "shutdown-lease-coordinator";
        var (workPlanId, _) = await SeedPlanAsync(
            coordinatorRunId, [(SubtaskStatus.Pending, null)]);
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.SingleAsync(p => p.Id == workPlanId);
            plan.Status = WorkPlanStatus.Dispatching;
            plan.CoordinatorPodId = Environment.MachineName;
            await db.SaveChangesAsync();
        }

        var lifetime = new StoppableHostApplicationLifetime();
        _ = BuildDispatch(new SqliteRunEventStream(_streamConfig), lifetime: lifetime);
        lifetime.StopApplication();

        using var verifyScope = _provider.CreateScope();
        var released = await verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .WorkPlans.AsNoTracking()
            .SingleAsync(p => p.Id == workPlanId);
        released.Status.Should().Be(WorkPlanStatus.Dispatching);
        released.CoordinatorPodId.Should().BeNull(
            "the replacement worker's reconciler must be able to re-arm the interrupted dispatch");
    }

    [Fact]
    public async Task RetryableInfrastructureFailure_ReleasesOldAgentHostPodBeforeFreshDispatch()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        const string coord = "infra-retry-pod-release";
        var failedChild = await SeedChildRunAsync(RunStatus.Failed);
        var (planId, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, failedChild)]);
        _streamStore.Create(coord, "owner");
        var lifecycle = new RecordingPodLifecycle();

        var sut = BuildDispatch(stream, podLifecycle: lifecycle);
        var retried = await sut.TryRedispatchRetryableFailureAsync(
            Context(coord), planId, ids[0], failedChild, "shell_execution_timeout", "deadline",
            new Dictionary<int, string> { [ids[0]] = SubtaskStatus.Running }, default);

        retried.Should().BeTrue();
        lifecycle.Released.Should().Equal(failedChild);
    }

    [Fact]
    public async Task RetryableInfrastructureFailure_StopsAtBudget_ThenUsesExistingTerminalFailure()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        const string coord = "infra-retry-exhausted-coord";
        var firstChild = await SeedChildRunAsync(RunStatus.Failed);
        var (planId, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, firstChild)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        var statusById = new Dictionary<int, string> { [ids[0]] = SubtaskStatus.Running };

        var currentChild = firstChild;
        for (var attempt = 1; attempt <= CoordinatorDispatchService.MaxInfrastructureRetries; attempt++)
        {
            var retried = await sut.TryRedispatchRetryableFailureAsync(
                Context(coord),
                planId,
                ids[0],
                currentChild,
                "a2a_transport_failure",
                "Connection reset by peer.",
                statusById,
                default);
            retried.Should().BeTrue();

            var row = await GetSubtaskAsync(ids[0]);
            row.Status.Should().Be(SubtaskStatus.Pending);
            row.InfrastructureRetryCount.Should().Be(attempt);

            currentChild = await SeedChildRunAsync(RunStatus.InProgress);
            await SetSubtaskRunningWithChildAsync(ids[0], currentChild);
            statusById[ids[0]] = SubtaskStatus.Running;
        }

        await stream.AppendAsync(currentChild, new RunEvent(0, EventTypes.RunFailed, new
        {
            reason = "a2a_transport_failure",
            message = "Connection reset by peer.",
            retryable = true,
        }));
        await stream.CompleteAsync(currentChild);
        await _runStore.UpdateStatusAsync(
            RunId.Parse(currentChild), RunStatus.Failed, DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var final = await GetSubtaskAsync(ids[0]);
        final.Status.Should().Be(SubtaskStatus.Failed);
        final.InfrastructureRetryCount.Should().Be(
            CoordinatorDispatchService.MaxInfrastructureRetries);
        _streamStore.Get(coord)!.GetSnapshotSince(0).Events
            .Count(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched)
            .Should().Be(CoordinatorDispatchService.MaxInfrastructureRetries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UnretryableOrUnspecifiedFailure_IsNotRedispatched(bool? retryable)
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var coord = $"infra-no-retry-{retryable?.ToString() ?? "missing"}";
        var child = await SeedChildRunAsync(RunStatus.InProgress);
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, child)]);
        _streamStore.Create(coord, "owner");

        object payload = retryable.HasValue
            ? new
            {
                reason = "invalid_request",
                message = "The request is not retryable.",
                retryable = retryable.Value,
            }
            : new
            {
                reason = "invalid_request",
                message = "The request is not retryable.",
            };
        await stream.AppendAsync(child, new RunEvent(0, EventTypes.RunFailed, payload));
        await stream.CompleteAsync(child);
        await _runStore.UpdateStatusAsync(RunId.Parse(child), RunStatus.Failed, DateTimeOffset.UtcNow);

        var sut = BuildDispatch(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var row = await GetSubtaskAsync(ids[0]);
        row.Status.Should().Be(SubtaskStatus.Failed);
        row.InfrastructureRetryCount.Should().Be(0);
        _streamStore.Get(coord)!.GetSnapshotSince(0).Events
            .Should().NotContain(e => e.Type == EventTypes.CoordinatorSubtaskRedispatched);
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private static int SubtaskIdOf(RunEvent e) =>
        System.Text.Json.JsonSerializer.SerializeToNode(e.Payload)!.AsObject()["subtaskId"]!.GetValue<int>();

    private async Task SetRecoveryAttemptsAsync(int subtaskId, int attempts)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        row.RecoveryAttempts = attempts;
        await db.SaveChangesAsync();
    }

    private async Task SetInfrastructureRetryCountAsync(int subtaskId, int attempts)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        row.InfrastructureRetryCount = attempts;
        await db.SaveChangesAsync();
    }

    private async Task SetSubtaskRunningWithChildAsync(int subtaskId, string childRunId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        row.Status = SubtaskStatus.Running;
        row.ChildRunId = childRunId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<int, string>> GetAllSubtaskStatusesAsync(IEnumerable<int> ids)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var result = new Dictionary<int, string>();
        foreach (var id in ids)
            result[id] = (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == id)).Status;
        return result;
    }

    private async Task<string> SeedActiveChildForSubtaskAsync(string coordinatorRunId, int subtaskId)
    {
        var id = RunId.New();
        var run = new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "fresh child",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = coordinatorRunId,
            SubtaskId = subtaskId.ToString(),
        };
        await _runStore.InsertAsync(run);
        return id.ToString();
    }

    private CoordinatorDispatchService BuildDispatch(
        IRunEventStream eventStream,
        double stallTimeoutMinutes = 5,
        bool instantRetryBackoff = true,
        IAgentHostPodLifecycle? podLifecycle = null,
        IConfiguration? configuration = null,
        IHostApplicationLifetime? lifetime = null)
    {
        var retrySeconds = instantRetryBackoff ? "0" : null;
        var config = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:SubtaskStallTimeoutMinutes"] =
                    stallTimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Coordinator:InfrastructureRetryAttempt1MinSeconds"] = retrySeconds,
                ["Coordinator:InfrastructureRetryAttempt1MaxSeconds"] = retrySeconds,
                ["Coordinator:InfrastructureRetryAttempt2MinSeconds"] = retrySeconds,
                ["Coordinator:InfrastructureRetryAttempt2MaxSeconds"] = retrySeconds,
            })
            .Build();

        var orchestrator = new RunOrchestrator(
            _runStore, _streamStore,
            worktreeManager: null!, workflowFactory: null!, registry: null!, watchLoop: null!,
            _scopeFactory, configuration: null!, NullLogger<RunOrchestrator>.Instance);

        return new CoordinatorDispatchService(
            _runStore, _streamStore, orchestrator, null!, new CoordinatorSteeringQueue(_scopeFactory), _assembly,
            _scopeFactory, lifetime ?? new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance,
            runOptions: null, autopilot: null, configuration: config, eventStream: eventStream,
            podLifecycle: podLifecycle,
            sandboxRuntime: Options.Create(new SandboxRuntimeOptions
            {
                AgentExecutionMode = podLifecycle is null ? "in-api" : "pod-per-run",
            }));
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
            Task = "child",
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
        (string Status, string? ChildRunId)[] subtasks,
        bool declaredDependency = false,
        (int SubtaskIndex, int DependsOnIndex)[]? dependencyPairs = null)
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
                Title = $"t{ids.Count}",
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

        // Wire dependencies: ids[1] depends on ids[0] when declaredDependency is set.
        if (declaredDependency && ids.Count >= 2)
        {
            db.SubtaskDependencies.Add(new SubtaskDependency
            {
                SubtaskId = ids[1],
                DependsOnSubtaskId = ids[0],
            });
            await db.SaveChangesAsync();
        }

        // Wire explicit dependency pairs by subtask-list index.
        if (dependencyPairs is not null)
        {
            foreach (var (subtaskIndex, dependsOnIndex) in dependencyPairs)
            {
                db.SubtaskDependencies.Add(new SubtaskDependency
                {
                    SubtaskId = ids[subtaskIndex],
                    DependsOnSubtaskId = ids[dependsOnIndex],
                });
            }
            await db.SaveChangesAsync();
        }

        return (plan.Id, ids);
    }

    private async Task<Subtask> GetSubtaskAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private static void CreateRunEventsTable(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
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

    // ── git repo helpers ──────────────────────────────────────────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-lock-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);
        return repoPath;
    }

    private static void CommitOnNewBranch(
        string repositoryPath, string branchName, string filePath, string fileContent, string commitMessage)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"] ?? throw new InvalidOperationException("main not found");
        var branch = repo.Branches[branchName] ?? repo.CreateBranch(branchName, main.Tip);

        var tmpBlobPath = Path.Combine(repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent, Encoding.UTF8);
        try
        {
            var blob = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit = repo.ObjectDatabase.CreateCommit(
                sig, sig, commitMessage, newTree, new[] { branch.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
        }
        finally
        {
            if (File.Exists(tmpBlobPath)) File.Delete(tmpBlobPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();

        await Task.Delay(50);
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
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

    private sealed class StoppableHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class RecordingPodLifecycle : IAgentHostPodLifecycle
    {
        public List<string> Released { get; } = [];

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult("http://fake-agent-host");

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            Task.FromResult("http://fake-agent-host");

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            Released.Add(runId);
            return Task.CompletedTask;
        }
    }
}
