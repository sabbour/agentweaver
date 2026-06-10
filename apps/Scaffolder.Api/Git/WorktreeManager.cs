using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
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
    private readonly ILogger<WorktreeManager> _logger;

    public WorktreeManager(IConfiguration configuration, ILogger<WorktreeManager> logger)
    {
        _logger = logger;
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
        Repository repo;
        try
        {
            repo = new Repository(repositoryPath);
        }
        catch (RepositoryNotFoundException ex)
        {
            throw new RunSubmissionValidationException(
                "Repository path is not a valid git repository.", ex);
        }

        using (repo)
        {
            var origin = repo.Branches[originatingBranch]
                ?? throw new RunSubmissionValidationException(
                    $"Originating branch '{Truncate(originatingBranch, 200)}' was not found.");

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

    /// <summary>
    /// Attempts to merge the run's worktree branch back into the originating branch.
    /// Returns a trichotomy outcome:
    ///   Merged   — succeeded; ref (and working tree if checked out) updated.
    ///   Blocked  — retriable precondition failure; no mutations occurred.
    ///   Conflict — terminal failure; run should transition to MergeFailed.
    /// </summary>
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

        // (a) Tree-hash mismatch — terminal.
        if (!string.Equals(worktree.Tip.Tree.Sha, expectedTreeHash, StringComparison.Ordinal))
        {
            return MergeOutcome.Conflict(
                "Worktree branch tree hash does not match the approved tree hash; the run changed after review.");
        }

        var mergeBase = repo.ObjectDatabase.FindMergeBase(origin.Tip, worktree.Tip);

        // (b) Idempotency: worktree tip is already an ancestor of origin tip — already merged.
        // FindMergeBase returns worktree.Tip iff worktree is reachable from origin.Tip.
        if (mergeBase is not null &&
            string.Equals(mergeBase.Sha, worktree.Tip.Sha, StringComparison.Ordinal))
        {
            return MergeOutcome.Merged(
                origin.Tip.Sha, "ref-only", origin.Tip.Sha, origin.Tip.Sha, wasFastForward: true);
        }

        // (c) Detect whether the originating branch is currently checked out in the main working tree.
        // A detached HEAD has IsHeadDetached == true, so FriendlyName won't match any branch name
        // and correctly falls through to the ref-only path.
        // Platform split: on Windows, git branch refs are case-insensitive (main == Main);
        // on Linux/macOS they are case-sensitive (feature/x != feature/X).
        // Seraph M1 advisory: branch identity is already validated case-sensitively via
        // repo.Branches[originatingBranch] before this point, so OrdinalIgnoreCase on Windows
        // is not a confused-deputy vector.
        var headComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        bool checkedOut = !repo.Info.IsBare
            && !repo.Info.IsHeadDetached
            && string.Equals(repo.Head.FriendlyName, originatingBranch, headComparison);

        if (checkedOut)
        {
            return MergeCheckedOut(repo, origin, worktree, mergeBase, originatingBranch);
        }

        return MergeRefOnly(repo, origin, worktree, mergeBase, originatingBranch);
    }

    /// <summary>
    /// Merges when the originating branch is checked out in the main working tree.
    /// Performs a full clean-check before any mutation; uses Hard Reset to keep
    /// the working tree and index consistent with the updated branch ref.
    /// </summary>
    private MergeOutcome MergeCheckedOut(
        Repository repo,
        Branch origin,
        Branch worktree,
        Commit? mergeBase,
        string originatingBranch)
    {
        // (d-1) Full clean-check before any mutation.
        if (!IsWorkingTreeMergeSafe(repo, origin.Tip, worktree.Tip.Tree, out var blockReason))
            return MergeOutcome.Blocked(blockReason);

        var prevSha = origin.Tip.Sha;

        // (d-2) Fast-forward: origin hasn't advanced since the run started.
        if (mergeBase is not null &&
            string.Equals(mergeBase.Sha, origin.Tip.Sha, StringComparison.Ordinal))
        {
            repo.Reset(ResetMode.Hard, worktree.Tip);
            var newSha = repo.Head.Tip.Sha;
            return MergeOutcome.Merged(newSha, "working-tree-reset", prevSha, newSha, wasFastForward: true);
        }

        // (d-3) 3-way merge.
        var result = repo.ObjectDatabase.MergeCommits(origin.Tip, worktree.Tip, new MergeTreeOptions());
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

        // Hard Reset moves HEAD's branch ref to mergeCommit AND updates working tree + index.
        // If the process dies between CreateCommit and this Reset, the merge commit is left
        // dangling in the object database and the branch ref is unchanged. Restart recovery
        // reverts the run to AwaitingReview; a re-approve re-runs the 3-way merge and produces
        // an equivalent commit (at worst a redundant merge commit, never data loss).
        repo.Reset(ResetMode.Hard, mergeCommit);
        var newHeadSha = repo.Head.Tip.Sha;
        return MergeOutcome.Merged(newHeadSha, "working-tree-reset", prevSha, newHeadSha, wasFastForward: false);
    }

    /// <summary>
    /// Merges when the originating branch is NOT checked out (bare repo or HEAD on a different
    /// branch). Uses UpdateTarget to move the ref without touching any working tree or index.
    /// NEVER called when the branch is checked out — that would leave the working tree stale.
    /// </summary>
    private MergeOutcome MergeRefOnly(
        Repository repo,
        Branch origin,
        Branch worktree,
        Commit? mergeBase,
        string originatingBranch)
    {
        var prevSha = origin.Tip.Sha;

        // Fast-forward.
        if (mergeBase is not null &&
            string.Equals(mergeBase.Sha, origin.Tip.Sha, StringComparison.Ordinal))
        {
            repo.Refs.UpdateTarget(repo.Refs[origin.CanonicalName], worktree.Tip.Id);
            return MergeOutcome.Merged(
                worktree.Tip.Sha, "ref-only", prevSha, worktree.Tip.Sha, wasFastForward: true);
        }

        // 3-way merge.
        var result = repo.ObjectDatabase.MergeCommits(origin.Tip, worktree.Tip, new MergeTreeOptions());
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
        return MergeOutcome.Merged(
            mergeCommit.Sha, "ref-only", prevSha, mergeCommit.Sha, wasFastForward: false);
    }

    public void RemoveWorktree(string repositoryPath, string worktreePath, string worktreeBranch)
    {
        // Step 1: Delete the physical worktree directory to make the worktree STALE.
        // This alone does NOT release the branch lock — git_branch_is_checked_out reads the
        // admin entry at .git/worktrees/<name>/HEAD, not the physical directory. The directory
        // must be gone so that Prune (Step 2) can remove the admin entry.
        if (Directory.Exists(worktreePath))
        {
            Directory.Delete(worktreePath, recursive: true);
        }

        // Step 2: Prune the stale admin entry (.git/worktrees/<name>/HEAD).
        // THIS is what actually releases the branch lock — once the admin entry is gone,
        // git_branch_is_checked_out will no longer find a HEAD referencing the branch.
        // Wrapped in try/catch so a missing/already-pruned entry does not abort branch removal.
        var worktreeName = Path.GetFileName(worktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        try
        {
            using var pruneRepo = new Repository(repositoryPath);
            var worktree = pruneRepo.Worktrees[worktreeName];
            if (worktree is not null)
            {
                pruneRepo.Worktrees.Prune(worktree, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Worktree prune failed for '{WorktreeName}' — continuing with branch removal", worktreeName);
        }

        // Step 3: Remove the branch using a FRESH Repository handle. A new handle is required
        // because libgit2 caches the worktree list internally — reusing the prune handle would
        // still see the (now-deleted) admin entry in its cache, causing Branches.Remove to fail
        // with "current HEAD of a linked repository".
        using var branchRepo = new Repository(repositoryPath);
        var branch = branchRepo.Branches[worktreeBranch];
        if (branch is not null)
        {
            branchRepo.Branches.Remove(branch);
        }
    }

    /// <summary>
    /// Returns the current HEAD commit SHA of the repository for logging and manual recovery.
    /// Returns null on any error (e.g., repo inaccessible after a failed merge).
    /// </summary>
    public string? TryGetCurrentHeadSha(string repositoryPath)
    {
        try
        {
            using var repo = new Repository(repositoryPath);
            return repo.Head.Tip?.Sha;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that the working tree is safe to hard-reset into the merge result.
    /// Returns false (with a category reason) if any of the following are true:
    ///   - A sequencer operation is in progress (MERGE_HEAD, REBASE_HEAD, etc.)
    ///   - The index has conflicted entries
    ///   - Staged changes are present
    ///   - Modified or deleted tracked files exist in the working directory
    ///   - An untracked file would be overwritten by a path added in the merge target
    /// Non-colliding untracked files are ignored (do not block).
    /// Reasons are enumerated categories — never raw file names, content, or absolute paths (S2).
    /// </summary>
    private static bool IsWorkingTreeMergeSafe(
        Repository repo,
        Commit originTip,
        Tree targetTree,
        out string blockReason)
    {
        blockReason = string.Empty;

        // Check for in-progress sequencer state against the real git directory.
        var gitDir = repo.Info.Path;
        var sequencerFiles = new[] { "MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "BISECT_LOG" };
        var sequencerDirs  = new[] { "rebase-merge", "rebase-apply" };

        foreach (var file in sequencerFiles)
        {
            if (File.Exists(Path.Combine(gitDir, file)))
            {
                blockReason = "a merge or rebase is already in progress";
                return false;
            }
        }
        foreach (var dir in sequencerDirs)
        {
            if (Directory.Exists(Path.Combine(gitDir, dir)))
            {
                blockReason = "a merge or rebase is already in progress";
                return false;
            }
        }

        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked     = true,
            IncludeIgnored       = false,
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs   = false,
        });

        // Conflicted index entries.
        if (status.Any(e => (e.State & FileStatus.Conflicted) != 0))
        {
            blockReason = "the index has conflicted entries";
            return false;
        }

        // Staged changes (index differs from HEAD).
        const FileStatus stagedMask =
            FileStatus.NewInIndex
            | FileStatus.ModifiedInIndex
            | FileStatus.DeletedFromIndex
            | FileStatus.RenamedInIndex
            | FileStatus.TypeChangeInIndex;

        if (status.Any(e => (e.State & stagedMask) != 0))
        {
            blockReason = "there are staged changes in the index";
            return false;
        }

        // Modified or deleted tracked files in the working directory.
        const FileStatus workdirModifiedMask =
            FileStatus.ModifiedInWorkdir
            | FileStatus.DeletedFromWorkdir
            | FileStatus.TypeChangeInWorkdir;

        if (status.Any(e => (e.State & workdirModifiedMask) != 0))
        {
            blockReason = "there are uncommitted changes to tracked files";
            return false;
        }

        // Untracked files that collide with paths added by the merge target.
        // Non-colliding untracked files are ignored (do not block).
        var untrackedPaths = status
            .Where(e => (e.State & FileStatus.NewInWorkdir) != 0)
            .Select(e => e.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (untrackedPaths.Count > 0)
        {
            var diff = repo.Diff.Compare<TreeChanges>(originTip.Tree, targetTree);
            foreach (var change in diff)
            {
                // change.Path is the destination path that Reset(Hard) would materialize.
                // Added, Renamed/Copied (destination), and TypeChange all introduce content
                // at change.Path; any of them colliding with an untracked file would silently
                // overwrite it. Match all four so no destination path is missed.
                if (change.Status is ChangeKind.Added
                        or ChangeKind.Renamed
                        or ChangeKind.Copied
                        or ChangeKind.TypeChanged
                    && untrackedPaths.Contains(change.Path))
                {
                    blockReason = "untracked files would be overwritten by the merge";
                    return false;
                }
            }
        }

        return true;
    }

    private Signature WithTimestamp() => new(_signature.Name, _signature.Email, DateTimeOffset.UtcNow);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
