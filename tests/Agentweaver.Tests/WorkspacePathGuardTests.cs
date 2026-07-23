using Agentweaver.Api.Security;

namespace Agentweaver.Tests;

/// <summary>
/// Tests the symlink-resolving workspace containment guard. A purely lexical
/// Path.GetFullPath + StartsWith check does not reject symlinks/reparse points, so a
/// repository-planted symlink inside a worktree could be followed out to a host/pod file
/// (e.g. a secrets mount). WorkspacePathGuard resolves symlinks before deciding containment.
/// </summary>
public class WorkspacePathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public WorkspacePathGuardTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"wpg-{Guid.NewGuid():N}");
        _root = Path.Combine(baseDir, "workspace");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try
        {
            var parent = Path.GetDirectoryName(_root);
            if (parent is not null && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Attempts to create a symlink; returns false when the host does not permit it (e.g. Windows
    /// without Developer Mode / admin), letting the test soft-skip. CI runs on Linux where symlink
    /// creation is always permitted, so real coverage is guaranteed there.
    /// </summary>
    private static bool TryCreateSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    // (a) A symlink INSIDE the workspace pointing to a file OUTSIDE the root is rejected.
    [Fact]
    public void Symlink_escaping_workspace_is_rejected()
    {
        var secret = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(secret, "top-secret");

        var link = Path.Combine(_root, "evil-link.txt");
        if (!TryCreateSymlink(link, secret)) return; // soft-skip on unprivileged Windows

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, link, out _);

        Assert.False(contained, "A symlink resolving outside the workspace root must be rejected.");
    }

    // (b) A symlink pointing to another location WITHIN the workspace still works.
    [Fact]
    public void Symlink_within_workspace_is_allowed_and_resolves_to_target()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        var realFile = Path.Combine(subDir, "real.txt");
        File.WriteAllText(realFile, "inside");

        var link = Path.Combine(_root, "inside-link.txt");
        if (!TryCreateSymlink(link, realFile)) return; // soft-skip on unprivileged Windows

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, link, out var resolved);

        Assert.True(contained, "A symlink resolving inside the workspace root must be allowed.");
        Assert.Equal("inside", File.ReadAllText(resolved));
    }

    // (c) Normal (non-symlink) paths inside the workspace are unaffected.
    [Fact]
    public void Normal_path_inside_workspace_is_allowed()
    {
        var subDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(subDir);
        var file = Path.Combine(subDir, "readme.md");
        File.WriteAllText(file, "hello");

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, file, out var resolved);

        Assert.True(contained);
        Assert.Equal("hello", File.ReadAllText(resolved));
    }

    // (c') A plain lexical traversal (no symlink) that escapes the root is still rejected.
    [Fact]
    public void Lexical_traversal_escaping_root_is_rejected()
    {
        var escaping = Path.Combine(_root, "..", "outside", "secret.txt");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "top-secret");

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, escaping, out _);

        Assert.False(contained);
    }

    // A not-yet-existing file inside the workspace (create/write scenario) is allowed.
    [Fact]
    public void Nonexistent_target_inside_workspace_is_allowed()
    {
        var newFile = Path.Combine(_root, ".agentweaver", "workflows", "new.yaml");

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, newFile, out var resolved);

        Assert.True(contained);
        Assert.False(string.IsNullOrEmpty(resolved));
    }

    // A not-yet-existing file whose existing ancestor is a symlink escaping the root is rejected.
    [Fact]
    public void Nonexistent_target_under_escaping_symlinked_dir_is_rejected()
    {
        var outsideDir = Path.Combine(_outside, "escaped-dir");
        Directory.CreateDirectory(outsideDir);

        var linkDir = Path.Combine(_root, "linked");
        if (!TryCreateSymlink(linkDir, outsideDir)) return; // soft-skip on unprivileged Windows

        var target = Path.Combine(linkDir, "new.yaml"); // does not exist yet

        var contained = WorkspacePathGuard.TryResolveContainedPath(_root, target, out _);

        Assert.False(contained, "A write target under a symlinked-out ancestor must be rejected.");
    }
}
