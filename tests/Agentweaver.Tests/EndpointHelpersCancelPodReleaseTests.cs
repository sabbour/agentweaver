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

        await EndpointHelpers.CancelRunWorkAsync(
            run,
            new NoOpRunStore(),
            streamStore,
            registry,
            new NoOpWorktreeOperations(),
            NullLogger.Instance,
            CancellationToken.None,
            podLifecycle: lifecycle,
            sandboxRuntime: new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" });

        lifecycle.ReleasedRunIds.Should().Contain(runId.ToString(),
            "cancelling a run (via DELETE or /cancel) must reliably tear down the remote AgentHost pod, not just the local token");
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

    /// <summary>Minimal <see cref="IRunStore"/> fake — only <see cref="TrySetTerminalStatusAsync"/> is
    /// exercised by <see cref="EndpointHelpers.CancelRunWorkAsync"/>; every other member throws.</summary>
    private sealed class NoOpRunStore : IRunStore
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
}
