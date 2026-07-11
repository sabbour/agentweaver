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
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Coordinator restart-recovery regression tests, including final-Scribe bounded admission/dedup and
/// the RC-2 <c>FailRunSafeAsync</c> losing-replica behavior.
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

    [Fact]
    public async Task RecoverInterruptedRunsAsync_TwentyTerminalRuns_BoundsConcurrentFinalScribes()
    {
        var coordinatorRuns = new List<Run>();
        for (var i = 0; i < 20; i++)
            coordinatorRuns.Add(await SeedTerminalCoordinatorRunAsync());

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Coordinator:FinalScribeMaxConcurrency"] = "2",
        });
        var streamStore = new RunStreamStore();
        var pipeline = new CountingScribePipeline(block: true);
        var assembly = BuildAssembly(_runStore, streamStore, pipeline, config);
        var svc = BuildCoordinatorRunService(_runStore, streamStore, assembly, config);

        await svc.RecoverInterruptedRunsAsync(CancellationToken.None);
        await WaitUntilAsync(() => pipeline.InvocationCount == 2);
        await Task.Delay(100);

        pipeline.InvocationCount.Should().Be(2,
            "blocked Scribe executions beyond the configured limit must wait for admission");
        pipeline.MaxObservedConcurrency.Should().Be(2);

        pipeline.Release();
        await WaitUntilAsync(() => pipeline.InvocationCount == coordinatorRuns.Count);
        await WaitUntilAsync(async () =>
        {
            foreach (var run in coordinatorRuns)
            {
                var children = await _runStore.GetRunsByParentAsync(run.Id.ToString());
                if (!children.Any(IsCompletedScribe))
                    return false;
            }

            return true;
        });

        pipeline.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_CompletedScribe_DoesNotReenqueue()
    {
        var coordinatorRun = await SeedTerminalCoordinatorRunAsync();
        await SeedScribeAttemptAsync(coordinatorRun, RunStatus.Completed);

        var config = BuildConfiguration();
        var streamStore = new RunStreamStore();
        var pipeline = new CountingScribePipeline();
        var assembly = BuildAssembly(_runStore, streamStore, pipeline, config);
        var svc = BuildCoordinatorRunService(_runStore, streamStore, assembly, config);

        await svc.RecoverInterruptedRunsAsync(CancellationToken.None);
        await Task.Delay(100);

        pipeline.InvocationCount.Should().Be(0);
        (await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString()))
            .Where(IsScribe)
            .Should()
            .ContainSingle()
            .Which.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_ThreeFailedScribes_DoesNotReenqueue()
    {
        var coordinatorRun = await SeedTerminalCoordinatorRunAsync();
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Coordinator:FinalScribeMaxAttempts"] = "3",
        });
        var streamStore = new RunStreamStore();
        var pipeline = new CountingScribePipeline(failScribes: true);
        var assembly = BuildAssembly(_runStore, streamStore, pipeline, config);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var expectedAttempt = attempt;
            while (pipeline.InvocationCount < expectedAttempt)
            {
                assembly.EnsureFinalScribe(coordinatorRun);
                await Task.Delay(20);
            }
            await WaitUntilAsync(async () =>
            {
                var children = await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString());
                return children.Count(r => IsScribe(r) && r.Status == RunStatus.Failed) == expectedAttempt;
            });
        }

        var svc = BuildCoordinatorRunService(_runStore, streamStore, assembly, config);
        await svc.RecoverInterruptedRunsAsync(CancellationToken.None);
        await Task.Delay(100);

        pipeline.InvocationCount.Should().Be(3);
        (await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString()))
            .Count(r => IsScribe(r) && r.Status == RunStatus.Failed)
            .Should().Be(3);
    }

    [Fact]
    public async Task EnsureFinalScribe_ConcurrentCallsForSameRun_ExecutesPipelineOnce()
    {
        var coordinatorRun = await SeedTerminalCoordinatorRunAsync();
        var config = BuildConfiguration();
        var streamStore = new RunStreamStore();
        var pipeline = new CountingScribePipeline(block: true);
        var assembly = BuildAssembly(_runStore, streamStore, pipeline, config);

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => assembly.EnsureFinalScribe(coordinatorRun))));
        await WaitUntilAsync(() => pipeline.InvocationCount == 1);
        await Task.Delay(100);

        pipeline.InvocationCount.Should().Be(1);

        pipeline.Release();
        await WaitUntilAsync(async () =>
        {
            var children = await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString());
            return children.Count(IsCompletedScribe) == 1;
        });

        pipeline.InvocationCount.Should().Be(1);
        (await _runStore.GetRunsByParentAsync(coordinatorRun.Id.ToString()))
            .Where(IsScribe)
            .Should()
            .ContainSingle();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private CoordinatorRunService BuildCoordinatorRunService(
        IRunStore runStore,
        RunStreamStore streamStore,
        ICoordinatorAssembly? assembly = null,
        IConfiguration? configuration = null)
    {
        var config = configuration ?? BuildConfiguration();

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
            assembly: assembly ?? new NoOpAssembly(),
            scopeFactory: _scopeFactory,
            runOptions: null!,        // not invoked in this path
            backlogStore: null!,      // not invoked (run.Origin == Interactive)
            lifetime: new TestHostApplicationLifetime(),
            configuration: config,
            logger: NullLogger<CoordinatorRunService>.Instance);
    }

    private IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Checkpoints:Path"] = _checkpointsPath,
            ["Coordinator:AutoDispatch"] = "false",
        };
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                values[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private CoordinatorAssemblyService BuildAssembly(
        IRunStore runStore,
        RunStreamStore streamStore,
        ICollectiveAssemblyPipeline pipeline,
        IConfiguration configuration) =>
        new(
            runStore,
            streamStore,
            assemblyStore: null!,
            reviewGate: null!,
            pipeline,
            _scopeFactory,
            _memoryServiceProvider,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorAssemblyService>.Instance,
            configuration);

    private async Task<Run> SeedTerminalCoordinatorRunAsync()
    {
        var run = new Run
        {
            Id = RunId.New(),
            AgentName = "Coordinator",
            ParentRunId = null,
            Status = RunStatus.Completed,
            RepositoryPath = _checkpointsPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test goal",
            SubmittingUser = "test-user",
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Result = "complete",
            Origin = RunOrigin.Interactive,
        };
        await _runStore.InsertAsync(run);
        return run;
    }

    private Task SeedScribeAttemptAsync(Run coordinatorRun, RunStatus status) =>
        _runStore.InsertAsync(new Run
        {
            Id = RunId.New(),
            AgentName = "Scribe",
            ParentRunId = coordinatorRun.Id.ToString(),
            SubtaskId = CoordinatorAssemblyService.AssemblyScribeSubtaskId,
            Status = status,
            RepositoryPath = coordinatorRun.RepositoryPath,
            OriginatingBranch = coordinatorRun.OriginatingBranch,
            ModelSource = coordinatorRun.ModelSource,
            Task = "final scribe",
            SubmittingUser = coordinatorRun.SubmittingUser,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = status == RunStatus.InProgress ? null : DateTimeOffset.UtcNow,
            Result = status == RunStatus.Failed ? "simulated failure" : "complete",
            Origin = RunOrigin.Interactive,
        });

    private static bool IsScribe(Run run) =>
        string.Equals(run.AgentName, "Scribe", StringComparison.Ordinal)
        && string.Equals(
            run.SubtaskId,
            CoordinatorAssemblyService.AssemblyScribeSubtaskId,
            StringComparison.Ordinal);

    private static bool IsCompletedScribe(Run run) =>
        IsScribe(run) && run.Status == RunStatus.Completed;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met within 10 seconds.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Condition was not met within 10 seconds.");
            await Task.Delay(20);
        }
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

    private sealed class NoOpAssembly : ICoordinatorAssembly
    {
        public void StartAssembly(CoordinatorDispatchContext context) { }
        public void EnsureFinalScribe(Run coordinatorRun) { }
        public bool IsAssemblyActive(string coordinatorRunId) => false;
        public void AbandonStaleReview(CoordinatorDispatchContext context) { }
        public void FailAssembly(CoordinatorDispatchContext context, string reason) { }
    }

    private sealed class CountingScribePipeline(
        bool block = false,
        bool failScribes = false) : ICollectiveAssemblyPipeline
    {
        private readonly TaskCompletionSource<bool> _release = CreateRelease(block);
        private int _invocationCount;
        private int _currentConcurrency;
        private int _maxObservedConcurrency;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public void Release() => _release.TrySetResult(true);

        public async Task RunScribeAsync(CollectiveScribeRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _invocationCount);
            var current = Interlocked.Increment(ref _currentConcurrency);
            UpdateMaxConcurrency(current);
            try
            {
                await _release.Task.WaitAsync(ct);
                if (failScribes)
                    throw new InvalidOperationException("simulated Scribe failure");
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrency);
            }
        }

        private void UpdateMaxConcurrency(int current)
        {
            var observed = Volatile.Read(ref _maxObservedConcurrency);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maxObservedConcurrency,
                    current,
                    observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private static TaskCompletionSource<bool> CreateRelease(bool block)
        {
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!block)
                release.SetResult(true);
            return release;
        }

        public IntegrationBranchResult BuildIntegrationBranch(CollectiveIntegrationRequest request) =>
            throw new NotImplementedException();

        public void PrepareIntegrationBranchRetry(CollectiveIntegrationRequest request) =>
            throw new NotImplementedException();

        public Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CollectiveGateDecision> RunRubberduckAsync(
            CollectiveRubberduckRequest request,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<CollectiveGateDecision> RunBuildTestAsync(
            CollectiveBuildTestRequest request,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task CleanupBuildTestResourcesAsync(
            string coordinatorRunId,
            string repositoryPath,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public string GetBuildTestWorktreePath(string coordinatorRunId) =>
            throw new NotImplementedException();

        public string PrepareReviewerWorktree(string coordinatorRunId, string repositoryPath, string integrationBranch) =>
            throw new NotImplementedException();

        public Task<CollectiveMergeResult> MergeAsync(
            CollectiveMergeRequest request,
            CancellationToken ct) =>
            throw new NotImplementedException();
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
