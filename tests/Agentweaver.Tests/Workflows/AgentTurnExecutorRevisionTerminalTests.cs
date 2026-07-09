using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Regression coverage for the in-place steering revision wedge
/// (v0.9.12-rc1: child run failed with `watch_stream_completed_without_terminal_event`).
///
/// The coordinator CHILD pipeline is a trimmed graph (agent -> child-assemble-ready) with no
/// failure->terminal edge. When the agent turn ends cleanly but the POST-TURN CommitChanges throws:
///  - a TRANSIENT git error (index.lock / ref race) must be RETRIED so the revision's edits still
///    commit and the child terminalizes assemble-ready on the SAME worktree (context preserved);
///  - a PERSISTENT failure must surface as a VISIBLE failure (rethrow) — NEVER a fabricated
///    no-change assemble_ready, which would silently drop the revision's edits and hide the error.
/// The visible terminal is produced by RunWatchLoopService (child ExecutorFailedEvent -> Failed),
/// covered by RunWatchLoopChildExecutorFailureTests.
/// </summary>
public sealed class AgentTurnExecutorRevisionTerminalTests
{
    private static AgentTurnInput RevisionInput() => new(
        RunId: "child-revision-run",
        Task: "Address the review feedback.",
        WorktreePath: AppContext.BaseDirectory,
        WorktreeBranch: "agentweaver/child-branch",
        RepositoryPath: AppContext.BaseDirectory,
        OriginatingBranch: "main",
        ModelSource: "github-copilot",
        ModelId: "claude-sonnet-5",
        SubmittingUser: "owner",
        IsRevision: true);

    private static AgentTurnExecutor NewExecutor(IWorktreeOperations worktree) => new(
        new CleanTurnAgent(),
        worktree,
        _ => null,
        NullLogger<AgentTurnExecutor>.Instance);

    [Fact]
    public async Task TransientCommitFailure_IsRetried_ThenSucceeds_OnSameWorktree()
    {
        // Fails the first two attempts (transient index.lock), succeeds on the third.
        var worktree = new StubWorktreeOperations
        {
            FailuresBeforeSuccess = 2,
            CommittedTreeHash = "committed-tree-after-retry",
            DiffText = "diff --git a/file.txt b/file.txt",
        };
        var executor = NewExecutor(worktree);

        var result = await executor.HandleAsync(RevisionInput(), context: null!, CancellationToken.None);

        result.TreeHash.Should().Be("committed-tree-after-retry",
            "a transient commit failure must be retried so the revision's edits still commit (context preserved)");
        result.Diff.Should().Be("diff --git a/file.txt b/file.txt");
        worktree.CommitAttempts.Should().Be(3, "the bounded retry must re-attempt the transient commit");
        worktree.GetTreeHashCalled.Should().BeFalse("there is no HEAD-tree fallback anymore — success must be a real commit");
    }

    [Fact]
    public async Task PersistentCommitFailure_Rethrows_VisibleFailure_NeverFakeSuccess()
    {
        // Every attempt throws (corrupt/unopenable repo) — must NOT degrade to a no-change success.
        var worktree = new StubWorktreeOperations
        {
            FailuresBeforeSuccess = int.MaxValue,
            HeadTreeHash = "pre-revision-head",
        };
        var executor = NewExecutor(worktree);

        var act = async () => await executor.HandleAsync(RevisionInput(), context: null!, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a persistent commit failure must surface as a VISIBLE executor failure (the child run then terminalizes as Failed), never a fabricated no-change assemble_ready");
        worktree.CommitAttempts.Should().Be(3, "the bounded retry exhausts its attempts before rethrowing");
        worktree.GetTreeHashCalled.Should().BeFalse("the removed HEAD-tree fallback must not silently mask the failure");
    }

    [Fact]
    public async Task CleanCommit_ReturnsNormalAssembleReadyOutput_NoRetry()
    {
        var worktree = new StubWorktreeOperations
        {
            FailuresBeforeSuccess = 0,
            CommittedTreeHash = "committed-tree-xyz",
            DiffText = "diff --git a/file.txt b/file.txt",
        };
        var executor = NewExecutor(worktree);

        var result = await executor.HandleAsync(RevisionInput(), context: null!, CancellationToken.None);

        result.TreeHash.Should().Be("committed-tree-xyz");
        result.Diff.Should().Be("diff --git a/file.txt b/file.txt");
        result.ContentSafetyFlagged.Should().BeFalse();
        worktree.CommitAttempts.Should().Be(1, "the happy path commits on the first attempt");
    }

    private sealed class CleanTurnAgent : IWorkflowTurnAgent
    {
        public Task SetupAsync(
            string workingDirectory,
            string repositoryPath,
            string runId,
            string? modelId,
            string? systemPromptContext,
            ChannelWriter<RunEvent>? streamWriter,
            string? projectId,
            string? agentName,
            string? apiBaseUrl,
            string? apiKey,
            CancellationToken ct,
            string? userId = null) => Task.CompletedTask;

        // Agent turn ends cleanly (mirrors the live agent.turn.end); the wedge came from the
        // post-turn commit, not the turn itself.
        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) =>
            Task.FromResult("Revision applied.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubWorktreeOperations : IWorktreeOperations
    {
        /// <summary>Number of leading CommitChanges calls that throw before one succeeds.</summary>
        public int FailuresBeforeSuccess { get; set; }
        public string CommittedTreeHash { get; set; } = "committed-tree";
        public string? HeadTreeHash { get; set; } = "head-tree";
        public string DiffText { get; set; } = string.Empty;
        public int CommitAttempts { get; private set; }
        public bool GetTreeHashCalled { get; private set; }

        public string CommitChanges(string worktreePath, string runId)
        {
            CommitAttempts++;
            if (CommitAttempts <= FailuresBeforeSuccess)
                throw new InvalidOperationException("simulated LibGit2 failure during post-turn commit");
            return CommittedTreeHash;
        }

        public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch) => DiffText;

        public int GetStepCount(string runId) => 0;

        public string? GetTreeHash(string worktreePath)
        {
            GetTreeHashCalled = true;
            return HeadTreeHash;
        }

        public MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash)
            => throw new NotSupportedException();

        public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch)
            => throw new NotSupportedException();

        public bool WorktreeExists(string worktreePath) => true;
    }
}
