using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Unit tests for WorkflowRestartService.RecoverAsync, focusing on the no-checkpoint
/// AwaitingReview recovery path (B1 fix: synthetic review.requested emission).
/// </summary>
public sealed class WorkflowRestartServiceTests : IAsyncDisposable
{
    private readonly TestSqliteDb _db;
    private readonly string _checkpointsPath;
    private readonly string _worktreePath;

    public WorkflowRestartServiceTests()
    {
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"restart-test-cp-{Guid.NewGuid():N}");
        _worktreePath = Path.Combine(Path.GetTempPath(), $"restart-test-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_checkpointsPath);
        Directory.CreateDirectory(_worktreePath);
        _db = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_checkpointsPath, recursive: true); } catch { }
        try { Directory.Delete(_worktreePath, recursive: true); } catch { }
    }

    // =========================================================================
    // Test 1 (B1): AwaitingReview run with no checkpoint + valid worktree
    // -> stream entry receives a synthetic review.requested event.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_ValidWorktree_EmitsSyntheticReviewRequested()
    {
        // Arrange — use realistic non-null merge data so all direct-review prerequisites pass.
        const string treeHash = "abc123def456abc123def456abc123def456abc1";
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        var worktreeOps = new TestWorktreeOps(worktreeExists: true, worktreePath: _worktreePath, treeHash: treeHash);

        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = _worktreePath,
            WorktreeBranch = "run/test-branch",
            TreeHash = treeHash,
        };
        await runStore.InsertAsync(run);
        // InsertAsync doesn't write TreeHash — set it explicitly so the DB has it.
        await runStore.UpdateReviewReadyAsync(runId, treeHash, "", 0);

        var service = BuildService(runStore, streamStore, worktreeOps);

        // Act
        await service.RecoverAsync(CancellationToken.None);

        // Assert: stream entry must have a review.requested event
        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull("stream entry must be created for the recovered run");

        var snapshot = entry!.GetSnapshotSince(0);
        snapshot.Events.Should().ContainSingle(e => e.Type == EventTypes.ReviewRequested,
            "synthetic review.requested must be emitted so SSE clients unblock");
        snapshot.Events.Should().NotContain(e => e.Type == EventTypes.RunFailed,
            "a valid worktree must not fail the run");

        entry.IsAwaitingReview.Should().BeTrue("the entry must remain in AwaitingReview state");
        entry.IsCompleted.Should().BeFalse("the stream must stay open for the review decision");
    }

    // =========================================================================
    // Test 2 (B1): AwaitingReview run with no checkpoint + MISSING worktree
    // -> run is failed, stream entry is completed.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_MissingWorktree_FailsRun()
    {
        // Arrange
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        // worktreeExists=false simulates a missing worktree (e.g. disk was wiped)
        var worktreeOps = new TestWorktreeOps(worktreeExists: false, worktreePath: _worktreePath, treeHash: null);

        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = _worktreePath,
            TreeHash = null,
        };
        await runStore.InsertAsync(run);

        var service = BuildService(runStore, streamStore, worktreeOps);

        // Act
        await service.RecoverAsync(CancellationToken.None);

        // Assert: run must be failed in the DB
        var updated = await runStore.GetAsync(runId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(RunStatus.Failed,
            "a missing worktree must fail the run rather than leaving it stuck");

        // Stream entry must be completed (closed)
        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull();
        entry!.IsCompleted.Should().BeTrue("the stream must be closed when the run fails");
    }

    // =========================================================================
    // Test 3 (B1 negative): AwaitingReview run with no checkpoint + missing
    // merge data (TreeHash null) -> run is Failed, no review.requested emitted.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_MissingTreeHash_FailsRun()
    {
        // Arrange — worktree exists but TreeHash is null: cannot satisfy direct-review prerequisites.
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        var worktreeOps = new TestWorktreeOps(worktreeExists: true, worktreePath: _worktreePath, treeHash: null);

        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = _worktreePath,
            WorktreeBranch = "run/test-branch",
            TreeHash = null,    // missing — direct-review would 500 if we emitted review.requested
        };
        await runStore.InsertAsync(run);

        var service = BuildService(runStore, streamStore, worktreeOps);

        // Act
        await service.RecoverAsync(CancellationToken.None);

        // Assert: run must be failed, not left in AwaitingReview
        var updated = await runStore.GetAsync(runId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(RunStatus.Failed,
            "a run with null TreeHash must be failed — approve would 500 otherwise");

        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull();
        entry!.IsCompleted.Should().BeTrue("stream must be closed when the run fails");

        var snapshot = entry.GetSnapshotSince(0);
        snapshot.Events.Should().NotContain(e => e.Type == EventTypes.ReviewRequested,
            "review.requested must NOT be emitted when the run cannot be approved");
    }

    // =========================================================================
    // Test 4 (B1 negative): AwaitingReview run with no checkpoint + tree-hash
    // mismatch -> run is Failed, no review.requested emitted.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_TreeHashMismatch_FailsRun()
    {
        // Arrange — worktree exists and returns a DIFFERENT hash than stored.
        const string storedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        const string actualHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2";
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        var worktreeOps = new TestWorktreeOps(worktreeExists: true, worktreePath: _worktreePath, treeHash: actualHash);

        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = _worktreePath,
            WorktreeBranch = "run/test-branch",
            TreeHash = storedHash,
        };
        await runStore.InsertAsync(run);
        // Set the stored tree hash in the DB so the mismatch is detected correctly.
        await runStore.UpdateReviewReadyAsync(runId, storedHash, "", 0);

        var service = BuildService(runStore, streamStore, worktreeOps);

        // Act
        await service.RecoverAsync(CancellationToken.None);

        // Assert: tampered worktree must fail the run
        var updated = await runStore.GetAsync(runId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(RunStatus.Failed,
            "tree-hash mismatch indicates a tampered worktree; run must be failed");

        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull();
        entry!.IsCompleted.Should().BeTrue();

        var snapshot = entry.GetSnapshotSince(0);
        snapshot.Events.Should().NotContain(e => e.Type == EventTypes.ReviewRequested,
            "review.requested must NOT be emitted when tree-hash validation fails");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private WorkflowRestartService BuildService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        TestWorktreeOps worktreeOps)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Checkpoints:Path"] = _checkpointsPath,
            })
            .Build();

        var registry = new RunWorkflowRegistry();
        var pendingStore = new PendingRequestStore();
        var copilotClientFactory = new Scaffolder.AgentRuntime.Providers.GitHubCopilotClientFactory(
            config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        var factory = new RunWorkflowFactory(
            new TestFileEditAgentRunner(),
            copilotClientFactory,
            new FixedInstallationScopeStub(),
            new Scaffolder.SandboxExec.PassthroughExecutor("test"),
            new StubPolicyStore(),
            new Scaffolder.AgentRuntime.InMemoryShellApprovalStore(),
            new Scaffolder.AgentRuntime.InMemoryToolApprovalGate(),
            worktreeOps,
            new ThrowingMergeCoordinator(),
            streamStore,
            loggerFactory,
            config);

        var watchLoop = new RunWatchLoopService(
            runStore,
            streamStore,
            registry,
            pendingStore,
            factory,
            worktreeOps,
            new TestHostApplicationLifetime(),
            loggerFactory.CreateLogger<RunWatchLoopService>());

        return new WorkflowRestartService(
            runStore,
            streamStore,
            registry,
            pendingStore,
            factory,
            worktreeOps,
            watchLoop,
            loggerFactory.CreateLogger<WorkflowRestartService>());
    }

    // -------------------------------------------------------------------------
    // Test-only IWorktreeOperations: WorktreeExists and GetTreeHash are
    // controlled by the test; all other methods throw NotImplementedException.
    // -------------------------------------------------------------------------
    private sealed class TestWorktreeOps : IWorktreeOperations
    {
        private readonly bool _worktreeExists;
        private readonly string _worktreePath;
        private readonly string? _treeHash;

        public TestWorktreeOps(bool worktreeExists, string worktreePath, string? treeHash)
        {
            _worktreeExists = worktreeExists;
            _worktreePath = worktreePath;
            _treeHash = treeHash;
        }

        public bool WorktreeExists(string worktreePath) => _worktreeExists;
        public string? GetTreeHash(string worktreePath) => _treeHash;
        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException("Not called in restart tests");
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException("Not called in restart tests");
        public int GetStepCount(string runId) => throw new NotImplementedException("Not called in restart tests");
        public MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash) => throw new NotImplementedException("Not called in restart tests");
        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch) => throw new NotImplementedException("Not called in restart tests");
    }

    // -------------------------------------------------------------------------
    // Test-only IMergeCoordinator: all methods throw NotImplementedException
    // (none are invoked in the no-checkpoint recovery path under test).
    // -------------------------------------------------------------------------
    private sealed class ThrowingMergeCoordinator : IMergeCoordinator
    {
        public Task<MergeLockResult> AcquireMergeLockAsync(string runId, string repositoryPath, CancellationToken ct) =>
            throw new NotImplementedException("Not called in restart tests");
        public Task<bool> CompleteMergeAsync(string runId, string mergeResult, CancellationToken ct) =>
            throw new NotImplementedException("Not called in restart tests");
        public Task RevertMergeAsync(string runId, CancellationToken ct) =>
            throw new NotImplementedException("Not called in restart tests");
        public Task<bool> FailMergeAsync(string runId, string mergeResult, string? mergeConflictsJson, CancellationToken ct) =>
            throw new NotImplementedException("Not called in restart tests");
        public Task<MergeExecutionResult> ExecuteMergeAsync(MergeInput input, CancellationToken ct) =>
            throw new NotImplementedException("Not called in restart tests");
    }

    // -------------------------------------------------------------------------
    // Minimal IHostApplicationLifetime to satisfy RunWatchLoopService
    // constructor without a full host environment.
    // -------------------------------------------------------------------------
    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // -------------------------------------------------------------------------
    // No-op IServiceScopeFactory for tests that don't exercise PostRunScribeService.
    // -------------------------------------------------------------------------
    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotImplementedException("Not called in restart tests");
    }
}
