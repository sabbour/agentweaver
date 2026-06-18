using System.Diagnostics;
using LibGit2Sharp;

namespace Agentweaver.Tests.Casting;

/// <summary>
/// FR-027: Merge-safe state — .squad event sidecars must survive two-branch concurrent edits
/// with no data loss when merged using the union merge driver.
///
/// NOTE: The merge=union driver is a git-binary feature. LibGit2Sharp (libgit2) does not
/// honour .gitattributes custom merge drivers — it uses its own merge algorithm and would
/// pick one side. These tests therefore invoke the real git binary for the merge step.
/// If git is not on PATH the tests are skipped.
/// </summary>
public sealed class MergeSafeStateTests : IDisposable
{
    private readonly string _root;
    private static readonly bool _gitAvailable = IsGitAvailable();

    public MergeSafeStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"merge-safe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TwoBranchEdits_RegistryEventsSidecar_MergeCleanly_NoDataLoss()
    {
        // merge=union is a git-binary feature; skip gracefully if git is not on PATH.
        if (!_gitAvailable) return;

        var repoDir = Path.Combine(_root, "registry-merge");
        InitRepoWithSquad(repoDir);

        var sidecarRel  = Path.Combine(".squad", "casting", "registry.events.jsonl");
        var sidecarPath = Path.Combine(repoDir, sidecarRel);

        // Branch A: append one event.
        Git(repoDir, "checkout -b branch-a");
        AppendJsonlLine(sidecarPath, """{"event":"cast","agent":"alpha","ts":"2026-01-02T00:00:00Z"}""");
        Git(repoDir, "add .");
        Git(repoDir, """commit -m "Branch A: add cast event" """);

        // Return to main, branch B: append a different event.
        Git(repoDir, "checkout main");
        Git(repoDir, "checkout -b branch-b");
        AppendJsonlLine(sidecarPath, """{"event":"retire","agent":"beta","ts":"2026-01-03T00:00:00Z"}""");
        Git(repoDir, "add .");
        Git(repoDir, """commit -m "Branch B: add retire event" """);

        // Merge branch-a into branch-b using the real git binary (honours merge=union).
        Git(repoDir, "checkout branch-a");
        var (exitCode, output) = GitWithOutput(repoDir, "merge branch-b --no-edit");

        Assert.True(exitCode == 0, $"git merge failed (exit {exitCode}):\n{output}");

        var mergedLines = File
            .ReadAllLines(sidecarPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Both events must survive the union merge.
        Assert.Equal(2, mergedLines.Count);
        Assert.Contains(mergedLines, l => l.Contains("cast",   StringComparison.Ordinal));
        Assert.Contains(mergedLines, l => l.Contains("retire", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoBranchEdits_HistoryEventsSidecar_MergeCleanly_NoDataLoss()
    {
        if (!_gitAvailable) return;

        var repoDir = Path.Combine(_root, "history-merge");
        InitRepoWithSquad(repoDir);

        var sidecarPath = Path.Combine(repoDir, ".squad", "casting", "history.events.jsonl");

        Git(repoDir, "checkout -b branch-a");
        AppendJsonlLine(sidecarPath, """{"event":"cast","snapshot":"v1","ts":"2026-01-02T00:00:00Z"}""");
        Git(repoDir, "add .");
        Git(repoDir, """commit -m "Branch A: history event 1" """);

        Git(repoDir, "checkout main");
        Git(repoDir, "checkout -b branch-b");
        AppendJsonlLine(sidecarPath, """{"event":"sync","snapshot":"v2","ts":"2026-01-03T00:00:00Z"}""");
        Git(repoDir, "add .");
        Git(repoDir, """commit -m "Branch B: history event 2" """);

        Git(repoDir, "checkout branch-a");
        var (exitCode, output) = GitWithOutput(repoDir, "merge branch-b --no-edit");

        Assert.True(exitCode == 0, $"git merge failed (exit {exitCode}):\n{output}");

        var mergedLines = File
            .ReadAllLines(sidecarPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        Assert.Equal(2, mergedLines.Count);
        Assert.Contains(mergedLines, l => l.Contains("cast", StringComparison.Ordinal));
        Assert.Contains(mergedLines, l => l.Contains("sync", StringComparison.Ordinal));
    }

    [Fact(Skip = "Activate once SquadWriter.RegenerateCanonicalJson() is plumbed through CastingService integration tests")]
    public void CanonicalJson_RegeneratedFromMergedSidecars_IsValidAndComplete()
    {
        // After merging two branches whose registry.events.jsonl lines were unioned,
        // SquadWriter.RegenerateCanonicalJson() must produce a valid registry.json
        // whose agent list contains every agent from both branches' sidecar events.
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void InitRepoWithSquad(string directory)
    {
        Directory.CreateDirectory(directory);
        SquadTestFixtureHelper.CreateMinimalSquad(directory);

        var castingDir = Path.Combine(directory, ".squad", "casting");
        Directory.CreateDirectory(castingDir);
        // Empty sidecars must exist in the common ancestor for union merge to work.
        File.WriteAllText(Path.Combine(castingDir, "registry.events.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(castingDir, "history.events.jsonl"),  string.Empty);

        // .gitattributes with union driver must be in the common ancestor commit.
        File.WriteAllText(
            Path.Combine(directory, ".squad", ".gitattributes"),
            "*.events.jsonl merge=union\n");

        // Configure a local git identity so commits work in CI without a global config.
        Git(directory, "init -b main");
        Git(directory, """config user.email "test@localhost" """);
        Git(directory, """config user.name "Test" """);
        Git(directory, "add .");
        Git(directory, """commit -m "Initial squad commit" """);
    }

    private static void AppendJsonlLine(string path, string line)
        => File.AppendAllText(path, line + "\n");

    private static void Git(string workingDir, string arguments)
    {
        var (code, output) = GitWithOutput(workingDir, arguments);
        if (code != 0)
            throw new InvalidOperationException($"git {arguments} failed (exit {code}):\n{output}");
    }

    private static (int ExitCode, string Output) GitWithOutput(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout + stderr);
    }

    private static bool IsGitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
