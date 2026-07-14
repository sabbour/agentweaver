using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

using WorkflowMergeResult = Agentweaver.AgentRuntime.Workflow.MergeResult;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Adapts WorktreeManager to the IWorktreeOperations interface consumed by workflow executors.
/// </summary>
public sealed class WorktreeOperationsAdapter : IWorktreeOperations
{
    private readonly WorktreeManager _worktreeManager;
    private readonly RunStreamStore _streamStore;
    private readonly ILogger<WorktreeOperationsAdapter> _logger;

    public WorktreeOperationsAdapter(
        WorktreeManager worktreeManager,
        RunStreamStore streamStore,
        ILogger<WorktreeOperationsAdapter> logger)
    {
        _worktreeManager = worktreeManager;
        _streamStore = streamStore;
        _logger = logger;
    }

    public void ApplyPreparedWriteback(
        string repositoryPath,
        string worktreePath,
        string worktreeBranch,
        string runId,
        PreparedWriteback writeback)
    {
        _worktreeManager.ApplyPreparedWriteback(
            repositoryPath,
            worktreePath,
            worktreeBranch,
            RunId.Parse(runId),
            writeback);
    }

    public string CommitChanges(string worktreePath, string runId)
    {
        return _worktreeManager.CommitChanges(worktreePath, RunId.Parse(runId));
    }

    public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch)
    {
        try
        {
            return _worktreeManager.GetDiff(repositoryPath, originatingBranch, worktreeBranch);
        }
        catch
        {
            return string.Empty;
        }
    }

    public int GetStepCount(string runId)
    {
        var entry = _streamStore.Get(runId);
        if (entry is null) return 0;
        return entry.GetSnapshotSince(0).Events.Count(e => e.Type == EventTypes.ToolCall);
    }

    public WorkflowMergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash)
    {
        var outcome = _worktreeManager.MergeWorktree(repositoryPath, originatingBranch, worktreeBranch, expectedTreeHash);
        return outcome.Kind switch
        {
            MergeOutcomeKind.Merged => new WorkflowMergeResult(
                MergeResultKind.Merged,
                outcome.CommitHash,
                outcome.MergeMode,
                outcome.PreviousHeadSha,
                outcome.NewHeadSha,
                outcome.WasFastForward,
                null),
            MergeOutcomeKind.Blocked => new WorkflowMergeResult(
                MergeResultKind.Blocked,
                null, null, null, null, false,
                outcome.Reason),
            MergeOutcomeKind.Conflict => new WorkflowMergeResult(
                MergeResultKind.Conflict,
                null, null, null, null, false,
                outcome.Reason,
                outcome.ConflictingFiles),
            _ => throw new InvalidOperationException($"Unknown merge outcome kind: {outcome.Kind}")
        };
    }

    public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch)
    {
        _worktreeManager.RemoveWorktree(repositoryPath, worktreePath, worktreeBranch);
    }

    public bool WorktreeExists(string worktreePath)
    {
        return Directory.Exists(worktreePath);
    }

    public string? GetTreeHash(string worktreePath)
    {
        try
        {
            using var repo = new Repository(worktreePath);
            return repo.Head.Tip?.Tree.Sha;
        }
        catch
        {
            return null;
        }
    }

    public (string WorktreePath, string BranchName)? TryReattachWorktree(
        string repositoryPath, string originatingBranch, string runId)
    {
        if (!RunId.TryParse(runId, out var parsedRunId)) return null;

        var branchName = WorktreeManager.BranchNameFor(parsedRunId);
        try
        {
            // Reconstruction requires the durable run branch to still exist — if the branch itself is
            // gone (not just the ephemeral worktree directory), there is nothing to recreate from and
            // this is genuinely unrecoverable.
            if (!_worktreeManager.BranchExists(repositoryPath, branchName)) return null;

            var info = _worktreeManager.EnsureWorktree(repositoryPath, originatingBranch, parsedRunId);
            return (info.WorktreePath, info.BranchName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to reattach worktree for run {RunId} in repository '{RepositoryPath}'",
                runId,
                repositoryPath);
            return null;
        }
    }

    public IndexLockClearResult TryClearStaleIndexLock(string worktreePath)
    {
        return _worktreeManager.ClearStaleIndexLock(worktreePath);
    }
}
