using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Sandbox;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Api;

/// <summary>
/// #350: <see cref="EndpointHelpers.CancelRunWorkAsync"/> is the SHARED cancellation path used by
/// both <c>DELETE /api/runs/{id}</c> and <c>POST /api/runs/{id}/cancel</c>. Previously it only
/// cancelled the local <see cref="RunWorkflowRegistry"/> token — which has no effect on a remote
/// AgentHost/sandbox pod (pod-per-run mode) — so a detached turn could keep executing tool calls
/// and emitting new tool.approval_required events against a run the system already considers dead.
/// These tests exercise the real helper (not a mock of it) against a fake pod-per-run lifecycle to
/// prove the pod is now reliably released whenever a run is cancelled via either endpoint.
/// </summary>
public sealed class EndpointHelpersCancelPodReleaseTests
{
    private static Run MakeRun(RunId id) => new()
    {
        Id = id,
        RepositoryPath = Path.GetTempPath(),
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "do something",
        SubmittingUser = "alice",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        AgentName = "tank",
        WorktreePath = null,
        WorktreeBranch = "agentweaver/run-branch",
    };

    [Fact]
    public async Task CancelRunWorkAsync_WhenPodPerRun_ReleasesTheAgentHostPod()
    {
        var lifecycle = new TrackingPodLifecycle();
        var runId = RunId.New();
        var run = MakeRun(runId);

        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        var registry = new RunWorkflowRegistry();
        var runStore = new NoOpRunStore();

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            registry,
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            podLifecycle: lifecycle,
            sandboxRuntime: new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" });

        lifecycle.ReleasedRunIds.Should().Contain(runId.ToString(),
            "cancelling a run (via DELETE or /cancel) must reliably tear down the remote AgentHost pod, not just the local token");
        streamStore.Get(runId.ToString())!.GetSnapshotSince(0).Events.Should().ContainSingle(evt =>
            evt.Type == EventTypes.RunCancelled
            && evt.Payload.ToString()!.Contains("abandoned", StringComparison.Ordinal));
        runStore.TerminalOutcome.Should().Match<TerminalRunOutcome>(outcome =>
            outcome.Status == RunStatus.Failed
            && outcome.EventType == EventTypes.RunCancelled
            && outcome.Payload.GetProperty("reason").GetString() == "abandoned");
    }

    [Fact]
    public async Task CancelRunWorkAsync_WhenInApiMode_DoesNotCallRelease()
    {
        var lifecycle = new TrackingPodLifecycle();
        var runId = RunId.New();
        var run = MakeRun(runId);

        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        var registry = new RunWorkflowRegistry();

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            new NoOpRunStore(),
            streamStore,
            registry,
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            podLifecycle: lifecycle,
            sandboxRuntime: new SandboxRuntimeOptions { AgentExecutionMode = "in-api" });

        lifecycle.ReleasedRunIds.Should().BeEmpty("in-api mode has no remote pod to release");
    }

    [Fact]
    public async Task CancelRunWorkAsync_ParentCancellation_PersistsAttributableCancelledEvent()
    {
        var runId = RunId.New();
        var parentRunId = RunId.New().ToString();
        var run = MakeRun(runId) with { ParentRunId = parentRunId };
        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        var runStore = new NoOpRunStore();

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            new RunWorkflowRegistry(),
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            eventStream: null,
            terminalOutcomeProjector: null,
            reason: "parent_cancelled",
            requestedByRunId: parentRunId);

        runStore.TerminalOutcome.Should().Match<TerminalRunOutcome>(outcome =>
            outcome.Status == RunStatus.Failed
            && outcome.EventType == EventTypes.RunCancelled
            && outcome.Payload.GetProperty("reason").GetString() == "parent_cancelled"
            && outcome.Payload.GetProperty("requested").GetBoolean()
            && outcome.Payload.GetProperty("requestedByRunId").GetString() == parentRunId);
    }

    [Fact]
    public async Task CancelRunWorkAsync_WhenTerminalTransitionLoses_DoesNotDestroyCompletedWork()
    {
        var lifecycle = new TrackingPodLifecycle();
        var worktree = new TrackingWorktreeOperations();
        var runId = RunId.New();
        var run = MakeRun(runId) with
        {
            WorktreePath = "C:\\repo\\.agentweaver\\worktrees\\child",
        };
        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            new NoOpRunStore(terminalizationResult: false),
            streamStore,
            new RunWorkflowRegistry(),
            worktree,
            NullLogger.Instance,
            CancellationToken.None,
            podLifecycle: lifecycle,
            sandboxRuntime: new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" },
            reason: "parent_cancelled",
            requestedByRunId: RunId.New().ToString());

        worktree.Removed.Should().BeFalse(
            "a concurrent successful terminal transition owns the completed child worktree");
        lifecycle.ReleasedRunIds.Should().Contain(runId.ToString(),
            "the cancellation attempt must still stop any remote execution after losing the terminal CAS");
        streamStore.Get(runId.ToString())!.GetSnapshotSince(0).Events.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelRunWorkAsync_WhenWorkerFailureWins_PersistsParentCancellationOnce()
    {
        var worktree = new TrackingWorktreeOperations();
        var runId = RunId.New();
        var parentRunId = RunId.New().ToString();
        var run = MakeRun(runId) with
        {
            ParentRunId = parentRunId,
            WorktreePath = "C:\\repo\\.agentweaver\\worktrees\\child",
        };
        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        using var durableEvents = new TemporarySqliteRunEventStream();
        var runStore = new NoOpRunStore(terminalizationResult: false);

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            new RunWorkflowRegistry(),
            worktree,
            NullLogger.Instance,
            CancellationToken.None,
            eventStream: durableEvents.Stream,
            reason: "parent_cancelled",
            requestedByRunId: parentRunId);
        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            new RunWorkflowRegistry(),
            worktree,
            NullLogger.Instance,
            CancellationToken.None,
            eventStream: durableEvents.Stream,
            reason: "parent_cancelled",
            requestedByRunId: parentRunId);

        worktree.Removed.Should().BeFalse(
            "the concurrent worker terminal outcome still owns the child worktree");
        var events = await durableEvents.Stream.GetPersistedEventsAsync(runId.ToString());
        var cancelled = events.Where(evt => evt.Type == EventTypes.RunCancelled).ToList();
        cancelled.Should().ContainSingle();
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(cancelled[0].Payload);
        payload.GetProperty("reason").GetString().Should().Be("parent_cancelled");
        payload.GetProperty("requested").GetBoolean().Should().BeTrue();
        payload.GetProperty("requestedByRunId").GetString().Should().Be(parentRunId);
    }

    [Fact]
    public async Task CancelRunWorkAsync_WhenCancellationWins_RetryDoesNotDuplicateProvenance()
    {
        var runId = RunId.New();
        var parentRunId = RunId.New().ToString();
        var run = MakeRun(runId) with { ParentRunId = parentRunId };
        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        using var durableEvents = new TemporarySqliteRunEventStream();
        var runStore = new NoOpRunStore();

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            new RunWorkflowRegistry(),
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            eventStream: durableEvents.Stream,
            reason: "parent_cancelled",
            requestedByRunId: parentRunId);
        runStore.TerminalizationResult = false;
        await EndpointHelpers.CancelRunWorkAsync(
            run,
            runStore,
            streamStore,
            new RunWorkflowRegistry(),
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            eventStream: durableEvents.Stream,
            reason: "parent_cancelled",
            requestedByRunId: parentRunId);

        var events = await durableEvents.Stream.GetPersistedEventsAsync(runId.ToString());
        events.Where(evt => evt.Type == EventTypes.RunCancelled).Should().ContainSingle();
    }

    [Fact]
    public async Task CancelRunWorkAsync_WhenPodLifecycleIsNull_DoesNotThrow()
    {
        var runId = RunId.New();
        var run = MakeRun(runId);

        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");
        var registry = new RunWorkflowRegistry();

        var act = async () => await EndpointHelpers.CancelRunWorkAsync(
            run,
            new NoOpRunStore(),
            streamStore,
            registry,
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            podLifecycle: null,
            sandboxRuntime: new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" });

        await act.Should().NotThrowAsync(
            "a null podLifecycle (not running in Kubernetes) must be a silent no-op, never an exception that could block cancellation");
    }

    [Fact]
    public async Task CancelRunWorkAsync_ActivePreviewPublication_ClearsLeaseBeforeTerminalizing()
    {
        var runId = RunId.New();
        var run = MakeRun(runId);
        var inner = new Agentweaver.Tests.Preview.PreviewPublicationLeaseRunStoreTests.LeaseRunStore();
        var runStore = new PreviewPublicationLeaseRunStore(
            inner, pollInterval: TimeSpan.FromMilliseconds(10));
        await runStore.TryBeginPreviewPublicationAsync(runId, DateTimeOffset.UtcNow.AddHours(1));

        var streamStore = new RunStreamStore();
        streamStore.Create(runId.ToString(), "alice");

        await EndpointHelpers.CancelRunWorkAsync(
                run,
                runStore,
                streamStore,
                new RunWorkflowRegistry(),
                new NoOpWorktreeOperations(),
                NullLogger.Instance,
                new CancellationToken(canceled: true))
            .WaitAsync(TimeSpan.FromSeconds(5));

        inner.TerminalCalls.Should().Be(1,
            "explicit cancellation clears the renewable lease before awaiting the terminal transition");
        (await inner.GetPreviewPublicationLeaseAsync(runId)).Should().BeNull();
    }

    /// <summary>Minimal <see cref="IRunStore"/> fake — only <see cref="TrySetTerminalOutcomeAsync"/> is
    /// exercised by <see cref="EndpointHelpers.CancelRunWorkAsync"/>; every other member throws.</summary>
    private sealed class NoOpRunStore : IRunStore
    {
        public NoOpRunStore(bool terminalizationResult = true)
        {
            TerminalizationResult = terminalizationResult;
        }

        public bool TerminalizationResult { get; set; }
        public TerminalRunOutcome? TerminalOutcome { get; private set; }

        public Task<bool> TrySetTerminalStatusAsync(
            RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
            => throw new NotSupportedException("Cancellation must persist a typed terminal outcome.");

        public Task<bool> TrySetTerminalOutcomeAsync(
            RunId runId, TerminalRunOutcome outcome, string? result, CancellationToken ct = default)
        {
            TerminalOutcome = outcome;
            return Task.FromResult(TerminalizationResult);
        }

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

    /// <summary>Minimal <see cref="IWorktreeOperations"/> fake — reports no worktree present so
    /// <see cref="EndpointHelpers.CancelRunWorkAsync"/>'s best-effort <c>RemoveWorktree</c> call is
    /// skipped; the pod-release behavior under test does not depend on worktree state.</summary>
    private sealed class NoOpWorktreeOperations : IWorktreeOperations
    {
        public bool WorktreeExists(string worktreePath) => false;
        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException();
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException();
        public int GetStepCount(string runId) => throw new NotImplementedException();
        public MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash) => throw new NotImplementedException();
        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch) => throw new NotImplementedException();
        public string? GetTreeHash(string worktreePath) => null;
    }

    private sealed class TrackingWorktreeOperations : IWorktreeOperations
    {
        public bool Removed { get; private set; }

        public bool WorktreeExists(string worktreePath) => true;
        public string CommitChanges(string worktreePath, string runId) => throw new NotImplementedException();
        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => throw new NotImplementedException();
        public int GetStepCount(string runId) => throw new NotImplementedException();
        public MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash) => throw new NotImplementedException();
        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch) => Removed = true;
        public string? GetTreeHash(string worktreePath) => null;
    }

    private sealed class TemporarySqliteRunEventStream : IDisposable
    {
        private readonly string _directory;

        public TemporarySqliteRunEventStream()
        {
            _directory = Path.Combine(Path.GetTempPath(), "aw-cancel-events-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var memoryDbPath = Path.Combine(_directory, "memory.db");
            using (var connection = new SqliteConnection($"Data Source={memoryDbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE "RunEvents" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                        "RunId" TEXT NOT NULL,
                        "Sequence" INTEGER NOT NULL,
                        "EventType" TEXT NOT NULL,
                        "PayloadJson" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX "IX_RunEvents_RunId_Sequence"
                        ON "RunEvents" ("RunId", "Sequence");
                    """;
                command.ExecuteNonQuery();
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(_directory, "agentweaver.db"),
                })
                .Build();
            Stream = new SqliteRunEventStream(configuration);
        }

        public SqliteRunEventStream Stream { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
