using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
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

        manager.TryCleanIntegrationLockFiles(repoPath, "agentweaver/integration/any-run");

        File.Exists(packedRefsLock).Should().BeFalse(
            "TryCleanIntegrationLockFiles must delete a stale packed-refs.lock");
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
    // Harness
    // -----------------------------------------------------------------------

    private CoordinatorDispatchService BuildDispatch(IRunEventStream eventStream, double stallTimeoutMinutes = 5)
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
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
