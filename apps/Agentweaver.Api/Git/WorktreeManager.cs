using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;

namespace Agentweaver.Api.Git;

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

    /// <summary>How old a git lock file must be before it is considered stale (left by a crashed
    /// process) and safe to delete. A lock held by a CONCURRENTLY-RUNNING git operation is only a few
    /// milliseconds old, so this threshold prevents one replica from deleting the lock another replica
    /// is actively holding (the multi-pod integration-merge race). Configurable via
    /// <c>Coordinator:StaleLockThresholdSeconds</c> (default 15 s).</summary>
    private readonly TimeSpan _staleLockThreshold;

    // Short-lived cache for committed diff results (5 s TTL).
    // Committed changes only vary when the agent pushes a new commit, so caching
    // eliminates redundant LibGit2Sharp Patch comparisons during the 3-second poll.
    private readonly ConcurrentDictionary<string, (DateTime ExpiresAt,
        IReadOnlyList<WorkspaceFileEntry> Entries,
        IReadOnlyDictionary<string, (int Added, int Removed)> LineCounts)> _committedCache = new();

    private static readonly TimeSpan CommittedCacheTtl = TimeSpan.FromSeconds(5);

    public WorktreeManager(
        IConfiguration configuration,
        ILogger<WorktreeManager> logger)
    {
        _logger = logger;
        var configuredBase = configuration["Worktrees:BasePath"];
        _basePath = string.IsNullOrWhiteSpace(configuredBase)
            ? Path.Combine(AppPaths.DataDirectory, "worktrees")
            : Path.GetFullPath(configuredBase);

        var workspaceMount = configuration["Sandbox:Kubernetes:WorkspaceMountPath"]
            ?? configuration["Workspace:PersistentVolume:MountRoot"]
            ?? configuration["Workspace:Path"]
            ?? "/workspace";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")) &&
            !IsPathUnder(_basePath, Path.GetFullPath(workspaceMount)))
        {
            throw new InvalidOperationException(
                $"Worktrees:BasePath must resolve under the shared workspace mount '{workspaceMount}' in Kubernetes. " +
                $"Resolved value: '{_basePath}'.");
        }

        Directory.CreateDirectory(_basePath);

        var authorName = configuration["Git:Author:Name"];
        var authorEmail = configuration["Git:Author:Email"];
        _signature = new Signature(
            string.IsNullOrWhiteSpace(authorName) ? "Agentweaver" : authorName,
            string.IsNullOrWhiteSpace(authorEmail) ? "agentweaver@localhost" : authorEmail,
            DateTimeOffset.UtcNow);

        var staleLockSecs = configuration.GetValue("Coordinator:StaleLockThresholdSeconds", 15.0);
        _staleLockThreshold = TimeSpan.FromSeconds(Math.Max(0.0, staleLockSecs));
    }

    public static string BranchNameFor(RunId runId) => $"agentweaver/{runId}";

    /// <summary>
    /// Idempotent worktree provisioner. Returns immediately if the physical directory already
    /// exists (normal first-run case). When the directory is missing — e.g. after a pod restart
    /// wiped ephemeral storage while the git branch and DB row persist on the PVC — prunes any
    /// stale git admin entry for the run branch (a stale entry blocks <c>git worktree add</c>
    /// with "already checked out") and then recreates the worktree from the existing branch.
    /// </summary>
    /// <remarks>
    /// This is the correct entry point for the orchestration shared-worktree re-provisioning
    /// path. <see cref="AddWorktree"/> should only be called for genuinely new worktrees;
    /// <see cref="EnsureWorktree"/> is safe to call any number of times.
    /// </remarks>
    public WorktreeInfo EnsureWorktree(string repositoryPath, string originatingBranch, RunId runId)
    {
        var worktreePath = Path.Combine(_basePath, runId.ToString());
        var branchName = BranchNameFor(runId);

        // Happy path: directory exists — valid worktree, nothing to do.
        if (Directory.Exists(worktreePath))
            return new WorktreeInfo { WorktreePath = worktreePath, BranchName = branchName };

        // Physical directory missing (pod restart wiped ephemeral storage). Any git admin entry
        // at .git/worktrees/<runId> is now stale and will cause `git worktree add` to fail with
        // "already checked out at <missing path>". Prune it before recreating.
        _logger.LogWarning(
            "Orchestration worktree directory missing at '{WorktreePath}' (runId={RunId}); " +
            "pruning stale git admin entry and recreating from branch '{BranchName}'",
            worktreePath, runId, branchName);

        // Use PruneWorktreeByName (looks up the admin entry directly by the worktree's NAME)
        // rather than PruneWorktreesCheckedOutOnBranch (which must open each worktree's
        // repository to inspect its HEAD and fails when the physical directory is missing).
        PruneWorktreeByName(repositoryPath, runId.ToString());

        // AddWorktree now provisions via the git CLI (`git worktree add -b agentweaver/<runId>
        // <path> <sha>`), which does NOT create a throw-away `<runId>`-named side-effect branch.
        // Older code (pre-v0.9.33) used LibGit2Sharp WorktreeCollection.Add, whose underlying
        // git_worktree_add always created such a branch as a side-effect. During a rolling restart a
        // worktree may have been provisioned by that old code, leaving an orphaned `<runId>` branch
        // that would make a fresh `git worktree add` fail with a name conflict. Delete it before
        // recreating. For new-code worktrees no such branch exists, so this is a harmless no-op.
        DeleteOrphanedWorktreeBranch(repositoryPath, runId.ToString());

        // AddWorktree skips branch creation when agentweaver/<runId> already exists (recovery: always).
        // The recreated worktree checks out the existing branch, preserving all prior committed work.
        return AddWorktree(repositoryPath, originatingBranch, runId);
    }

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

        var branchName = BranchNameFor(runId);
        var worktreePath = Path.Combine(_basePath, runId.ToString());
        bool branchExists;
        string startSha;

        using (repo)
        {
            var origin = repo.Branches[originatingBranch]
                ?? throw new RunSubmissionValidationException(
                    $"Originating branch '{Truncate(originatingBranch, 200)}' was not found.");

            branchExists = repo.Branches[branchName] is not null;

            // Resolve the originating branch to a concrete commit SHA while the repo handle is open.
            // Passing the resolved SHA (NOT the raw branch string) to `git worktree add` preserves the
            // case-insensitive branch resolution LibGit2Sharp gives us (e.g. originatingBranch="main"
            // resolving against a HEAD named "Main"), which callers/tests rely on and which the
            // case-sensitive git CLI would otherwise fail to reproduce.
            startSha = branchExists ? string.Empty : origin.Tip.Sha;
        }
        // Dispose the LibGit2Sharp repo handle (exit the using block) BEFORE invoking the git CLI to
        // avoid Windows file-handle contention on the .git directory.

        if (!branchExists)
        {
            // New run: create the run branch at the resolved originating commit and check it out into
            // a fresh worktree in a single git_worktree_add. This replaces the old LibGit2Sharp
            // two-step (add at the main repo's HEAD + a non-forced Commands.Checkout onto
            // agentweaver/<runId>), whose step 2 aborted with CheckoutConflictException when the
            // integration tip diverged from HEAD in a checkout-unsafe way (e.g. a file<->directory
            // typechange). Dependent subtasks base on the integration branch and hit exactly that.
            RunGit(repositoryPath, "worktree", "add", "-b", branchName, worktreePath, startSha);
        }
        else
        {
            // Recovery: the run branch already exists (e.g. re-provisioning after a pod restart wiped
            // ephemeral storage). Check the existing branch out into the worktree, preserving all
            // prior committed work.
            RunGit(repositoryPath, "worktree", "add", worktreePath, branchName);
        }

        return new WorktreeInfo
        {
            WorktreePath = worktreePath,
            BranchName = branchName
        };
    }

    /// <summary>
    /// Creates a short-lived detached worktree at <paramref name="sourceBranch"/> for read-only gates
    /// (for example collective Build/Test). The source branch is never checked out in the primary
    /// repository, so later headless ref resets can delete/recreate it safely.
    /// </summary>
    public WorktreeInfo AddDetachedWorktree(string repositoryPath, string sourceBranch, string worktreeName)
    {
        var safeName = SanitizeWorktreeName(worktreeName);
        var worktreePath = DetachedWorktreePath(safeName);

        if (Directory.Exists(worktreePath))
            Directory.Delete(worktreePath, recursive: true);

        PruneWorktreeByName(repositoryPath, safeName);
        TryRunGitWorktreePrune(repositoryPath);

        RunGit(
            repositoryPath,
            "worktree",
            "add",
            "--detach",
            worktreePath,
            sourceBranch);

        return new WorktreeInfo
        {
            WorktreePath = worktreePath,
            BranchName = string.Empty,
        };
    }

    public string DetachedWorktreePath(string worktreeName) =>
        Path.Combine(_basePath, SanitizeWorktreeName(worktreeName));

    private static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void RemoveDetachedWorktree(string repositoryPath, string worktreePath)
    {
        if (Directory.Exists(worktreePath))
            Directory.Delete(worktreePath, recursive: true);

        var worktreeName = Path.GetFileName(worktreePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(worktreeName))
            PruneWorktreeByName(repositoryPath, worktreeName);
    }

    public bool BranchExists(string repositoryPath, string branchName)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Branches[branchName] is not null;
    }

    /// <summary>
    /// Validates a child branch tip against the run handoff contract (issue #197 dependency-base fix).
    /// Returns <c>true</c> when <paramref name="branchName"/> exists AND — when
    /// <paramref name="expectedTreeSha"/> is non-empty — the branch tip's TREE sha equals it. This is the
    /// authoritative "does this committed child carry the artifacts the coordinator recorded?" check,
    /// replacing the unreliable <c>run.Diff</c> display string as the inclusion predicate. A non-empty
    /// <paramref name="expectedTreeSha"/> that mismatches the branch tip means the branch is stale /
    /// diverged from the recorded handoff (e.g. an in-place steer re-commit whose row was not observed),
    /// so the caller must NOT include it silently. An empty/absent <paramref name="expectedTreeSha"/> is
    /// treated as "no contract to verify" and passes as long as the branch exists.
    /// <see cref="BranchExists"/> alone is too weak — it cannot detect a stale/mismatched tip.
    /// </summary>
    public bool BranchTipMatchesTree(string repositoryPath, string branchName, string? expectedTreeSha)
    {
        if (string.IsNullOrEmpty(branchName) || !Repository.IsValid(repositoryPath))
            return false;

        using var repo = new Repository(repositoryPath);
        var tip = repo.Branches[branchName]?.Tip;
        if (tip is null)
            return false;
        if (string.IsNullOrEmpty(expectedTreeSha))
            return true;
        return string.Equals(tip.Tree.Sha, expectedTreeSha, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the tip COMMIT sha of <paramref name="branchName"/>, or <c>null</c> when the branch is
    /// absent/empty. Used by the dependency-base contains-check to obtain a concrete commit id to feed
    /// into <see cref="BranchContains"/> (which needs a commit, not the run's TREE hash).
    /// </summary>
    public string? GetBranchTipCommitSha(string repositoryPath, string branchName)
    {
        if (string.IsNullOrEmpty(branchName) || !Repository.IsValid(repositoryPath))
            return null;

        using var repo = new Repository(repositoryPath);
        return repo.Branches[branchName]?.Tip?.Sha;
    }

    /// <summary>
    /// Ancestor / containment check used to VERIFY that an integration branch actually incorporates a
    /// required dependency's HEAD before a dependent child dispatches from it (issue #197, BLOCKING #3).
    /// Returns <c>true</c> when <paramref name="candidateTipSha"/> is reachable from
    /// <paramref name="branchName"/>'s tip (i.e. the merge-base of the two is exactly the candidate),
    /// meaning the candidate commit is contained in the branch. An empty <paramref name="candidateTipSha"/>
    /// is vacuously contained. This is the authoritative guard against a clobbered / stale / incomplete
    /// integration branch (BuildIntegrationBranch deletes+recreates the ref each rebuild), so a concurrent
    /// or crashed rebuild that dropped a required child is detected here and repaired before dispatch.
    /// </summary>
    public bool BranchContains(string repositoryPath, string branchName, string candidateTipSha)
    {
        if (string.IsNullOrEmpty(candidateTipSha))
            return true;
        if (string.IsNullOrEmpty(branchName) || !Repository.IsValid(repositoryPath))
            return false;

        using var repo = new Repository(repositoryPath);
        var branchTip = repo.Branches[branchName]?.Tip;
        if (branchTip is null)
            return false;

        var candidate = repo.Lookup<Commit>(candidateTipSha);
        if (candidate is null)
            return false;

        var mergeBase = repo.ObjectDatabase.FindMergeBase(branchTip, candidate);
        return mergeBase is not null && string.Equals(mergeBase.Sha, candidate.Sha, StringComparison.Ordinal);
    }

    public string CommitChanges(string worktreePath, RunId runId)
    {
        using var repo = new Repository(worktreePath);

        Commands.Unstage(repo, "*");
        var pathsToStage = ResolveStagingPaths(repo, worktreePath, runId);
        if (pathsToStage.Count > 0)
            Commands.Stage(repo, pathsToStage);

        // Check whether staging produced any actual changes vs HEAD. Creating an empty commit when
        // the agent wrote nothing causes the child branch to diverge from the origin with a
        // zero-diff commit that masquerades as delivered work — the assembly diff ends up empty and
        // the review panel shows "No changes." If nothing was staged, return the HEAD tree hash so
        // HasChanges evaluates to false and the child is correctly flagged as a no-change subtask.
        var headTree = repo.Head.Tip?.Tree;
        using var stagedDiff = repo.Diff.Compare<TreeChanges>(headTree, DiffTargets.Index);
        if (stagedDiff.Count == 0)
            return headTree?.Sha ?? string.Empty;

        var signature = WithTimestamp();
        var commit = repo.Commit(
            $"Agentweaver run {runId}",
            signature,
            signature);

        return commit.Tree.Sha;
    }

    private IReadOnlyList<string> ResolveStagingPaths(Repository repo, string worktreePath, RunId runId)
    {
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            IncludeIgnored = false,
            RecurseUntrackedDirs = true,
            RecurseIgnoredDirs = false,
        });

        // Scope-independent staging (issue #222 root cause): capture EVERY changed entry that is not
        // ignored. Previously this set was filtered against a whitelist of path-like tokens scraped
        // from the subtask scope prose, so a deliverable written outside that (mis-scraped) list —
        // e.g. an entire server/ tree — was silently dropped and never committed, leaving dependent
        // subtasks unable to see the work. This canonical set already INCLUDES deletions and renames,
        // so it must NOT be narrowed to a New/Modified-only mask (that would drop deletions and
        // corrupt renames).
        var changed = status
            .Where(e => e.State != 0 && (e.State & FileStatus.Ignored) == 0)
            .Select(e => NormalizePathSeparators(e.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.Count == 0)
            return [];

        return ExcludeNestedRepositoryPaths(worktreePath, changed, runId);
    }

    /// <summary>
    /// Defensively removes any changed path that lives at or under a NESTED git repository — a
    /// subdirectory that contains its own <c>.git</c>. Scaffolders such as create-react-app and Vite
    /// run <c>git init</c>, and libgit2 would stage such a subdirectory as an empty gitlink (a
    /// submodule pointer) instead of the deliverable file tree, silently losing the work. Any skipped
    /// nested-repo roots are logged as a warning.
    /// </summary>
    private IReadOnlyList<string> ExcludeNestedRepositoryPaths(
        string worktreePath, IReadOnlyList<string> changed, RunId runId)
    {
        var nestedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in changed)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Walk every prefix directory from the top-level segment down to (and INCLUDING) the leaf.
            // The leaf is probed on purpose: libgit2 reports an embedded repo as a single "client" or
            // "client/" gitlink entry (no child paths), so unless we test the leaf itself we would miss
            // it and stage it as an empty gitlink (the N2 case). DirectoryIsGitRepository only matches
            // real directories containing a .git, so probing a regular file leaf costs one extra stat
            // and never false-positives — an accepted trade-off; do not "optimize" the leaf probe away.
            var prefix = string.Empty;
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[i] : prefix + "/" + segments[i];
                if (nestedRoots.Contains(prefix))
                    break;
                if (inspected.Add(prefix) && DirectoryIsGitRepository(
                        Path.Combine(worktreePath, prefix.Replace('/', Path.DirectorySeparatorChar))))
                {
                    nestedRoots.Add(prefix);
                    break;
                }
            }
        }

        if (nestedRoots.Count == 0)
            return changed;

        _logger.LogWarning(
            "Run {RunId}: skipping {Count} nested git repository root(s) during staging to avoid " +
            "committing them as empty gitlinks: {Roots}",
            runId, nestedRoots.Count, string.Join(", ", nestedRoots.OrderBy(r => r, StringComparer.Ordinal)));

        return changed
            .Where(path => !IsUnderAnyRoot(path, nestedRoots))
            .ToList();
    }

    private static bool IsUnderAnyRoot(string path, HashSet<string> roots)
    {
        var trimmed = path.TrimEnd('/');
        foreach (var root in roots)
        {
            if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool DirectoryIsGitRepository(string absoluteDirectory)
    {
        var gitPath = Path.Combine(absoluteDirectory, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
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
    /// Computes the unified diff of <paramref name="branch"/> vs <paramref name="originatingBranch"/>,
    /// returning <c>null</c> when either branch is absent (e.g. a coordinator integration branch that
    /// has not been assembled yet) instead of throwing. Used to surface the collective assembly diff
    /// for a coordinator run that owns no worktree.
    /// </summary>
    public string? TryGetBranchDiff(string repositoryPath, string originatingBranch, string branch)
    {
        if (string.IsNullOrEmpty(repositoryPath) || !Repository.IsValid(repositoryPath))
            return null;

        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch];
        var target = repo.Branches[branch];
        if (origin?.Tip is null || target?.Tip is null)
            return null;

        using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, target.Tip.Tree);
        return patch.Content;
    }

    /// <summary>
    /// Phase 3 (D1): builds the COLLECTIVE integration branch. Creates (or resets)
    /// <paramref name="integrationBranch"/> at the originating branch tip, then merges each eligible
    /// child branch in <paramref name="childBranchesInOrder"/> (already dependency/topologically
    /// ordered) into it using HEADLESS tree merges (<see cref="ObjectDatabase.MergeCommits"/>) — no
    /// working directory or worktree is checked out, so this is safe to run from the coordinator's
    /// background loop. When a merge conflict occurs, the coordinator currently auto-resolves it by
    /// accepting the CHILD branch's version for each conflicting path and continues building the
    /// aggregate. On success it returns the aggregate tree hash, the aggregate diff vs the
    /// originating branch, and any auto-resolutions that occurred. An empty
    /// <paramref name="childBranchesInOrder"/> (every child was a no-change <c>completed</c>) yields
    /// an empty-diff success.
    /// <para>Branch-ref only: the originating branch is never modified here; that happens later in the
    /// single collective merge.</para>
    /// </summary>
    public IntegrationBranchResult BuildIntegrationBranch(
        string repositoryPath,
        string originatingBranch,
        string integrationBranch,
        IReadOnlyList<string> childBranchesInOrder)
    {
        EnsurePrimaryWorktreeNotCheckedOutOnBranch(repositoryPath, originatingBranch, integrationBranch);

        // Defensive: a prior — or interrupted — assembly can leave a LINKED worktree checked out on
        // the integration branch, which makes the ref undeletable below ("Cannot delete branch ... as
        // it is the current HEAD of a linked repository"). The integration branch is built headlessly
        // and is never meant to be checked out, so prune any such stale worktree first so a re-run
        // (e.g. after request-changes re-dispatch) can reset the branch cleanly.
        PruneWorktreesCheckedOutOnBranch(repositoryPath, integrationBranch);

        using var repo = new Repository(repositoryPath);

        var origin = repo.Branches[originatingBranch]
            ?? throw new InvalidOperationException($"Originating branch '{originatingBranch}' was not found.");

        EnsureMainRepositoryNotCheckedOutOnBranch(repo, integrationBranch, origin);

        // Create/reset the integration branch ref at the originating branch tip.
        var existing = repo.Branches[integrationBranch];
        if (existing is not null)
            repo.Branches.Remove(existing);
        var intBranch = repo.CreateBranch(integrationBranch, origin.Tip);

        var integrationCommit = origin.Tip;
        var autoResolutions = new List<(string Branch, IReadOnlyList<string> Files)>();

        foreach (var childBranch in childBranchesInOrder)
        {
            var child = repo.Branches[childBranch];
            if (child?.Tip is null)
            {
                _logger.LogWarning(
                    "Integration build: child branch '{Branch}' not found or empty — skipping", childBranch);
                continue;
            }

            var mergeBase = repo.ObjectDatabase.FindMergeBase(integrationCommit, child.Tip);

            // Child is already contained in the integration branch — no-op.
            if (mergeBase is not null && string.Equals(mergeBase.Sha, child.Tip.Sha, StringComparison.Ordinal))
                continue;

            // Fast-forward: integration is an ancestor of the child tip.
            if (mergeBase is not null && string.Equals(mergeBase.Sha, integrationCommit.Sha, StringComparison.Ordinal))
            {
                integrationCommit = child.Tip;
                continue;
            }

            // 3-way headless tree merge.
            var merge = repo.ObjectDatabase.MergeCommits(integrationCommit, child.Tip, new MergeTreeOptions());
            if (merge.Status == MergeTreeStatus.Conflicts)
            {
                var conflictingFiles = ExtractConflictingFiles(merge);
                _logger.LogInformation(
                    "Integration build: auto-resolving {Count} conflict(s) from branch '{Branch}' by accepting child changes. Files: {Files}",
                    conflictingFiles.Count,
                    childBranch,
                    string.Join(", ", conflictingFiles));

                // TODO(issue-85): distinguish "single child amends another child's file" (safe to
                // auto-resolve) from true sibling-vs-sibling conflicts that should still surface as
                // IntegrationBranchOutcome.Conflict for human resolution.
                if (mergeBase is null)
                {
                    return IntegrationBranchResult.Conflict(
                        integrationBranch,
                        childBranch,
                        conflictingFiles,
                        "Unable to auto-resolve integration conflict because no merge base was found.");
                }

                var treeDefinition = TreeDefinition.From(integrationCommit.Tree);
                var childTree = child.Tip.Tree;
                var childChanges = repo.Diff.Compare<TreeChanges>(mergeBase.Tree, child.Tip.Tree);
                foreach (var change in childChanges)
                {
                    if (change.Status is ChangeKind.Deleted or ChangeKind.Renamed)
                        treeDefinition.Remove(change.OldPath ?? change.Path);

                    if (change.Status is ChangeKind.Deleted or ChangeKind.Unmodified)
                        continue;

                    var childEntry = childTree[change.Path];
                    if (childEntry?.TargetType == TreeEntryTargetType.Blob)
                    {
                        var childBlob = repo.Lookup<Blob>(childEntry.Target.Id);
                        if (childBlob is not null)
                            treeDefinition.Add(change.Path, childBlob, childEntry.Mode);
                    }
                }

                var resolvedTree = repo.ObjectDatabase.CreateTree(treeDefinition);
                var resolvedSignature = WithTimestamp();
                integrationCommit = repo.ObjectDatabase.CreateCommit(
                    resolvedSignature,
                    resolvedSignature,
                    $"Assemble {childBranch} into {integrationBranch} [auto-resolved {conflictingFiles.Count} conflict(s) — accepted child changes]",
                    resolvedTree,
                    new[] { integrationCommit, child.Tip },
                    prettifyMessage: true);

                autoResolutions.Add((childBranch, conflictingFiles));
                continue;
            }

            var signature = WithTimestamp();
            integrationCommit = repo.ObjectDatabase.CreateCommit(
                signature,
                signature,
                $"Assemble {childBranch} into {integrationBranch}",
                merge.Tree,
                new[] { integrationCommit, child.Tip },
                prettifyMessage: true);
        }

        // Point the integration branch ref at the final assembled commit. Under the shared-repo race
        // (issue #218) a concurrent build can delete+recreate this ref between its creation above and
        // here, so repo.Refs[intBranch.CanonicalName] may momentarily be null. Re-create the ref in that
        // case instead of dereferencing null (which surfaced as an ArgumentNullException from UpdateTarget).
        var intRef = repo.Refs[intBranch.CanonicalName];
        if (intRef is null)
            repo.Refs.Add(intBranch.CanonicalName, integrationCommit.Id, allowOverwrite: true);
        else
            repo.Refs.UpdateTarget(intRef, integrationCommit.Id);

        using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, integrationCommit.Tree);
        return IntegrationBranchResult.Success(
            integrationBranch,
            integrationCommit.Tree.Sha,
            patch.Content,
            autoResolutions);
    }

    private static string SanitizeWorktreeName(string name)
    {
        var cleaned = Regex.Replace(name, @"[^A-Za-z0-9_.-]+", "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(cleaned)
            ? "agentweaver-worktree-" + Guid.NewGuid().ToString("N")
            : cleaned;
    }

    private void RunGit(string repositoryPath, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0)
            return;

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var commandSummary = args.Length >= 2 ? $"{args[0]} {args[1]}" : string.Join(' ', args);
        throw new InvalidOperationException(
            $"git {commandSummary} failed in '{repositoryPath}' with exit code {process.ExitCode}: {detail.Trim()}");
    }

    private void EnsurePrimaryWorktreeNotCheckedOutOnBranch(
        string repositoryPath,
        string fallbackBranch,
        string branchName)
    {
        try
        {
            using var repo = new Repository(repositoryPath);
            if (repo.Info.IsHeadDetached || !string.Equals(repo.Head.FriendlyName, branchName, StringComparison.Ordinal))
                return;

            var fallback = repo.Branches[fallbackBranch]
                ?? throw new InvalidOperationException($"Fallback branch '{fallbackBranch}' was not found.");

            _logger.LogWarning(
                "Primary repository worktree is checked out on generated integration branch '{Branch}'. " +
                "Checking out '{Fallback}' before resetting the integration ref.",
                branchName,
                fallbackBranch);

            try
            {
                Commands.Checkout(repo, fallback);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Safe checkout from generated integration branch '{Branch}' to '{Fallback}' failed; retrying with force",
                    branchName,
                    fallbackBranch);
                Commands.Checkout(repo, fallback, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not move primary repository off integration branch '{Branch}' before reset",
                branchName);
        }
    }

    /// <summary>
    /// Deletes stale git ref lock files for <paramref name="integrationBranch"/>. A lock file is
    /// left behind when a previous process died mid-ref-write; LibGit2Sharp's next write attempt
    /// throws <see cref="LibGit2Sharp.LockedFileException"/> ("failed to create locked file ...
    /// .lock: File exists"). This method removes:
    /// <list type="bullet">
    /// <item><c>.git/refs/heads/&lt;branch&gt;.lock</c> — the per-ref lock file.</item>
    /// <item><c>.git/packed-refs.lock</c> — the shared packed-refs lock file.</item>
    /// </list>
    /// Best-effort: logs on failure and never throws. Intended to be called before a retry of
    /// <see cref="BuildIntegrationBranch"/> after a <see cref="LibGit2Sharp.LockedFileException"/>.
    /// </summary>
    internal void TryCleanIntegrationLockFiles(string repositoryPath, string integrationBranch)
    {
        try
        {
            var gitDir = Path.Combine(repositoryPath, ".git");

            // Per-ref lock file: .git/refs/heads/<branch>.lock
            var refRelPath = integrationBranch.Replace('/', Path.DirectorySeparatorChar);
            var refLockPath = Path.Combine(gitDir, "refs", "heads", refRelPath) + ".lock";
            if (TryDeleteStaleLock(refLockPath))
                _logger.LogInformation(
                    "WorktreeManager: deleted stale ref lock file for integration branch {Branch}",
                    integrationBranch);

            // Packed-refs lock (shared across all refs): .git/packed-refs.lock
            var packedRefsLock = Path.Combine(gitDir, "packed-refs.lock");
            if (TryDeleteStaleLock(packedRefsLock))
                _logger.LogInformation(
                    "WorktreeManager: deleted stale packed-refs lock file in repository {Path}",
                    repositoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorktreeManager: failed to clean stale git lock files for integration branch {Branch} in {Path} (best-effort)",
                integrationBranch, repositoryPath);
        }
    }

    /// <summary>
    /// Deletes <paramref name="lockPath"/> ONLY if it exists and its last-write time is older than
    /// <see cref="_staleLockThreshold"/> (i.e. it is genuinely stale, not an actively-held lock).
    /// Returns true when a file was deleted. Never throws.
    /// </summary>
    private bool TryDeleteStaleLock(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
                return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath);
            if (age < _staleLockThreshold)
            {
                _logger.LogDebug(
                    "WorktreeManager: git lock file {Path} is only {AgeMs}ms old; not deleting (likely held by an active operation)",
                    lockPath, (int)age.TotalMilliseconds);
                return false;
            }

            File.Delete(lockPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorktreeManager: failed to delete git lock file {Path} (best-effort)", lockPath);
            return false;
        }
    }

    /// <summary>
    /// Best-effort retry preparation for integration-branch writes: remove stale git lock files,
    /// prune any linked worktree checked out on the integration branch, and run
    /// <c>git worktree prune</c> against the repository to drop stale admin entries.
    /// Never throws.
    /// </summary>
    internal void TryCleanIntegrationRetryArtifacts(string repositoryPath, string integrationBranch)
    {
        TryCleanIntegrationLockFiles(repositoryPath, integrationBranch);
        TryCleanRepositoryIndexLock(repositoryPath);
        PruneWorktreesCheckedOutOnBranch(repositoryPath, integrationBranch);
        TryRunGitWorktreePrune(repositoryPath);
    }

    private void TryCleanRepositoryIndexLock(string repositoryPath)
    {
        try
        {
            var indexLockPath = Path.Combine(repositoryPath, ".git", "index.lock");
            if (!File.Exists(indexLockPath))
                return;

            File.Delete(indexLockPath);
            _logger.LogWarning(
                "WorktreeManager: deleted stale index lock file in repository {Path}",
                repositoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorktreeManager: failed to clean stale index lock file in repository {Path} (best-effort)",
                repositoryPath);
        }
    }

    /// <summary>
    /// Conservatively clears a STALE <c>index.lock</c> for a run's worktree between post-turn commit
    /// retries (the in-place-revision wedge: a lingering/crashed process left the index locked). This
    /// is the age-checked pattern used by <see cref="TryDeleteStaleLock"/> — NOT the unconditional
    /// direct-delete anti-pattern — extended to resolve the ACTUAL gitdir for LINKED worktrees (their
    /// <c>.git</c> is a pointer file, and the per-worktree index.lock lives under the resolved gitdir,
    /// not <c>worktreePath/.git/index.lock</c>).
    /// <para>
    /// The SOLE guard is the configurable AGE threshold (<c>Coordinator:StaleLockThresholdSeconds</c>,
    /// default 15s). We deliberately do NOT consult a host-global <c>git</c> process list: our own
    /// commits use IN-PROCESS LibGit2Sharp (no subprocess), while a busy coordinator almost always has
    /// SOME unrelated <c>git</c> process running (our own <c>git worktree add/prune</c> subprocesses,
    /// agents invoking git as a tool). A global check would therefore refuse the clear in exactly the
    /// concurrent scenario Fix-A #1 targets, re-wedging commit-retry. A lock older than the threshold
    /// with no in-process git operation is safe to clear.
    /// </para>
    /// Never throws; returns diagnostics for the child-turn-failed evidence trail. The
    /// <c>LiveGitProcessDetected</c> evidence field is retained for contract stability and is always
    /// <c>false</c> under the age-only guard.
    /// </summary>
    public IndexLockClearResult ClearStaleIndexLock(string worktreePath)
    {
        try
        {
            var gitDir = ResolveGitDir(worktreePath);
            if (gitDir is null)
                return new IndexLockClearResult(false, false, null, false, null, "gitdir_unresolved");

            var lockPath = Path.Combine(gitDir, "index.lock");
            if (!File.Exists(lockPath))
                return new IndexLockClearResult(false, false, null, false, lockPath, "no_lock_present");

            var ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath)).TotalSeconds;

            // The AGE gate is the sole guard. A lock younger than the configurable stale threshold is
            // presumed actively held by a concurrent operation — refuse. A lock OLDER than the
            // threshold is safe to clear: our own commits go through IN-PROCESS LibGit2Sharp (no git
            // subprocess), so nothing of ours legitimately holds an index.lock for longer than the
            // threshold. (We deliberately do NOT consult a host-global `git` process list — on a busy
            // coordinator our own `git worktree add/prune` subprocesses and agent git-tool invocations
            // mean a `git` process is almost always present, which would make the clear NEVER fire in
            // exactly the concurrent scenario Fix-A #1 targets and re-wedge commit-retry. See
            // decision note morpheus-fixa-inplace-terminal-design.md.)
            if (ageSeconds < _staleLockThreshold.TotalSeconds)
            {
                _logger.LogDebug(
                    "WorktreeManager: index.lock {Path} is only {AgeSec:F1}s old (< {ThresholdSec:F0}s); refusing to clear (likely active)",
                    lockPath, ageSeconds, _staleLockThreshold.TotalSeconds);
                return new IndexLockClearResult(true, false, ageSeconds, false, lockPath, "lock_too_recent");
            }

            File.Delete(lockPath);
            _logger.LogWarning(
                "WorktreeManager: cleared stale index.lock {Path} (age {AgeSec:F1}s) before post-turn commit retry",
                lockPath, ageSeconds);
            return new IndexLockClearResult(true, true, ageSeconds, false, lockPath, "cleared");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorktreeManager: best-effort stale index.lock clear failed for worktree {Path}", worktreePath);
            return new IndexLockClearResult(false, false, null, false, null, $"error:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Resolves the real gitdir for a worktree. A MAIN worktree has a <c>.git</c> DIRECTORY; a LINKED
    /// worktree has a <c>.git</c> FILE containing <c>gitdir: &lt;abs-or-rel path&gt;</c>. Returns null
    /// when neither is found.
    /// </summary>
    private static string? ResolveGitDir(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(dotGit))
            return dotGit;
        if (File.Exists(dotGit))
        {
            const string prefix = "gitdir:";
            foreach (var rawLine in File.ReadAllLines(dotGit))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var pointer = line[prefix.Length..].Trim();
                if (pointer.Length == 0)
                    return null;
                return Path.IsPathRooted(pointer)
                    ? Path.GetFullPath(pointer)
                    : Path.GetFullPath(Path.Combine(worktreePath, pointer));
            }
        }
        return null;
    }

    private void TryRunGitWorktreePrune(string repositoryPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("worktree");
            process.StartInfo.ArgumentList.Add("prune");

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
                return;

            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            _logger.LogWarning(
                "WorktreeManager: git worktree prune failed in repository {Path} with exit code {ExitCode}: {Message}",
                repositoryPath,
                process.ExitCode,
                detail.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorktreeManager: git worktree prune failed in repository {Path} (best-effort)",
                repositoryPath);
        }
    }

    /// <summary>
    /// Guardrail for a failed/aborted build-test agent that checked out the generated integration
    /// branch in the main repository instead of a linked worktree. LibGit2Sharp refuses to delete a
    /// branch that is the current HEAD; the integration branch is Agentweaver-owned scratch state, so
    /// force the main worktree back to the originating branch before resetting the integration ref.
    /// </summary>
    private void EnsureMainRepositoryNotCheckedOutOnBranch(
       Repository repo,
       string integrationBranch,
       Branch fallbackBranch)
    {
       if (repo.Info.IsHeadDetached
           || !string.Equals(repo.Head.FriendlyName, integrationBranch, StringComparison.Ordinal))
           return;

       _logger.LogWarning(
           "WorktreeManager: main repository was checked out on integration branch {Branch}; " +
           "checking out {FallbackBranch} so the integration branch can be reset",
           integrationBranch,
           fallbackBranch.FriendlyName);

       Commands.Checkout(repo, fallbackBranch, new CheckoutOptions
       {
           CheckoutModifiers = CheckoutModifiers.Force,
       });
    }

    /// <summary>
    /// Prunes the linked-worktree admin entry with the given <paramref name="worktreeName"/>
    /// (the label stored in <c>.git/worktrees/&lt;worktreeName&gt;/</c>) without requiring the
    /// physical worktree directory to exist. This is the correct variant to call during restart
    /// recovery, where the physical directory has been wiped from ephemeral storage but the
    /// git admin entry persists on the PVC — <see cref="PruneWorktreesCheckedOutOnBranch"/> cannot
    /// be used in that scenario because it must open the worktree's repository to inspect its HEAD.
    /// When LibGit2Sharp's Lookup rejects the entry (because git_worktree_validate fails for a
    /// stale entry whose physical directory is gone), falls back to direct filesystem deletion of
    /// the <c>.git/worktrees/&lt;name&gt;/</c> admin directory — which is exactly what
    /// <c>git worktree prune</c> does internally. Best-effort: logs and returns on failure.
    /// </summary>
    private void PruneWorktreeByName(string repositoryPath, string worktreeName)
    {
        // First try LibGit2Sharp's built-in prune path (works when the worktree entry is "valid"
        // per git_worktree_validate — i.e. the physical directory still exists).
        try
        {
            using var repo = new Repository(repositoryPath);
            var wt = repo.Worktrees[worktreeName];
            if (wt is not null)
            {
                _logger.LogWarning(
                    "Pruning stale git admin entry for worktree '{WorktreeName}' (via LibGit2Sharp)",
                    worktreeName);
                repo.Worktrees.Prune(wt, true);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LibGit2Sharp prune failed for worktree '{WorktreeName}'; falling back to direct deletion",
                worktreeName);
        }

        // Fallback: directly delete the admin directory. This mirrors what `git worktree prune`
        // does — it removes .git/worktrees/<name>/ for any worktree whose physical path is gone.
        // LibGit2Sharp's Lookup returns null for stale entries because git_worktree_validate()
        // checks both the admin dir and the physical path, so the API path above is unavailable
        // when the pod-restart wipe is exactly what we need to clean up.
        var adminDir = Path.Combine(repositoryPath, ".git", "worktrees", worktreeName);
        if (Directory.Exists(adminDir))
        {
            try
            {
                Directory.Delete(adminDir, recursive: true);
                _logger.LogWarning(
                    "Deleted stale worktree admin directory '{AdminDir}' (direct fallback)",
                    adminDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete stale worktree admin directory '{AdminDir}'",
                    adminDir);
            }
        }
        else
        {
            _logger.LogDebug(
                "No stale git admin entry found for worktree '{WorktreeName}'; nothing to prune",
                worktreeName);
        }
    }

    /// <summary>
    /// Removes the orphaned branch <c>refs/heads/<paramref name="branchName"/></c> that
    /// <c>git_worktree_add</c> creates as a side-effect when a worktree is provisioned via
    /// <see cref="WorktreeCollection.Add(string, string, string, bool)"/>. The branch is a
    /// throw-away: all commits go to the run's real branch (<c>agentweaver/&lt;runId&gt;</c>).
    /// Without removing it, re-creating the worktree after a pod restart fails with
    /// <see cref="LibGit2Sharp.NameConflictException"/> because <c>git_worktree_add</c> tries to
    /// create the same branch again. Best-effort — logs and returns on failure.
    /// </summary>
    private void DeleteOrphanedWorktreeBranch(string repositoryPath, string branchName)
    {
        try
        {
            using var repo = new Repository(repositoryPath);
            var branch = repo.Branches[branchName];
            if (branch is null) return;
            repo.Branches.Remove(branch);
            _logger.LogDebug(
                "Removed orphaned throw-away worktree branch '{BranchName}'", branchName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to remove orphaned worktree branch '{BranchName}'; " +
                "git worktree add may still fail with a NameConflictException",
                branchName);
        }
    }

    /// <summary>
    /// Prunes any LINKED worktree currently checked out on <paramref name="branchName"/> so the branch
    /// ref can be deleted or reset. Mirrors the robust delete-dir → prune sequence used by
    /// <see cref="RemoveWorktree"/>: deleting the physical directory makes the admin entry prunable,
    /// and the prune itself releases libgit2's branch-checked-out lock. Best-effort — every step logs
    /// and continues on failure so a transient inspection error never aborts the assembly. A no-op
    /// when no linked worktree references the branch (the normal headless case).
    /// </summary>
    private void PruneWorktreesCheckedOutOnBranch(string repositoryPath, string branchName)
    {
        // Phase 1: identify linked worktrees on the branch (capture admin name + physical path).
        var targets = new List<(string Name, string? Path)>();
        try
        {
            using var repo = new Repository(repositoryPath);
            foreach (var wt in repo.Worktrees)
            {
                try
                {
                    using var wtRepo = wt.WorktreeRepository;
                    var head = wtRepo.Info.IsHeadDetached ? null : wtRepo.Head.FriendlyName;
                    if (string.Equals(head, branchName, StringComparison.Ordinal))
                        targets.Add((wt.Name, wtRepo.Info.WorkingDirectory));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not inspect linked worktree '{Name}' while resolving integration branch '{Branch}'",
                        wt.Name, branchName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not enumerate worktrees while resolving integration branch '{Branch}'", branchName);
            return;
        }

        // Phase 2: for each match, delete the physical dir then prune the admin entry (fresh handle).
        foreach (var (name, path) in targets)
        {
            _logger.LogWarning(
                "Pruning stale linked worktree '{Name}' checked out on integration branch '{Branch}'",
                name, branchName);

            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete worktree directory '{Path}'", path); }
            }

            try
            {
                using var pruneRepo = new Repository(repositoryPath);
                var wt = pruneRepo.Worktrees[name];
                if (wt is not null)
                    pruneRepo.Worktrees.Prune(wt, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune stale worktree '{Name}'", name);
            }
        }
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
            // Skip mode-only changes (identical blob content, e.g. an executable-bit flip that a
            // cross-platform headless merge introduces). These are the "+0 -0" phantom rows the run
            // never actually produced (issue #197 symptom C).
            if (IsContentIdenticalModeChange(change)) continue;
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
    /// True when a tree change has identical blob content on both sides (same <see cref="TreeEntryChanges.Oid"/>
    /// as <see cref="TreeEntryChanges.OldOid"/>) — i.e. only the file mode changed. Such entries carry no
    /// line additions/removals and must be excluded from a run's changed-file set so the Changes/Files tab
    /// does not surface "+0 -0" phantom rows for files the run never actually modified (issue #197).
    /// </summary>
    private static bool IsContentIdenticalModeChange(TreeEntryChanges change)
        => change.Status == ChangeKind.Modified
           && change.Oid == change.OldOid
           && change.Oid != null
           && !change.Oid.Equals(ObjectId.Zero);

    /// <summary>
    /// Reads a single file's content from a durable git source (a run's committed worktree branch tip,
    /// or an explicit merged commit hash) WITHOUT needing the ephemeral worktree directory on disk.
    /// Used by the file Preview endpoint so a file a child subtask created remains previewable after
    /// its sandbox/worktree has been torn down (issue #197 symptom C — no more 409 "Worktree not
    /// available" for a file the run demonstrably produced). Returns null when the repository, branch,
    /// commit, or file path cannot be resolved; sets <paramref name="isBinary"/> when the blob is binary.
    /// </summary>
    public WorkspaceFileContent? TryReadCommittedFileContent(
        string repositoryPath,
        string? worktreeBranch,
        string? commitHash,
        string relativeFilePath,
        out bool isBinary)
    {
        isBinary = false;
        if (string.IsNullOrEmpty(repositoryPath) || !Repository.IsValid(repositoryPath))
            return null;

        using var repo = new Repository(repositoryPath);

        Commit? commit = null;
        if (!string.IsNullOrEmpty(commitHash))
            commit = repo.Lookup<Commit>(commitHash);
        if (commit is null && !string.IsNullOrEmpty(worktreeBranch))
            commit = repo.Branches[worktreeBranch]?.Tip;
        if (commit is null)
            return null;

        var gitPath = relativeFilePath.Replace('\\', '/');
        var treeEntry = commit[gitPath];
        if (treeEntry is null || treeEntry.TargetType != TreeEntryTargetType.Blob)
            return null;

        var blob = (Blob)treeEntry.Target;
        isBinary = blob.IsBinary;
        return EndpointBlobContent(blob, NormalizePathSeparators(relativeFilePath));
    }

    private static WorkspaceFileContent EndpointBlobContent(Blob blob, string path)
    {
        if (blob.IsBinary)
        {
            return new WorkspaceFileContent
            {
                Path     = path,
                Content  = null,
                IsBinary = true,
                Language = DetectLanguageFromPath(path),
            };
        }

        const long maxContentBytes = 1 * 1024 * 1024; // 1 MB — mirrors the filesystem content endpoint.
        if (blob.Size > maxContentBytes)
        {
            return new WorkspaceFileContent
            {
                Path     = path,
                Content  = null,
                IsBinary = false,
                Language = "too_large",
            };
        }

        return new WorkspaceFileContent
        {
            Path     = path,
            Content  = blob.GetContentText(),
            IsBinary = false,
            Language = DetectLanguageFromPath(path),
        };
    }

    private static string DetectLanguageFromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".cs"           => "csharp",
            ".py"           => "python",
            ".go"           => "go",
            ".rs"           => "rust",
            ".java"         => "java",
            ".json"         => "json",
            ".md"           => "markdown",
            ".html" or ".htm" => "html",
            ".css"          => "css",
            ".yml" or ".yaml" => "yaml",
            ".xml"          => "xml",
            ".sh"           => "shell",
            _               => "plaintext",
        };
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
            if (IsContentIdenticalModeChange(change)) continue;
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
            $"Merge agentweaver run into {originatingBranch}",
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
            $"Merge agentweaver run into {originatingBranch}",
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
