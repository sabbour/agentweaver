using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;

namespace Agentweaver.Tests.Git;

/// <summary>
/// Direct <see cref="WorktreeManager.MergeWorktree"/> tests for the issue #621 fix: canonical Squad
/// bookkeeping ledgers (<c>.squad/decisions.md</c>, <c>.squad/agents/*/history.md</c>,
/// <c>.squad/identity/now.md</c>) are resolved path-level "ours" during a per-run branch merge, so a
/// run's stale/racing copy of those centrally-consolidated files can never produce a
/// human-resolution-required conflict — while genuine conflicts on every OTHER path are still detected
/// exactly as before.
///
/// No mocks: each test builds a real on-disk git repository with LibGit2Sharp, diverges the
/// originating branch and the run branch on the SAME lines of a file, and calls
/// <see cref="WorktreeManager.MergeWorktree"/> directly.
/// </summary>
public sealed class SquadStateMergeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort; git packs may still be locked */ }
        }
    }

    // =========================================================================
    // Bookkeeping-only divergence on decisions.md that WOULD be a genuine 3-way
    // conflict → now merges cleanly, keeping the originating branch's version.
    // =========================================================================
    [Fact]
    public void MergeWorktree_BookkeepingOnlyDivergence_MergesCleanly_KeepingOursLedger()
    {
        var (repoPath, manager) = CreateRepo();
        string worktreeTreeHash;

        using (var repo = new Repository(repoPath))
        {
            // C0: base with a decisions ledger.
            WriteFile(repoPath, ".squad/decisions.md", "# Squad Decisions\n\n## base\n");
            CommitAll(repo, "base");

            // Run branch diverges: appends its own line to the SAME ledger.
            var runBranch = repo.CreateBranch("agentweaver/run", repo.Head.Tip);
            Commands.Checkout(repo, runBranch);
            WriteFile(repoPath, ".squad/decisions.md", "# Squad Decisions\n\n## base\n\n## from-run\n");
            worktreeTreeHash = CommitAll(repo, "run scribe write");

            // Back on main, the consolidation service (simulated) has already advanced the SAME
            // ledger lines differently — this is exactly the racing-divergence that conflicts today.
            Commands.Checkout(repo, repo.Branches["main"]);
            WriteFile(repoPath, ".squad/decisions.md", "# Squad Decisions\n\n## base\n\n## consolidated-on-default\n");
            CommitAll(repo, "consolidated on default");
        }

        var outcome = manager.MergeWorktree(repoPath, "main", "agentweaver/run", worktreeTreeHash);

        outcome.Kind.Should().Be(MergeOutcomeKind.Merged,
            "a divergence confined to the centrally-consolidated Squad ledger must never be a " +
            "human-resolution-required conflict (issue #621)");

        using (var repo = new Repository(repoPath))
        {
            var merged = ReadTreeText(repo.Branches["main"]!.Tip.Tree, ".squad/decisions.md");
            merged.Should().Contain("consolidated-on-default",
                "the originating branch's (ours) ledger version must be preserved");
            merged.Should().NotContain("from-run",
                "the run branch's racing ledger write must NOT clobber the consolidated content");
        }
    }

    // =========================================================================
    // Per-agent history.md also resolves ours (Scribe cross-agent appends race
    // across runs exactly like decisions.md).
    // =========================================================================
    [Fact]
    public void MergeWorktree_AgentHistoryDivergence_MergesCleanly_KeepingOursLedger()
    {
        var (repoPath, manager) = CreateRepo();
        string worktreeTreeHash;

        using (var repo = new Repository(repoPath))
        {
            WriteFile(repoPath, ".squad/agents/scribe/history.md", "# History\n\n- base\n");
            CommitAll(repo, "base");

            var runBranch = repo.CreateBranch("agentweaver/run", repo.Head.Tip);
            Commands.Checkout(repo, runBranch);
            WriteFile(repoPath, ".squad/agents/scribe/history.md", "# History\n\n- base\n- run-learning\n");
            worktreeTreeHash = CommitAll(repo, "run history write");

            Commands.Checkout(repo, repo.Branches["main"]);
            WriteFile(repoPath, ".squad/agents/scribe/history.md", "# History\n\n- base\n- default-learning\n");
            CommitAll(repo, "default history write");
        }

        var outcome = manager.MergeWorktree(repoPath, "main", "agentweaver/run", worktreeTreeHash);

        outcome.Kind.Should().Be(MergeOutcomeKind.Merged);
        using (var repo = new Repository(repoPath))
        {
            var merged = ReadTreeText(repo.Branches["main"]!.Tip.Tree, ".squad/agents/scribe/history.md");
            merged.Should().Contain("default-learning");
            merged.Should().NotContain("run-learning");
        }
    }

    // =========================================================================
    // A REAL conflict on a non-bookkeeping path must STILL be reported as a
    // conflict — the fix must not weaken genuine conflict detection.
    // =========================================================================
    [Fact]
    public void MergeWorktree_GenuineConflictOnNonBookkeepingPath_StillConflicts()
    {
        var (repoPath, manager) = CreateRepo();
        string worktreeTreeHash;

        using (var repo = new Repository(repoPath))
        {
            WriteFile(repoPath, "app.txt", "v0\n");
            WriteFile(repoPath, ".squad/decisions.md", "# Squad Decisions\n\n## base\n");
            CommitAll(repo, "base");

            var runBranch = repo.CreateBranch("agentweaver/run", repo.Head.Tip);
            Commands.Checkout(repo, runBranch);
            WriteFile(repoPath, "app.txt", "run-change\n");
            // Also touch the ledger to prove neutralization doesn't mask the real app.txt conflict.
            WriteFile(repoPath, ".squad/decisions.md", "# Squad Decisions\n\n## base\n\n## from-run\n");
            worktreeTreeHash = CommitAll(repo, "run change");

            Commands.Checkout(repo, repo.Branches["main"]);
            WriteFile(repoPath, "app.txt", "master-change\n");
            CommitAll(repo, "master change");
        }

        var outcome = manager.MergeWorktree(repoPath, "main", "agentweaver/run", worktreeTreeHash);

        outcome.Kind.Should().Be(MergeOutcomeKind.Conflict,
            "a genuine content conflict on a non-Squad path must still require human resolution");
        outcome.ConflictingFiles.Should().Contain("app.txt");
        outcome.ConflictingFiles.Should().NotContain(".squad/decisions.md",
            "the neutralized ledger must not appear as a conflicting path");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private (string repoPath, WorktreeManager manager) CreateRepo()
    {
        var repoPath = MakeTempDir("repo");
        var basePath = MakeTempDir("worktrees");

        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            // A throwaway initial commit so branch "main" exists deterministically.
            WriteFile(repoPath, ".gitkeep", "");
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            Commands.Stage(repo, "*");
            repo.Commit("init", sig, sig);
            if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
                repo.Branches.Rename(repo.Head, "main");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = basePath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
            })
            .Build();

        var manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        return (repoPath, manager);
    }

    private static string CommitAll(Repository repo, string message)
    {
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var commit = repo.Commit(message, sig, sig);
        return commit.Tree.Sha;
    }

    private static void WriteFile(string repoPath, string relativePath, string content)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string ReadTreeText(Tree tree, string path)
    {
        var entry = tree[path];
        if (entry?.Target is not Blob blob) return string.Empty;
        using var stream = blob.GetContentStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-squad621-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
