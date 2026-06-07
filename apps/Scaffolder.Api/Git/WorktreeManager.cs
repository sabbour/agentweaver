using LibGit2Sharp;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Git;

/// <summary>
/// Manages the git worktree that backs a run's isolated artifact directory
/// (FR-003). Each run gets a dedicated branch and worktree checked out from the
/// originating branch; the run's changes never touch the originating branch
/// until an approved merge (FR-016).
/// </summary>
public sealed class WorktreeManager
{
    private readonly string _basePath;
    private readonly Signature _signature;

    public WorktreeManager(IConfiguration configuration)
    {
        var configuredBase = configuration["Worktrees:BasePath"];
        _basePath = string.IsNullOrWhiteSpace(configuredBase)
            ? Path.Combine(AppPaths.DataDirectory, "worktrees")
            : Path.GetFullPath(configuredBase);

        Directory.CreateDirectory(_basePath);

        var authorName = configuration["Git:Author:Name"];
        var authorEmail = configuration["Git:Author:Email"];
        _signature = new Signature(
            string.IsNullOrWhiteSpace(authorName) ? "Scaffolder" : authorName,
            string.IsNullOrWhiteSpace(authorEmail) ? "scaffolder@localhost" : authorEmail,
            DateTimeOffset.UtcNow);
    }

    public static string BranchNameFor(RunId runId) => $"scaffolder/{runId}";

    public WorktreeInfo AddWorktree(string repositoryPath, string originatingBranch, RunId runId)
    {
        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch]
            ?? throw new InvalidOperationException($"Originating branch '{originatingBranch}' was not found.");

        var branchName = BranchNameFor(runId);
        if (repo.Branches[branchName] is null)
        {
            repo.CreateBranch(branchName, origin.Tip);
        }

        var worktreePath = Path.Combine(_basePath, runId.ToString());
        repo.Worktrees.Add(branchName, runId.ToString(), worktreePath, isLocked: false);

        return new WorktreeInfo
        {
            WorktreePath = worktreePath,
            BranchName = branchName
        };
    }

    public string CommitChanges(string worktreePath, RunId runId)
    {
        using var repo = new Repository(worktreePath);

        Commands.Stage(repo, "*");

        var signature = WithTimestamp();
        var commit = repo.Commit(
            $"Scaffolder run {runId}",
            signature,
            signature,
            new CommitOptions { AllowEmptyCommit = true });

        return commit.Tree.Sha;
    }

    public string GetDiff(string repositoryPath, string originatingBranch, string worktreeBranch)
    {
        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch]
            ?? throw new InvalidOperationException($"Originating branch '{originatingBranch}' was not found.");
        var worktree = repo.Branches[worktreeBranch]
            ?? throw new InvalidOperationException($"Worktree branch '{worktreeBranch}' was not found.");

        using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, worktree.Tip.Tree);
        return patch.Content;
    }

    public MergeOutcome MergeWorktree(
        string repositoryPath,
        string originatingBranch,
        string worktreeBranch,
        string expectedTreeHash)
    {
        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch]
            ?? throw new InvalidOperationException($"Originating branch '{originatingBranch}' was not found.");
        var worktree = repo.Branches[worktreeBranch]
            ?? throw new InvalidOperationException($"Worktree branch '{worktreeBranch}' was not found.");

        if (!string.Equals(worktree.Tip.Tree.Sha, expectedTreeHash, StringComparison.Ordinal))
        {
            return MergeOutcome.Conflict(
                "Worktree branch tree hash does not match the approved tree hash; the run changed after review.");
        }

        var mergeBase = repo.ObjectDatabase.FindMergeBase(origin.Tip, worktree.Tip);

        // Fast-forward when the originating branch has not advanced since the run started.
        if (mergeBase is not null && mergeBase.Sha == origin.Tip.Sha)
        {
            repo.Refs.UpdateTarget(repo.Refs[origin.CanonicalName], worktree.Tip.Id);
            return MergeOutcome.Merged(worktree.Tip.Sha);
        }

        var result = repo.ObjectDatabase.MergeCommits(
            origin.Tip,
            worktree.Tip,
            new MergeTreeOptions());

        if (result.Status == MergeTreeStatus.Conflicts)
        {
            return MergeOutcome.Conflict(
                "The originating branch has diverged and the merge has conflicts that require human resolution.");
        }

        var signature = WithTimestamp();
        var mergeCommit = repo.ObjectDatabase.CreateCommit(
            signature,
            signature,
            $"Merge scaffolder run into {originatingBranch}",
            result.Tree,
            new[] { origin.Tip, worktree.Tip },
            prettifyMessage: true);

        repo.Refs.UpdateTarget(repo.Refs[origin.CanonicalName], mergeCommit.Id);
        return MergeOutcome.Merged(mergeCommit.Sha);
    }

    public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch)
    {
        using var repo = new Repository(repositoryPath);

        var worktreeName = Path.GetFileName(worktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var worktree = repo.Worktrees[worktreeName];
        if (worktree is not null)
        {
            repo.Worktrees.Prune(worktree, true);
        }

        var branch = repo.Branches[worktreeBranch];
        if (branch is not null)
        {
            repo.Branches.Remove(branch);
        }
    }

    private Signature WithTimestamp() => new(_signature.Name, _signature.Email, DateTimeOffset.UtcNow);
}
