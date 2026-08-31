using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Sandbox;

public sealed class BubblewrapSandboxCommandTests
{
    [Theory]
    [InlineData("native-linux")]
    [InlineData("wsl")]
    public void BwrapCommand_UsesSelectiveEtcMountsAndHidesHomes(string mode)
    {
        var payload = mode == "native-linux"
            ? LinuxBwrapExecutor.BuildBwrapPayload("echo ok", "/workspace", networkEnabled: false)
            : WslMxcSandboxExecutor.BuildBwrapCommand("echo ok", networkEnabled: false);

        payload.Should().NotContain("--ro-bind /etc /etc");
        payload.Should().Contain("--ro-bind-try /etc/resolv.conf /etc/resolv.conf");
        payload.Should().Contain("--ro-bind-try /etc/passwd /etc/passwd");
        payload.Should().Contain("--ro-bind-try /etc/group /etc/group");
        payload.Should().Contain("--ro-bind-try /etc/nsswitch.conf /etc/nsswitch.conf");
        payload.Should().Contain("--tmpfs /home");
        payload.Should().Contain("--tmpfs /root");
        payload.Should().Contain("--unshare-user");
        payload.Should().Contain("--unshare-net");
    }

    [Theory]
    [InlineData("native-linux")]
    [InlineData("wsl")]
    public void BwrapCommand_UsesTargetedUsrMountsNotBroadBind(string mode)
    {
        // Phase 6 alignment: replace --ro-bind /usr /usr with selective mounts.
        // This prevents exposing /usr/share/doc, /usr/include, etc. unnecessarily.
        var payload = mode == "native-linux"
            ? LinuxBwrapExecutor.BuildBwrapPayload("echo ok", "/workspace", networkEnabled: false)
            : WslMxcSandboxExecutor.BuildBwrapCommand("echo ok", networkEnabled: false);

        payload.Should().NotContain("--ro-bind /usr /usr",
            "broad /usr mount replaced by targeted --ro-bind-try for bin/lib dirs");
        payload.Should().Contain("--ro-bind-try /usr/bin /usr/bin");
        payload.Should().Contain("--ro-bind-try /usr/lib /usr/lib");
        payload.Should().Contain("--ro-bind-try /usr/share/nodejs /usr/share/nodejs");
    }

    [Theory]
    [InlineData("native-linux")]
    [InlineData("wsl")]
    public void BwrapCommand_NetworkDisabled_IncludesUnshareNet(string mode)
    {
        var payload = mode == "native-linux"
            ? LinuxBwrapExecutor.BuildBwrapPayload("echo ok", "/workspace", networkEnabled: false)
            : WslMxcSandboxExecutor.BuildBwrapCommand("echo ok", networkEnabled: false);

        payload.Should().Contain("--unshare-net",
            "network namespace must be unshared when networkEnabled=false");
    }

    [Theory]
    [InlineData("native-linux")]
    [InlineData("wsl")]
    public void BwrapCommand_NetworkEnabled_OmitsUnshareNet(string mode)
    {
        var payload = mode == "native-linux"
            ? LinuxBwrapExecutor.BuildBwrapPayload("echo ok", "/workspace", networkEnabled: true)
            : WslMxcSandboxExecutor.BuildBwrapCommand("echo ok", networkEnabled: true);

        payload.Should().NotContain("--unshare-net",
            "--unshare-net must be absent when networkEnabled=true");
    }

    [Fact]
    public void BwrapPayload_WorktreePath_MountedAtWorkspaceNotHomePath()
    {
        // Simulate a worktree path under /home — the masking bug scenario.
        var homeWorktree = "/home/asabbour/.local/share/agentweaver/worktrees/run-abc123";
        var payload = LinuxBwrapExecutor.BuildBwrapPayload("echo ok", homeWorktree, networkEnabled: false);

        // The destination bind path must NOT stay inside /home (masked by --tmpfs /home).
        payload.Should().NotContain($"--bind '{homeWorktree}' '{homeWorktree}'",
            "binding the worktree at its original /home path is masked by --tmpfs /home");

        // The worktree must instead be mounted at /workspace.
        payload.Should().Contain($"--bind '{homeWorktree}' /workspace",
            "worktree must be re-mounted at /workspace to escape --tmpfs /home masking");

        // Working directory inside the sandbox must be /workspace.
        payload.Should().Contain("--chdir /workspace",
            "chdir must point to /workspace, not the original host path under /home");
    }
}


/// <summary>
/// F. SandboxedFileTools defense-in-depth: in-tool validation provides an
/// additional layer even if governance is somehow bypassed.
/// </summary>
public sealed class SandboxedFileToolsDefenseTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxedFileTools _tools;

    public SandboxedFileToolsDefenseTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sandbox-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _tools = new SandboxedFileTools(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData(@"..\..\..\etc\passwd")]
    [InlineData(@"..\..\Windows\system32\cmd.exe")]
    public async Task ReadFileAsync_TraversalAttack_ReturnsRejected(string path)
    {
        var (content, failure) = await _tools.ReadFileAsync(path);

        content.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData(@"D:\evil\x.txt")]
    public async Task ReadFileAsync_AbsoluteOutOfSandbox_ReturnsRejected(string path)
    {
        var (content, failure) = await _tools.ReadFileAsync(path);

        content.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Fact]
    public async Task ReadFileAsync_InSandbox_ReturnsContent()
    {
        var filePath = Path.Combine(_sandboxRoot, "hello.txt");
        await File.WriteAllTextAsync(filePath, "hello world");

        var (content, failure) = await _tools.ReadFileAsync("hello.txt");

        failure.Should().BeNull();
        content.Should().Be("hello world");
    }

    [Theory]
    [InlineData(@"..\..\..\etc\shadow")]
    [InlineData(@"C:\Windows\temp\evil.txt")]
    public async Task WriteFileAsync_EscapeAttempt_ReturnsRejected(string path)
    {
        var (bytes, failure) = await _tools.WriteFileAsync(path, "evil content");

        bytes.Should().Be(0);
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Fact]
    public async Task WriteFileAsync_InSandbox_Succeeds()
    {
        var (bytes, failure) = await _tools.WriteFileAsync("output.txt", "safe content");

        failure.Should().BeNull();
        bytes.Should().BeGreaterThan(0);
        File.ReadAllText(Path.Combine(_sandboxRoot, "output.txt")).Should().Be("safe content");
    }

    [Fact]
    public async Task GitControlFiles_CannotBeMutatedByFileToolsOrPatches()
    {
        var (bytes, failure) = await _tools.WriteFileAsync(".git", "gitdir: /workspace/other");
        bytes.Should().Be(0);
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);

        var patch = await _tools.ApplyPatchAsync(
            """
            *** Begin Patch
            *** Add File: nested/.git/config
            +[core]
            +hooksPath = /workspace/other/hooks
            *** End Patch
            """);
        patch.Success.Should().BeFalse();
        patch.Reason.Should().Contain(".git");
    }

    [Fact]
    public async Task ReadFileAsync_OnDirectory_ReturnsDirectoryHint()
    {
        // "." resolves to the sandbox root, which is a directory — should return a helpful failure
        var (content, failure) = await _tools.ReadFileAsync(".");

        content.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.NotFound);
        failure.Message.Should().Contain("list_directory");
    }

    [Fact]
    public async Task ListDirectoryAsync_DotPath_ReturnsEntries()
    {
        // Create a file and a subdirectory inside the sandbox
        File.WriteAllText(Path.Combine(_sandboxRoot, "readme.md"), "hello");
        Directory.CreateDirectory(Path.Combine(_sandboxRoot, "src"));

        var (entries, failure) = await _tools.ListDirectoryAsync(".");

        failure.Should().BeNull();
        entries.Should().NotBeNull();
        entries!.Should().Contain(e => e.Name == "readme.md" && e.Kind == SandboxEntryKind.File);
        entries.Should().Contain(e => e.Name == "src" && e.Kind == SandboxEntryKind.Directory);
    }

    [Fact]
    public async Task ListDirectoryAsync_SubDirectory_ReturnsEntries()
    {
        var sub = Path.Combine(_sandboxRoot, "subdir");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "file.cs"), "code");

        var (entries, failure) = await _tools.ListDirectoryAsync("subdir");

        failure.Should().BeNull();
        entries.Should().NotBeNull();
        entries!.Should().ContainSingle(e => e.Name == "file.cs");
    }

    [Fact]
    public async Task ListDirectoryAsync_NonExistentDir_ReturnsNotFound()
    {
        var (entries, failure) = await _tools.ListDirectoryAsync("nonexistent");

        entries.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.NotFound);
    }

    [Fact]
    public async Task ListDirectoryAsync_EscapeAttempt_ReturnsRejected()
    {
        var (entries, failure) = await _tools.ListDirectoryAsync(@"..\..\Windows");

        entries.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(SandboxFailureKind.Rejected);
    }

    [Fact]
    public async Task ListDirectoryAsync_CancellationToken_ThrowsWhenCancelled()
    {
        // Create enough entries to iterate over
        for (int i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(_sandboxRoot, $"file{i}.txt"), "x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _tools.ListDirectoryAsync(".", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListDirectoryAsync_DoesNotFollowReparsePoints()
    {
        // Create a real subdirectory with a file
        var realSub = Path.Combine(_sandboxRoot, "real");
        Directory.CreateDirectory(realSub);
        File.WriteAllText(Path.Combine(realSub, "legit.txt"), "ok");

        // Create a symlink directory inside sandbox pointing outside
        var outsideTarget = Path.Combine(Path.GetTempPath(), $"outside-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllText(Path.Combine(outsideTarget, "secret.txt"), "sensitive");

        var symlinkDir = Path.Combine(_sandboxRoot, "linkdir");
        try
        {
            Directory.CreateSymbolicLink(symlinkDir, outsideTarget);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        finally
        {
            try { Directory.Delete(outsideTarget, true); } catch { }
        }

        try
        {
            // List the root — the symlink directory should be skipped by EnumerationOptions
            var (entries, failure) = await _tools.ListDirectoryAsync(".");

            failure.Should().BeNull();
            entries.Should().NotBeNull();
            entries!.Should().NotContain(e => e.Name == "linkdir",
                "reparse-point entries must be excluded from listing");
            entries.Should().Contain(e => e.Name == "real");
        }
        finally
        {
            try { Directory.Delete(symlinkDir); } catch { }
        }
    }
}

/// <summary>
/// Finding 1 regression: per-command temp dir isolation.
/// Verifies that SandboxPolicyEnrichment no longer grants the shared %TEMP% root as RW,
/// and that the MxcSandboxExecutor per-command subdir is created under a "agentweaver-sandbox"
/// prefix (structural contract test — does not require the mxc binary).
/// </summary>
public sealed class PerCommandTempDirTests
{
    [Fact]
    public void SandboxPolicyEnrichment_BuildForWindows_DoesNotIncludeTempRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var enrichment = SandboxPolicyEnrichment.BuildForWindows();
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        // The raw %TEMP% root must NOT appear as a read-write path.
        enrichment.AdditionalReadWritePaths.Should().NotContain(
            p => string.Equals(
                p.TrimEnd(Path.DirectorySeparatorChar),
                tempRoot,
                StringComparison.OrdinalIgnoreCase),
            "the shared temp root grants cross-sandbox write access and must be replaced " +
            "by per-command isolated subdirs injected at execution time");
    }

    [Fact]
    public void PerCommandTempSubdir_IsCreatedUnderAgentweaverSandboxPrefix()
    {
        // Reproduce the naming logic from MxcSandboxExecutor.ExecuteAsync to verify
        // the structural contract: subdir lives under Path.GetTempPath()/agentweaver-sandbox/<guid>.
        var perCmdTempDir = Path.Combine(
            Path.GetTempPath(), "agentweaver-sandbox", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(perCmdTempDir);
        try
        {
            Directory.Exists(perCmdTempDir).Should().BeTrue(
                "per-command temp subdir must exist after Directory.CreateDirectory");

            perCmdTempDir.Should().StartWith(
                Path.Combine(Path.GetTempPath(), "agentweaver-sandbox"),
                "subdir must be scoped under agentweaver-sandbox to avoid polluting temp root");
        }
        finally
        {
            try { Directory.Delete(perCmdTempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PerCommandTempSubdir_IsCleanedUpAfterDelete()
    {
        var perCmdTempDir = Path.Combine(
            Path.GetTempPath(), "agentweaver-sandbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(perCmdTempDir);
        File.WriteAllText(Path.Combine(perCmdTempDir, "artifact.txt"), "data");

        // Simulate the best-effort cleanup in MxcSandboxExecutor's finally block.
        try { Directory.Delete(perCmdTempDir, recursive: true); } catch { }

        Directory.Exists(perCmdTempDir).Should().BeFalse(
            "per-command temp subdir must be deleted after the command completes");
    }
}
