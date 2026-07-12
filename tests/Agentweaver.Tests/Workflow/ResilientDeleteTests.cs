using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;

namespace Agentweaver.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="WorktreeManager.DeleteDirectoryResilient"/> — the bounded-retry
/// recursive delete that absorbs the Azure Files SMB <c>Directory not empty</c> (ENOTEMPTY)
/// eventual-consistency window (issue #243). All fault injection uses the
/// <see cref="WorktreeManager.DeleteAttemptOverride"/> test seam — NO real SMB / no real filesystem
/// race — so the tests are portable and deterministic.
/// </summary>
public sealed class ResilientDeleteTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    ClearReadOnly(dir);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Test 1: read-only file tree deletes where a bare Directory.Delete throws (Windows).
    // -------------------------------------------------------------------------
    [Fact]
    public void Delete_ReadOnlyFileTree_Succeeds()
    {
        var (basePath, manager) = CreateManager();
        var dir = MakeDirUnder(basePath, "readonly");
        var file = Path.Combine(dir, "artifact.node");
        File.WriteAllText(file, "native build output");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        // No override — exercise the REAL delete. On Windows the bare top-down delete throws
        // UnauthorizedAccessException on the read-only file (attempt 1); the retry clears the
        // read-only bit and succeeds. On Linux the first unlink already succeeds.
        var act = () => manager.DeleteDirectoryResilient(dir);

        act.Should().NotThrow();
        Directory.Exists(dir).Should().BeFalse("the resilient delete must clear read-only attrs and remove the tree");
    }

    // -------------------------------------------------------------------------
    // Test 2: a transient ENOTEMPTY on the first attempts is retried, then succeeds.
    // -------------------------------------------------------------------------
    [Fact]
    public void Delete_TransientIOException_ThenSucceeds()
    {
        var (basePath, manager) = CreateManager();
        var dir = MakeDirUnder(basePath, "transient");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "x");

        var calls = 0;
        manager.DeleteAttemptOverride = (path, _) =>
        {
            calls++;
            if (calls <= 2)
                throw new IOException($"Directory not empty : '{path}'");
            Directory.Delete(path, recursive: true); // SMB metadata converged on the 3rd attempt
        };

        var act = () => manager.DeleteDirectoryResilient(dir);

        act.Should().NotThrow("the helper must retry past the transient SMB eventual-consistency window");
        calls.Should().Be(3, "attempts 1-2 fail transiently, attempt 3 succeeds");
        Directory.Exists(dir).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 3: a persistent failure rethrows after exactly N attempts and NEVER silently succeeds.
    // -------------------------------------------------------------------------
    [Fact]
    public void Delete_PersistentFailure_RethrowsAfterBounds()
    {
        var (basePath, manager) = CreateManager();
        var dir = MakeDirUnder(basePath, "persistent");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "x");

        var flags = new List<bool>();
        manager.DeleteAttemptOverride = (path, bottomUp) =>
        {
            flags.Add(bottomUp);
            throw new IOException($"Directory not empty : '{path}'");
        };

        var act = () => manager.DeleteDirectoryResilient(dir);

        act.Should().Throw<IOException>("a directory that never clears must rethrow, not silently return");
        flags.Should().Equal(new[] { false, false, false, true },
            "4 bounded attempts run; the top-down fast path is used first, the final attempt escalates to bottom-up");
        Directory.Exists(dir).Should().BeTrue("no-silent-success: the directory still exists, so the helper must have thrown");
    }

    // -------------------------------------------------------------------------
    // Test 4: on the final attempt the deepest-first bottom-up sweep removes a deep native tree.
    // -------------------------------------------------------------------------
    [Fact]
    public void Delete_BottomUp_RemovesDeepNestedTree()
    {
        var (basePath, manager) = CreateManager();
        // Mimic backend/node_modules/better-sqlite3/build/Release/obj/gen/sqlite3
        var deepTree = MakeDirUnder(basePath, "node_modules");
        var leaf = Path.Combine(deepTree, "better-sqlite3", "build", "Release", "obj", "gen", "sqlite3");
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "sqlite3.o"), "obj");
        File.WriteAllText(Path.Combine(deepTree, "better-sqlite3", "package.json"), "{}");

        var reachedBottomUp = false;
        manager.DeleteAttemptOverride = (path, bottomUp) =>
        {
            if (!bottomUp)
                throw new IOException($"Directory not empty : '{path}'"); // force the fast path to fail
            reachedBottomUp = true;
            WorktreeManager.DeleteDirectoryBottomUp(path); // exercise the REAL deepest-first sweep
        };

        var act = () => manager.DeleteDirectoryResilient(deepTree);

        act.Should().NotThrow();
        reachedBottomUp.Should().BeTrue("the final attempt must escalate to the bottom-up branch");
        Directory.Exists(deepTree).Should().BeFalse("the bottom-up sweep must remove the entire deep tree");
    }

    // -------------------------------------------------------------------------
    // Test 5: the manual-recursion path refuses to operate outside the worktree base.
    // -------------------------------------------------------------------------
    [Fact]
    public void Delete_RefusesPathOutsideBase()
    {
        var (_, manager) = CreateManager();
        var outside = MakeTempDir("outside"); // sibling of the base, NOT under it
        File.WriteAllText(Path.Combine(outside, "f.txt"), "x");

        var attempts = 0;
        manager.DeleteAttemptOverride = (path, _) =>
        {
            attempts++;
            throw new IOException($"Directory not empty : '{path}'"); // force entry into the retry path
        };

        var act = () => manager.DeleteDirectoryResilient(outside);

        act.Should().Throw<UnauthorizedAccessException>().WithMessage("*not under the worktree base*");
        attempts.Should().Be(1, "the guard must refuse BEFORE any read-only walk or bottom-up recursion");
        Directory.Exists(outside).Should().BeTrue("a refused path must be left untouched");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private (string basePath, WorktreeManager manager) CreateManager()
    {
        var basePath = MakeTempDir("worktrees");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = basePath,
            })
            .Build();
        var manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        return (basePath, manager);
    }

    private string MakeDirUnder(string basePath, string name)
    {
        var dir = Path.Combine(basePath, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-resilient-test-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void ClearReadOnly(string path)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists)
            return;
        foreach (var info in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes &= ~FileAttributes.ReadOnly;
        }
    }
}
