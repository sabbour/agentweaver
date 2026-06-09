using LibGit2Sharp;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

using WorkflowMergeResult = Scaffolder.AgentRuntime.Workflow.MergeResult;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Adapts WorktreeManager to the IWorktreeOperations interface consumed by workflow executors.
/// </summary>
public sealed class WorktreeOperationsAdapter : IWorktreeOperations
{
    private readonly WorktreeManager _worktreeManager;
    private readonly RunStreamStore _streamStore;

    public WorktreeOperationsAdapter(WorktreeManager worktreeManager, RunStreamStore streamStore)
    {
        _worktreeManager = worktreeManager;
        _streamStore = streamStore;
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
                outcome.Reason),
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
}
