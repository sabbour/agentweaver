namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Abstraction over worktree git operations needed by workflow executors.
/// Implemented by WorktreeManager in the API project.
/// </summary>
public interface IWorktreeOperations
{
    string CommitChanges(string worktreePath, string runId);
    string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch);
    int GetStepCount(string runId);
    MergeResult MergeWorktree(string repositoryPath, string originatingBranch, string worktreeBranch, string expectedTreeHash);
    void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch);
    bool WorktreeExists(string worktreePath);
    string? GetTreeHash(string worktreePath);
}

/// <summary>Simplified merge result for the workflow executor.</summary>
public sealed record MergeResult(
    MergeResultKind Kind,
    string? CommitHash,
    string? MergeMode,
    string? PreviousHeadSha,
    string? NewHeadSha,
    bool WasFastForward,
    string? Reason,
    IReadOnlyList<string>? ConflictingFiles = null);

public enum MergeResultKind
{
    Merged,
    Blocked,
    Conflict
}
