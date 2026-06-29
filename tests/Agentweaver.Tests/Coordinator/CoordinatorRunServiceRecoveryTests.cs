using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the RC-2 fix: <c>CoordinatorRunService.FailRunSafeAsync</c> must check the boolean
/// returned by <c>TrySetTerminalStatusAsync</c> and return early (without writing RunEvents) when
/// the transition is a no-op — i.e. the run was already set to a terminal status by another replica.
///
/// <para>Without the fix, the losing pod still calls <c>RecordNext</c> and fires
/// <c>PersistRunEventsAsync</c>, racing with the winning pod and causing Postgres 40001
/// serialization failures on the <c>RunEvents</c> INSERT.</para>
/// </summary>
public sealed class CoordinatorRunServiceRecoveryTests : IAsyncDisposable
{
    private readonly string _checkpointsPath;
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _memoryServiceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;

    public CoordinatorRunServiceRecoveryTests()
    {
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"coord-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointsPath);

        // Shared in-memory SQLite connection for MemoryDbContext (WorkPlans + RunEvents).
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _memoryServiceProvider = services.BuildServiceProvider();
        using (var scope = _memoryServiceProvider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _memoryServiceProvider.GetRequiredService<IServiceScopeFactory>();

        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
    }

    public async ValueTask DisposeAsync()
    {
        await _runDb.DisposeAsync();
        _memoryServiceProvider.Dispose();
        _memoryConn.Dispose();
        try { Directory.Delete(_checkpointsPath, recursive: true); } catch { }
    }

    // =========================================================================
    // Test 1: Winner pod — sets run to Failed, writes exactly one RunEvent.
    // =========================================================================
    [Fact]
    public async Task RecoverInterruptedRunsAsync_WinnerPod_WritesExactlyOneRunEvent()
    {
        // Seed a Coordinator run in InProgress with no work plan (→ ResumeSpecPhase → no checkpoint → FailRunSafeAsync).
        var runId = RunId.New();
        await _runStore.InsertAsync(new Run
        {
            Id = runId,
            AgentName = "Coordinator",
            ParentRunId = null,
            Status = RunStatus.InProgress,
            RepositoryPath = _checkpointsPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test goal",
            SubmittingUser = "test-user",
            StartedAt = DateTimeOffset.UtcNow,
            Origin = RunOrigin.Interactive,
        });

        var streamStore = new RunStreamStore();
        var svc = BuildCoordinatorRunService(_runStore, streamStore);

        await svc.RecoverInterruptedRunsAsync(CancellationToken.None);
        // PersistRunEventsAsync is fire-and-forget; give it a moment to complete.
        await Task.Delay(200);

        // Run must be Failed in the run store.
        var updated = await _runStore.GetAsync(runId);
        updated!.Status.Should().Be(RunStatus.Failed,
            "the winner pod must transition the run to Failed");

        // Exactly one RunEvent (run.failed) must have been written.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = await db.RunEvents
            .Where(e => e.RunId == runId.ToString())
            .ToListAsync();
        events.Should().HaveCount(1, "the winner pod writes exactly one run.failed event");
        events[0].EventType.Should().Be("run.failed");
    }

    // =========================================================================
    // Test 2 (RC-2 fix): Loser pod — TrySetTerminalStatusAsync no-op → must
    // NOT write any RunEvents and must NOT add events to the stream entry.
    // =========================================================================
    [Fact]
    public async Task RecoverInterruptedRunsAsync_LoserPod_DoesNotWriteRunEvents()
    {
        // Arrange: use a stub that returns the run from GetByStatusAsync(InProgress)
        // but always returns false from TrySetTerminalStatusAsync (simulating a losing CAS).
        var runId = RunId.New();
        var seedRun = new Run
        {
            Id = runId,
            AgentName = "Coordinator",
            ParentRunId = null,
            Status = RunStatus.InProgress,
            RepositoryPath = _checkpointsPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test goal",
            SubmittingUser = "test-user",
            StartedAt = DateTimeOffset.UtcNow,
            Origin = RunOrigin.Interactive,
        };

        var noOpStore = new AlwaysNoOpRunStore(seedRun);
        var streamStore = new RunStreamStore();
        var svc = BuildCoordinatorRunService(noOpStore, streamStore);

        // Act
        await svc.RecoverInterruptedRunsAsync(CancellationToken.None);
        // Allow any fire-and-forget to complete (should be none with the fix).
        await Task.Delay(200);

        // Assert: no RunEvents must be in the DB for this run.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = await db.RunEvents
            .Where(e => e.RunId == runId.ToString())
            .ToListAsync();
        events.Should().BeEmpty(
            "the losing pod must not write RunEvents when TrySetTerminalStatusAsync is a no-op");

        // Assert: the stream entry (created by RecoverSpecPhaseAsync) must contain no RunFailed event.
        var entry = streamStore.Get(runId.ToString());
        if (entry is not null)
        {
            var snapshot = entry.GetSnapshotSince(0);
            snapshot.Events.Should().NotContain(
                e => e.Type == "run.failed",
                "RecordNext(RunFailed) must not be called on the losing pod");
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private CoordinatorRunService BuildCoordinatorRunService(
        IRunStore runStore,
        RunStreamStore streamStore)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Checkpoints:Path"] = _checkpointsPath,
                ["Coordinator:AutoDispatch"] = "false",
            })
            .Build();

        var loggerFactory = NullLoggerFactory.Instance;

        var registry = new RunWorkflowRegistry();
        var pendingStore = new PendingRequestStore(_scopeFactory);
        var copilotClientFactory = new Agentweaver.AgentRuntime.Providers.GitHubCopilotClientFactory(
            config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());

        var agentFactory = new WorkflowAgentFactory(
            copilotClientFactory,
            new FixedInstallationScopeStub(),
            new Agentweaver.SandboxExec.PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            new InMemoryQuestionGate(),
            new InMemoryRunOptionsStore(),
            loggerFactory);

        var runWorkflowFactory = new RunWorkflowFactory(
            new TestFileEditAgentRunner(),
            copilotClientFactory,
            new FixedInstallationScopeStub(),
            new Agentweaver.SandboxExec.PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            new ThrowingWorktreeOps(),
            new ThrowingMergeCoordinator(),
            streamStore,
            runStore,
            loggerFactory,
            _scopeFactory,
            agentFactory,
            config);

        var coordWorkflowFactory = new CoordinatorWorkflowFactory(
            agentFactory,
            new ThrowingSpecDrafter(),
            streamStore,
            _scopeFactory,
            loggerFactory,
            config);

        return new CoordinatorRunService(
            runStore: runStore,
            streamStore: streamStore,
            registry: registry,
            pendingStore: pendingStore,
            factory: coordWorkflowFactory,
            runWorkflowFactory: runWorkflowFactory,
            dispatchService: null!,   // not invoked in ResumeSpecPhase → FailRunSafeAsync path
            assemblyStore: null!,     // not invoked in this path
            assembly: null!,          // not invoked in this path
            scopeFactory: _scopeFactory,
            runOptions: null!,        // not invoked in this path
            backlogStore: null!,      // not invoked (run.Origin == Interactive)
            lifetime: new TestHostApplicationLifetime(),
            configuration: config,
            logger: NullLogger<CoordinatorRunService>.Instance);
    }

    // -------------------------------------------------------------------------
    // Stub: always returns the seeded run from GetByStatusAsync(InProgress),
    // but TrySetTerminalStatusAsync always returns false (loser pod simulation).
    // -------------------------------------------------------------------------
    private sealed class AlwaysNoOpRunStore(Run seedRun) : IRunStore
    {
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default)
        {
            if (status == RunStatus.InProgress)
                return Task.FromResult<IReadOnlyList<Run>>(new[] { seedRun });
            return Task.FromResult<IReadOnlyList<Run>>(Array.Empty<Run>());
        }

        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
            => Task.FromResult(false); // always no-op — simulates the losing pod

        // The remaining IRunStore members are not called in the path under test.
        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => Task.FromResult<Run?>(null);
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
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingWorktreeOps : IWorktreeOperations
    {
        public bool WorktreeExists(string worktreePath) => throw new NotImplementedException("Not called in FailRunSafeAsync path");
        public string? GetTreeHash(string worktreePath) => throw new NotImplementedException();
        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException();
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException();
        public int GetStepCount(string runId) => throw new NotImplementedException();
        public MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash) => throw new NotImplementedException();
        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch) => throw new NotImplementedException();
    }

    private sealed class ThrowingMergeCoordinator : IMergeCoordinator
    {
        public Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct) => throw new NotImplementedException();
        public Task RevertMergeAsync(string runId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> FailMergeAsync(string runId, string mergeResult, string? mergeConflictsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<MergeExecutionResult> ExecuteMergeAsync(MergeInput input, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class ThrowingSpecDrafter : ICoordinatorSpecDrafter
    {
        public Task<OutcomeSpecDraft> DraftAsync(CoordinatorDraftInput input, string charter, string? memoryContext, CancellationToken ct)
            => throw new NotImplementedException("DraftAsync is not called in the FailRunSafeAsync path");
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
