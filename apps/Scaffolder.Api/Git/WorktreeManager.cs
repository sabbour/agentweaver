using System.Collections.Concurrent;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Contracts;
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

    // Short-lived cache for committed diff results (5 s TTL).
    // Committed changes only vary when the agent pushes a new commit, so caching
    // eliminates redundant LibGit2Sharp Patch comparisons during the 3-second poll.
    private readonly ConcurrentDictionary<string, (DateTime ExpiresAt,
        IReadOnlyList<WorkspaceFileEntry> Entries,
        IReadOnlyDictionary<string, (int Added, int Removed)> LineCounts)> _committedCache = new();

    private static readonly TimeSpan CommittedCacheTtl = TimeSpan.FromSeconds(5);

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
    /// Returns files that differ between the originating branch tip and the worktree branch tip
    /// (committed changes in this run). Results are cached for <see cref="CommittedCacheTtl"/>.
    /// </summary>
    public IReadOnlyList<WorkspaceFileEntry> GetCommittedFileEntries(
        string repositoryPath, string originatingBranch, string worktreeBranch)
    {
        var cacheKey = $"{repositoryPath}|{originatingBranch}|{worktreeBranch}";
        if (_committedCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Entries;

        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch]
            ?? throw new InvalidOperationException($"Originating branch '{originatingBranch}' was not found.");
        var worktree = repo.Branches[worktreeBranch]
            ?? throw new InvalidOperationException($"Worktree branch '{worktreeBranch}' was not found.");

        using var diff = repo.Diff.Compare<TreeChanges>(origin.Tip.Tree, worktree.Tip.Tree);
        var entries = new List<WorkspaceFileEntry>();

        foreach (var change in diff)
        {
            if (change.Status == ChangeKind.Unmodified) continue;
            entries.Add(new WorkspaceFileEntry
            {
                Path   = NormalizePathSeparators(change.Path),
                Status = MapChangeKindToStatus(change.Status),
                Scope  = "committed",
            });
        }

        var result = (IReadOnlyList<WorkspaceFileEntry>)entries;
        // Store with empty line counts placeholder so GetFileDiffLineCounts can update the same entry.
        _committedCache[cacheKey] = (DateTime.UtcNow.Add(CommittedCacheTtl), result,
            new Dictionary<string, (int, int)>(StringComparer.Ordinal));
        return result;
    }

    /// <summary>
    /// Returns files that have staged or working-directory changes in the worktree
    /// compared to the worktree HEAD (uncommitted changes).
    /// </summary>
    public IReadOnlyList<WorkspaceFileEntry> GetUncommittedFileEntries(string worktreePath)
    {
        using var repo = new Repository(worktreePath);

        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked     = true,
            IncludeIgnored       = false,
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs   = false,
        });

        // Index changes are inserted first; working-directory changes overwrite them
        // because the working directory represents the most current state.
        var byPath = new Dictionary<string, WorkspaceFileEntry>(StringComparer.Ordinal);

        foreach (var entry in status)
        {
            var indexStatus = MapFileStatusToEntryStatus(entry.State, staged: true);
            if (indexStatus is not null)
            {
                var p = NormalizePathSeparators(entry.FilePath);
                byPath[p] = new WorkspaceFileEntry { Path = p, Status = indexStatus, Scope = "uncommitted" };
            }
        }

        foreach (var entry in status)
        {
            var workdirStatus = MapFileStatusToEntryStatus(entry.State, staged: false);
            if (workdirStatus is not null)
            {
                var p = NormalizePathSeparators(entry.FilePath);
                byPath[p] = new WorkspaceFileEntry { Path = p, Status = workdirStatus, Scope = "uncommitted" };
            }
        }

        return [.. byPath.Values];
    }

    /// <summary>
    /// Returns files changed in the most recent commit on the worktree branch vs its parent.
    /// Returns an empty list when the worktree HEAD has no parent (initial commit).
    /// </summary>
    public IReadOnlyList<WorkspaceFileEntry> GetLastCommitFileEntries(string worktreePath)
    {
        using var repo = new Repository(worktreePath);

        var head = repo.Head.Tip;
        if (head is null) return [];

        var parent = head.Parents.FirstOrDefault();
        if (parent is null) return [];

        using var diff = repo.Diff.Compare<TreeChanges>(parent.Tree, head.Tree);
        var entries = new List<WorkspaceFileEntry>();

        foreach (var change in diff)
        {
            if (change.Status == ChangeKind.Unmodified) continue;
            entries.Add(new WorkspaceFileEntry
            {
                Path   = NormalizePathSeparators(change.Path),
                Status = MapChangeKindToStatus(change.Status),
                Scope  = "committed",
            });
        }

        return entries;
    }

    /// <summary>
    /// Returns the unified diff for a single file relative to the originating branch tip,
    /// including both committed and any uncommitted working-directory changes.
    /// Returns (null, true) when the file is binary; (null, false) when no diff was produced.
    /// </summary>
    public (string? Diff, bool IsBinary) GetFileDiffEntry(
        string repositoryPath,
        string worktreePath,
        string originatingBranch,
        string worktreeBranch,
        string relativeFilePath)
    {
        var parts = new System.Text.StringBuilder();
        bool isBinary = false;

        // Committed diff: origin branch tip to worktree branch tip.
        using (var repo = new Repository(repositoryPath))
        {
            var origin   = repo.Branches[originatingBranch];
            var worktree = repo.Branches[worktreeBranch];

            if (origin is not null && worktree is not null)
            {
                using var patch = repo.Diff.Compare<Patch>(
                    origin.Tip.Tree,
                    worktree.Tip.Tree,
                    new[] { relativeFilePath },
                    new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false });

                var entry = patch[relativeFilePath];
                if (entry is not null)
                {
                    if (entry.IsBinaryComparison) isBinary = true;
                    else if (!string.IsNullOrEmpty(entry.Patch)) parts.Append(entry.Patch);
                }
            }
        }

        // Uncommitted diff: worktree HEAD to working directory and index.
        if (!isBinary && Directory.Exists(worktreePath))
        {
            using var repo = new Repository(worktreePath);
            var headTree = repo.Head.Tip?.Tree;

            if (headTree is not null)
            {
                using var patch = repo.Diff.Compare<Patch>(
                    headTree,
                    DiffTargets.WorkingDirectory | DiffTargets.Index,
                    new[] { relativeFilePath },
                    new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false });

                var entry = patch[relativeFilePath];
                if (entry is not null)
                {
                    if (entry.IsBinaryComparison) isBinary = true;
                    else if (!string.IsNullOrEmpty(entry.Patch)) parts.Append(entry.Patch);
                }
            }
        }

        if (isBinary) return (null, true);
        var result = parts.ToString();
        return (string.IsNullOrEmpty(result) ? null : result, false);
    }

    /// <summary>
    /// Returns per-file line counts from the committed diff between the originating branch
    /// and the worktree branch. Uses a single Patch comparison and LibGit2Sharp's built-in
    /// LinesAdded/LinesDeleted counters. Returns an empty dictionary on any error.
    /// </summary>
    public IReadOnlyDictionary<string, (int Added, int Removed)> GetFileDiffLineCounts(
        string repositoryPath, string originatingBranch, string worktreeBranch)
    {
        // Check cache — the committed entries and line counts share the same TTL bucket.
        var cacheKey = $"{repositoryPath}|{originatingBranch}|{worktreeBranch}";
        if (_committedCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTime.UtcNow
            && cached.LineCounts.Count > 0)
            return cached.LineCounts;

        try
        {
            using var repo = new Repository(repositoryPath);
            var origin   = repo.Branches[originatingBranch];
            var worktree = repo.Branches[worktreeBranch];
            if (origin is null || worktree is null)
                return new Dictionary<string, (int, int)>(StringComparer.Ordinal);

            using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, worktree.Tip.Tree);
            var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var entry in patch)
                counts[NormalizePathSeparators(entry.Path)] = (entry.LinesAdded, entry.LinesDeleted);

            // Update the cache entry with the computed line counts, resetting TTL.
            var entries = _committedCache.TryGetValue(cacheKey, out var prev) ? prev.Entries
                : (IReadOnlyList<WorkspaceFileEntry>)Array.Empty<WorkspaceFileEntry>();
            _committedCache[cacheKey] = (DateTime.UtcNow.Add(CommittedCacheTtl), entries, counts);
            return counts;
        }
        catch
        {
            return new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Returns per-file line counts for uncommitted changes in the worktree (working directory
    /// and index vs HEAD). Returns an empty dictionary on any error.
    /// </summary>
    public IReadOnlyDictionary<string, (int Added, int Removed)> GetUncommittedFileDiffLineCounts(
        string worktreePath)
    {
        try
        {
            using var repo = new Repository(worktreePath);
            var head = repo.Head.Tip;
            if (head is null)
                return new Dictionary<string, (int, int)>(StringComparer.Ordinal);

            using var patch = repo.Diff.Compare<Patch>(
                head.Tree,
                DiffTargets.WorkingDirectory | DiffTargets.Index);
            var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var entry in patch)
                counts[NormalizePathSeparators(entry.Path)] = (entry.LinesAdded, entry.LinesDeleted);
            return counts;
        }
        catch
        {
            return new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        }
    }

    private static string NormalizePathSeparators(string path) =>
        path.Replace('\\', '/');

    private static string MapChangeKindToStatus(ChangeKind kind) => kind switch
    {
        ChangeKind.Added       => "added",
        ChangeKind.Deleted     => "deleted",
        ChangeKind.Renamed     => "modified",
        ChangeKind.Copied      => "added",
        ChangeKind.TypeChanged => "modified",
        _                      => "modified",
    };

    private static string? MapFileStatusToEntryStatus(FileStatus state, bool staged)
    {
        if (staged)
        {
            if ((state & FileStatus.NewInIndex)        != 0) return "added";
            if ((state & FileStatus.ModifiedInIndex)   != 0) return "modified";
            if ((state & FileStatus.DeletedFromIndex)  != 0) return "deleted";
            if ((state & FileStatus.RenamedInIndex)    != 0) return "modified";
            if ((state & FileStatus.TypeChangeInIndex) != 0) return "modified";
            return null;
        }

        if ((state & FileStatus.NewInWorkdir)        != 0) return "added";
        if ((state & FileStatus.ModifiedInWorkdir)   != 0) return "modified";
        if ((state & FileStatus.DeletedFromWorkdir)  != 0) return "deleted";
        if ((state & FileStatus.RenamedInWorkdir)    != 0) return "modified";
        if ((state & FileStatus.TypeChangeInWorkdir) != 0) return "modified";
        return null;
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
        {
            // A sequencer in progress (MERGE_HEAD, REBASE_HEAD, etc.) cannot be bypassed
            // via the ref-only path — the user must resolve it first.
            if (blockReason.Contains("a git operation is in progress", StringComparison.Ordinal))
                return MergeOutcome.Blocked(blockReason);

            // A conflicted index also cannot be bypassed via the ref-only path — advancing
            // the branch ref underneath unresolved conflicts is unsafe.
            if (blockReason.Contains("conflicted", StringComparison.Ordinal))
                return MergeOutcome.Blocked(blockReason);

            // For all other cases (dirty working tree, staged changes, untracked collisions),
            // fall back to the ref-only path. MergeRefOnly never touches the working tree,
            // so local changes are irrelevant and preserved. The user will need a `git pull`
            // to sync their working tree after the merge.
            _logger.LogWarning(
                "Main working tree has uncommitted changes — using ref-only merge. " +
                "A `git pull` in the repository is needed to reflect the merged changes locally.");
            return MergeRefOnly(repo, origin, worktree, mergeBase, originatingBranch);
        }

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
                "The originating branch has diverged and the merge has conflicts that require human resolution.",
                ExtractConflictingFiles(result));
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
    /// Merges by updating only the branch ref — no working tree or index is touched.
    /// Used when the originating branch is NOT checked out (bare repo or HEAD on a different branch),
    /// OR as a fallback when the branch IS checked out but the working tree has uncommitted changes
    /// (dirty working tree, staged changes, untracked collisions). In the fallback case the user's
    /// local changes are left untouched; a <c>git pull</c> is required to sync the working tree.
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
                "The originating branch has diverged and the merge has conflicts that require human resolution.",
                ExtractConflictingFiles(result));
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
        var sequencerFiles = new[] { "MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG" };
        var sequencerDirs  = new[] { "rebase-merge", "rebase-apply" };

        foreach (var file in sequencerFiles)
        {
            if (File.Exists(Path.Combine(gitDir, file)))
            {
                blockReason = "a git operation is in progress (merge, rebase, cherry-pick, revert, bisect)";
                return false;
            }
        }
        foreach (var dir in sequencerDirs)
        {
            if (Directory.Exists(Path.Combine(gitDir, dir)))
            {
                blockReason = "a git operation is in progress (merge, rebase, cherry-pick, revert, bisect)";
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

    /// <summary>
    /// Extracts the list of conflicting relative file paths from a <see cref="MergeTreeResult"/>
    /// that has <see cref="MergeTreeStatus.Conflicts"/>. Paths are normalised to forward slashes.
    /// Uses Ours path when available, Theirs as fallback, Ancestor as last resort.
    /// Paths are validated to reject rooted paths, traversal sequences, and control characters.
    /// Results are capped at 50 entries.
    /// </summary>
    private static IReadOnlyList<string> ExtractConflictingFiles(MergeTreeResult mergeResult)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conflict in mergeResult.Conflicts)
        {
            if (paths.Count >= 50) break;

            var path = conflict.Ours?.Path
                    ?? conflict.Theirs?.Path
                    ?? conflict.Ancestor?.Path;
            if (string.IsNullOrEmpty(path)) continue;

            var normalized = NormalizePathSeparators(path);

            // Reject rooted paths (absolute paths starting with / or drive letters like C:\).
            if (Path.IsPathRooted(normalized)) continue;

            // Reject paths containing .. traversal segments.
            if (normalized.Split('/').Any(seg => seg == "..")) continue;

            // Reject paths with null bytes or C0/C1 control characters.
            if (normalized.Any(c => c == '\0' || (c < 0x20 && c != '\t') || (c >= 0x7F && c <= 0x9F))) continue;

            paths.Add(normalized);
        }
        return [.. paths];
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
