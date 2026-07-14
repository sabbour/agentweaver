using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

using WorkflowMergeResult = Agentweaver.AgentRuntime.Workflow.MergeResult;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Unit tests for WorkflowRestartService.RecoverAsync, focusing on the no-checkpoint
/// AwaitingReview recovery path (B1 fix: synthetic review.requested emission).
/// </summary>
public sealed class WorkflowRestartServiceTests : IAsyncDisposable
{
    private readonly TestSqliteDb _db;
    private readonly string _checkpointsPath;
    private readonly string _worktreePath;
    private readonly List<string> _tempDirs = new();
    private SqliteConnection? _memoryConn;
    private ServiceProvider? _memoryServiceProvider;

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
        _memoryServiceProvider?.Dispose();
        _memoryConn?.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
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
    // Test 2b (#246 P0-A): AwaitingReview run with no checkpoint + MISSING worktree
    // DIRECTORY, but the durable run branch still exists and can be reconstructed
    // (WorktreeManager.EnsureWorktree) with a tree hash matching Run.TreeHash ->
    // the run must be RECOVERED (synthetic review.requested), not failed. This is
    // the exact "reattach recoverable worktree" contract from GH #246's P0-A design
    // and its own test plan item #2 ("same without a checkpoint: recreate and
    // restore synthetic review gate").
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_MissingWorktree_ReattachSucceeds_RecoversInsteadOfFailing()
    {
        const string treeHash = "abc123def456abc123def456abc123def456abc1";
        var reattachedPath = Path.Combine(Path.GetTempPath(), $"restart-test-reattached-{Guid.NewGuid():N}");
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        // Original path reports missing; reattach reconstructs at a (possibly different) path whose
        // tree hash matches the run's persisted TreeHash — this is the durably-recoverable case.
        var worktreeOps = new TestWorktreeOps(
            worktreeExists: false,
            worktreePath: _worktreePath,
            treeHash: null,
            reattachResult: (reattachedPath, "agentweaver/reattached-branch"),
            reattachTreeHash: treeHash);

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
            WorktreeBranch = "agentweaver/original-branch",
            TreeHash = treeHash,
        };
        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, "", 0);

        var service = BuildService(runStore, streamStore, worktreeOps);

        // Act
        await service.RecoverAsync(CancellationToken.None);

        // Assert: reattach was actually attempted (not skipped) ...
        worktreeOps.ReattachAttempted.Should().BeTrue(
            "a missing worktree directory must trigger a reattach attempt before failing (#246 P0-A)");

        // ... and the run recovered rather than being terminalized.
        var updated = await runStore.GetAsync(runId);
        updated!.Status.Should().Be(RunStatus.AwaitingReview,
            "a reconstructable worktree (durable branch + matching tree hash) must be recovered, not failed");
        // Note: IRunStore.UpdateWorktreeAsync only writes when the persisted WorktreePath is NULL
        // (it's designed for first-time provisioning, e.g. the coordinator shared-orchestration
        // worktree). Since this run already had a non-null WorktreePath before recovery, the DB
        // correction is intentionally a no-op here — the reattached path is used for THIS recovery
        // pass only. What must hold is that recovery used the reattached path/branch (proven by the
        // synthetic review.requested below, since GetTreeHash/WorktreeExists only match on that path
        // in this fake) rather than persistence of a corrected row, which is out of scope for P0-A.

        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull();
        entry!.GetSnapshotSince(0).Events.Should().ContainSingle(e => e.Type == EventTypes.ReviewRequested,
            "a successfully reattached worktree must still emit the synthetic review.requested event");
        entry.IsCompleted.Should().BeFalse("a recovered run's stream must stay open for the review decision");
    }

    // =========================================================================
    // Test 2c (#246 P0-A negative): AwaitingReview run with no checkpoint + MISSING
    // worktree directory, AND the durable run branch is also gone (or reattach
    // otherwise fails) -> reattach is attempted but the run still fails exactly as
    // before. Proves the new reattach step never masks a genuinely unrecoverable run.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_NoCheckpoint_MissingWorktree_ReattachUnavailable_StillFailsRun()
    {
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        // reattachResult: null simulates WorktreeManager finding no durable branch to reconstruct
        // from (e.g. agentweaver/<runId> branch itself was deleted) — genuinely unrecoverable.
        var worktreeOps = new TestWorktreeOps(worktreeExists: false, worktreePath: _worktreePath, treeHash: null, reattachResult: null);

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

        // Assert: reattach was tried ...
        worktreeOps.ReattachAttempted.Should().BeTrue();

        // ... but since it couldn't reconstruct anything, the run must still fail exactly as before.
        var updated = await runStore.GetAsync(runId);
        updated!.Status.Should().Be(RunStatus.Failed,
            "when reattach cannot reconstruct the worktree, the run must still fail (no false recovery)");

        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull();
        entry!.IsCompleted.Should().BeTrue("the stream must be closed when the run fails");
    }

    // =========================================================================
    // Test 2d (#246 regression): an invalid repository path must not let the
    // adapter throw out of TryReattachWorktree and abort recovery of later runs.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_InvalidRepositoryDuringReattach_DoesNotAbortSweep()
    {
        string treeHash;
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();

        var invalidRepoPath = MakeTempDir("invalid-repo");
        var invalidRunId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = invalidRunId,
            RepositoryPath = invalidRepoPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "invalid repo recovery",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = Path.Combine(invalidRepoPath, "missing-worktree"),
            WorktreeBranch = WorktreeManager.BranchNameFor(invalidRunId),
            TreeHash = null,
        });

        var (repoPath, _, manager, worktreeOps) = CreateGitRecoveryEnvironment();
        var recoverableRunId = RunId.New();
        var durableWorktree = manager.AddWorktree(repoPath, "main", recoverableRunId);
        using (var durableRepo = new Repository(durableWorktree.WorktreePath))
            treeHash = durableRepo.Head.Tip!.Tree.Sha;
        Directory.Delete(durableWorktree.WorktreePath, recursive: true);

        await runStore.InsertAsync(new Run
        {
            Id = recoverableRunId,
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "recoverable repo recovery",
            SubmittingUser = "test-user",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = durableWorktree.WorktreePath,
            WorktreeBranch = durableWorktree.BranchName,
            TreeHash = treeHash,
        });
        await runStore.UpdateReviewReadyAsync(recoverableRunId, treeHash, "", 0);

        var service = BuildService(runStore, streamStore, worktreeOps);

        await service.RecoverAsync(CancellationToken.None);

        var invalidUpdated = await runStore.GetAsync(invalidRunId);
        invalidUpdated!.Status.Should().Be(RunStatus.Failed,
            "an invalid repository should fail only that run via the normal terminal path");

        var recoveredUpdated = await runStore.GetAsync(recoverableRunId);
        recoveredUpdated!.Status.Should().Be(RunStatus.AwaitingReview,
            "a later recoverable run must still be processed after the invalid repository run");

        var recoveredEntry = streamStore.Get(recoverableRunId.ToString());
        recoveredEntry.Should().NotBeNull();
        recoveredEntry!.GetSnapshotSince(0).Events.Should().ContainSingle(e => e.Type == EventTypes.ReviewRequested,
            "the sweep must continue far enough to emit the recovered review gate");
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
    // Feature 008: a stranded InProgress COORDINATOR run is NOT failed by the generic restart sweep;
    // it is deferred (left InProgress) to CoordinatorRunService.RecoverInterruptedRunsAsync, which
    // re-arms the dispatch / collective-assembly engine from the persisted work plan.
    // =========================================================================
    [Fact]
    public async Task RecoverAsync_StrandedCoordinatorRun_LeftInProgressForCoordinatorRecovery()
    {
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        var worktreeOps = new TestWorktreeOps(worktreeExists: true, worktreePath: _worktreePath, treeHash: null);

        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "goal",
            SubmittingUser = "test-user",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
            ParentRunId = null,
        });

        var service = BuildService(runStore, streamStore, worktreeOps);
        await service.RecoverAsync(CancellationToken.None);

        var updated = await runStore.GetAsync(runId);
        updated!.Status.Should().Be(RunStatus.InProgress,
            "the generic sweep must defer coordinator runs to coordinator restart recovery, not fail them");
    }

    [Fact]
    public async Task RecoverAsync_StrandedChildRun_EmitsRetryableTransportFailure()
    {
        var runStore = new SqliteRunStore(_db.Db);
        var streamStore = new RunStreamStore();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = _worktreePath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "implement",
            SubmittingUser = "test-user",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "42",
        });

        await BuildService(
                runStore,
                streamStore,
                new TestWorktreeOps(worktreeExists: true, worktreePath: _worktreePath, treeHash: null))
            .RecoverAsync(CancellationToken.None);

        (await runStore.GetAsync(runId))!.Status.Should().Be(RunStatus.Failed);
        var failure = streamStore.Get(runId.ToString())!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.RunFailed);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(failure.Payload);
        payload.GetProperty("reason").GetString().Should().Be("a2a_transport_interrupted");
        payload.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private WorkflowRestartService BuildService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        IWorktreeOperations worktreeOps)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Checkpoints:Path"] = _checkpointsPath,
            })
            .Build();

        // Set up an in-memory SQLite-backed MemoryDbContext for the scope factory.
        _memoryServiceProvider?.Dispose();
        _memoryConn?.Dispose();
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        var memServices = new ServiceCollection();
        memServices.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _memoryServiceProvider = memServices.BuildServiceProvider();
        using (var scope = _memoryServiceProvider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        var scopeFactory = _memoryServiceProvider.GetRequiredService<IServiceScopeFactory>();

        var registry = new RunWorkflowRegistry();
        var pendingStore = new PendingRequestStore(scopeFactory);
        var copilotClientFactory = new Agentweaver.AgentRuntime.Providers.GitHubCopilotClientFactory(
            config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        var agentFactory = new Agentweaver.AgentRuntime.Workflow.WorkflowAgentFactory(
            copilotClientFactory,
            new FixedInstallationScopeStub(),
            new Agentweaver.SandboxExec.PassthroughExecutor("test"),
            new StubPolicyStore(),
            new Agentweaver.AgentRuntime.InMemoryShellApprovalStore(),
            new Agentweaver.AgentRuntime.InMemoryToolApprovalGate(),
            new Agentweaver.AgentRuntime.InMemoryQuestionGate(),
            new Agentweaver.AgentRuntime.InMemoryRunOptionsStore(),
            loggerFactory);
        var factory = new RunWorkflowFactory(
            new TestFileEditAgentRunner(),
            copilotClientFactory,
            new FixedInstallationScopeStub(),
            new Agentweaver.SandboxExec.PassthroughExecutor("test"),
            new StubPolicyStore(),
            new Agentweaver.AgentRuntime.InMemoryShellApprovalStore(),
            new Agentweaver.AgentRuntime.InMemoryToolApprovalGate(),
            worktreeOps,
            new ThrowingMergeCoordinator(),
            streamStore,
            runStore,
            loggerFactory,
            scopeFactory,
            agentFactory,
            config);

        var watchLoop = new RunWatchLoopService(
            runStore,
            streamStore,
            registry,
            pendingStore,
            factory,
            worktreeOps,
            new TestHostApplicationLifetime(),
            config,
            scopeFactory,
            new NoOpRunLeaseStore(),
            loggerFactory.CreateLogger<RunWatchLoopService>());

        return new WorkflowRestartService(
            runStore,
            streamStore,
            registry,
            pendingStore,
            factory,
            worktreeOps,
            watchLoop,
            scopeFactory,
            loggerFactory.CreateLogger<WorkflowRestartService>());
    }

    private (string RepositoryPath, string BasePath, WorktreeManager Manager, WorktreeOperationsAdapter Adapter)
        CreateGitRecoveryEnvironment()
    {
        var repoPath = MakeTempDir("repo");
        var basePath = MakeTempDir("worktrees");

        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial");
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", sig, sig);

            if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
                repo.Branches.Rename(repo.Head, "main");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = basePath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                ["Checkpoints:Path"] = _checkpointsPath,
            })
            .Build();

        var manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var adapter = new WorktreeOperationsAdapter(
            manager,
            new RunStreamStore(),
            NullLogger<WorktreeOperationsAdapter>.Instance);
        return (repoPath, basePath, manager, adapter);
    }

    private string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-restart-test-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
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
        private readonly (string WorktreePath, string BranchName)? _reattachResult;
        private readonly string? _reattachTreeHash;

        /// <summary>True once <see cref="TryReattachWorktree"/> has been invoked — lets tests assert
        /// that a missing-worktree recovery attempt actually went through the reattach path (#246
        /// P0-A) rather than failing immediately without trying.</summary>
        public bool ReattachAttempted { get; private set; }

        public TestWorktreeOps(
            bool worktreeExists,
            string worktreePath,
            string? treeHash,
            (string WorktreePath, string BranchName)? reattachResult = null,
            string? reattachTreeHash = null)
        {
            _worktreeExists = worktreeExists;
            _worktreePath = worktreePath;
            _treeHash = treeHash;
            _reattachResult = reattachResult;
            _reattachTreeHash = reattachTreeHash;
        }

        public bool WorktreeExists(string worktreePath)
        {
            // Simulates the directory now existing at the reattached location, as it would after a
            // real WorktreeManager.EnsureWorktree() recreated it from the durable branch.
            if (_reattachResult is { } reattached && string.Equals(worktreePath, reattached.WorktreePath, StringComparison.Ordinal))
                return true;
            return _worktreeExists;
        }

        public string? GetTreeHash(string worktreePath)
        {
            if (_reattachResult is { } reattached && string.Equals(worktreePath, reattached.WorktreePath, StringComparison.Ordinal))
                return _reattachTreeHash;
            return _treeHash;
        }

        public (string WorktreePath, string BranchName)? TryReattachWorktree(
            string repositoryPath, string originatingBranch, string runId)
        {
            ReattachAttempted = true;
            return _reattachResult;
        }

        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException("Not called in restart tests");
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException("Not called in restart tests");
        public int GetStepCount(string runId) => throw new NotImplementedException("Not called in restart tests");
        public WorkflowMergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash) => throw new NotImplementedException("Not called in restart tests");
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
