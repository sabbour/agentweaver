using System.Diagnostics;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class KataBwrapExecutorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(AppContext.BaseDirectory, $"kata-bwrap-{Guid.NewGuid():N}");
    private readonly string _workspace;
    private readonly string _runA;
    private readonly string _runB;
    private readonly string _runtimeHome;

    public KataBwrapExecutorTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _runA = Path.Combine(_workspace, "worktrees", "run-a");
        _runB = Path.Combine(_workspace, "worktrees", "run-b");
        _runtimeHome = Path.Combine(_root, "runtime-home", "run-a");
        Directory.CreateDirectory(_runA);
        Directory.CreateDirectory(_runB);
        CreateRuntimeHome(_runtimeHome);
    }

    [Fact]
    public void MountPlan_ContainsOwnWorktreeAndGitMetadataButNotSharedRootOrSibling()
    {
        var commonGit = Path.Combine(_workspace, "repositories", "project", ".git");
        var worktreeGit = Path.Combine(commonGit, "worktrees", "run-a");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(_runA, ".git"), $"gitdir: {worktreeGit}");
        File.WriteAllText(Path.Combine(worktreeGit, "gitdir"), Path.Combine(_runA, ".git"));
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var mounts = executor.BuildMountPlan(Command(_runA));

        mounts.Select(mount => mount.Source).Should().Contain(Path.GetFullPath(_runA));
        mounts.Select(mount => mount.Source).Should().Contain(Path.GetFullPath(commonGit));
        mounts.Single(mount => mount.Source == Path.GetFullPath(commonGit))
            .ReadOnly.Should().BeTrue();
        mounts.Select(mount => mount.Source).Should().NotContain(Path.GetFullPath(_workspace));
        mounts.Select(mount => mount.Source).Should().NotContain(Path.GetFullPath(_runB));
    }

    [Fact]
    public void MountPlan_RejectsProtectedSharedRoot()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);

        var act = () => executor.BuildMountPlan(Command(_workspace));

        act.Should().Throw<SandboxViolationException>()
            .WithMessage("*protected shared root*");
    }

    [Fact]
    public void MountPlan_RejectsFilesystemRootAncestorOfProtectedWorkspace()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(_workspace))!;
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);

        var act = () => executor.BuildMountPlan(Command(filesystemRoot));

        act.Should().Throw<SandboxViolationException>()
            .WithMessage("*protected shared root*");
    }

    [Fact]
    public void MountPlan_RejectsTraversalThatNormalizesToProtectedRoot()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        var traversingRoot = Path.Combine(_runA, "..", "..");

        var act = () => executor.BuildMountPlan(Command(traversingRoot));

        act.Should().Throw<SandboxViolationException>()
            .WithMessage("*protected shared root*");
    }

    [Fact]
    public void MountPlan_RejectsSymlinkOrJunctionRoot()
    {
        var link = Path.Combine(_workspace, "run-link");
        try
        {
            Directory.CreateSymbolicLink(link, _runA);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        var act = () => executor.BuildMountPlan(Command(link));

        act.Should().Throw<SandboxViolationException>()
            .WithMessage("*symbolic link or junction*");
    }

    [Fact]
    public void RegisteredWorkspace_DoesNotReReadPoisonedGitMetadata()
    {
        var commonGit = Path.Combine(_workspace, "repositories", "project", ".git");
        var worktreeGit = Path.Combine(commonGit, "worktrees", "run-a");
        Directory.CreateDirectory(worktreeGit);
        var dotGit = Path.Combine(_runA, ".git");
        File.WriteAllText(dotGit, $"gitdir: {worktreeGit}");
        File.WriteAllText(Path.Combine(worktreeGit, "gitdir"), dotGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        File.WriteAllText(dotGit, $"gitdir: {_runB}");

        var mounts = executor.BuildMountPlan(Command(_runA));

        mounts.Select(mount => mount.Source).Should().Contain(Path.GetFullPath(commonGit));
        mounts.Select(mount => mount.Source).Should().NotContain(Path.GetFullPath(_runB));
    }

    [Fact]
    public void PreviewSubdirectory_UsesRegisteredWorkspaceRoot()
    {
        var subdirectory = Path.Combine(_runA, "app");
        Directory.CreateDirectory(subdirectory);
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);

        var mounts = executor.BuildMountPlan(Command(subdirectory));

        mounts.Select(mount => mount.Source).Should().Contain(Path.GetFullPath(_runA));
        mounts.Select(mount => mount.Source).Should().NotContain(Path.GetFullPath(_workspace));
    }

    [Fact]
    public void MountPlan_ContainsOnlyExplicitlyRegisteredRuntimeHome()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var command = Command(_runA) with
        {
            Environment = new Dictionary<string, string>
            {
                ["HOME"] = _runB,
                ["XDG_CACHE_HOME"] = Path.Combine(_runB, ".cache"),
            },
        };

        var mounts = executor.BuildMountPlan(command);

        mounts.Should().ContainSingle(mount =>
            mount.Source == Path.GetFullPath(_runtimeHome) && !mount.ReadOnly);
        mounts.Select(mount => mount.Source).Should().NotContain(Path.GetFullPath(_runB));
        mounts.Select(mount => mount.Source).Should().NotContain("/home/appuser");
    }

    [Fact]
    public void ShellChildProcess_UsesRegisteredRuntimeHomeAndXdg()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var command = Command(_runA) with
        {
            Environment = new Dictionary<string, string>
            {
                ["HOME"] = _runB,
                ["XDG_CACHE_HOME"] = _runB,
                ["XDG_DATA_HOME"] = _runB,
                ["XDG_CONFIG_HOME"] = _runB,
            },
        };

        var environment = OperatingSystem.IsLinux()
            ? ReadSetEnvironment(executor.BuildProcessStartInfo(command).ArgumentList)
            : executor.BuildChildEnvironment(command);

        AssertRuntimeHomeEnvironment(environment);
    }

    [Fact]
    public void PreviewChildProcess_UsesRegisteredRuntimeHomeAndXdg()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var subdirectory = Path.Combine(_runA, "app");
        Directory.CreateDirectory(subdirectory);
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);

        using var process = executor.CreateProcess(
            "echo preview",
            subdirectory,
            new Dictionary<string, string>
            {
                ["HOME"] = _runB,
                ["XDG_CACHE_HOME"] = _runB,
                ["XDG_DATA_HOME"] = _runB,
                ["XDG_CONFIG_HOME"] = _runB,
            },
            networkEnabled: true);
        var environment = ReadSetEnvironment(process.StartInfo.ArgumentList);

        AssertRuntimeHomeEnvironment(environment);
        process.StartInfo.ArgumentList.Should().ContainInOrder(
            "--bind",
            Path.GetFullPath(_runtimeHome),
            Path.GetFullPath(_runtimeHome));
    }

    [Fact]
    public void MountPlan_RejectsTrustedWorkspaceWithoutRegisteredRuntimeHome()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        executor.RegisterTrustedWorkspace(_runA);

        var act = () => executor.BuildMountPlan(Command(_runA));

        act.Should().Throw<SandboxViolationException>()
            .WithMessage("*runtime HOME was not registered*");
    }

    [Fact]
    public void RegisterRuntimeHome_RejectsInvalidOrChangedRegistration()
    {
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        var incompleteHome = Path.Combine(_root, "runtime-home", "incomplete");
        Directory.CreateDirectory(incompleteHome);

        var incomplete = () => executor.RegisterRuntimeHome(_runA, incompleteHome);
        var overlapping = () => executor.RegisterRuntimeHome(_runA, _runA);

        incomplete.Should().Throw<SandboxViolationException>()
            .WithMessage("*missing an authoritative XDG directory*");
        overlapping.Should().Throw<SandboxViolationException>()
            .WithMessage("*disjoint from the run workspace*");

        executor.RegisterRuntimeHome(_runA, _runtimeHome);
        var otherHome = Path.Combine(_root, "runtime-home", "other");
        CreateRuntimeHome(otherHome);
        var changed = () => executor.RegisterRuntimeHome(_runA, otherHome);

        changed.Should().Throw<SandboxViolationException>()
            .WithMessage("*changed after registration*");
    }

    [Fact]
    public void StandaloneRepositoryGitDirectory_IsReadOnly()
    {
        var dotGit = Path.Combine(_runA, ".git");
        Directory.CreateDirectory(dotGit);
        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);

        var mounts = executor.BuildMountPlan(Command(_runA));

        mounts.Single(mount => mount.Source == Path.GetFullPath(dotGit))
            .ReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ReadOnlyGitMountCannotBeHardLinkedIntoWritableWorkspace()
    {
        if (!OperatingSystem.IsLinux() || !KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        Run("git", "-C", _runA, "init");
        Run("git", "-C", _runA, "config", "user.name", "Agentweaver Test");
        var configPath = Path.Combine(_runA, ".git", "config");
        var originalConfig = await File.ReadAllTextAsync(configPath);

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var result = await executor.ExecuteAsync(new SandboxCommand(
            "ln .git/config config-link && printf poisoned > config-link",
            _runA,
            null,
            new SandboxFsPolicy([_runA], [], []),
            10_000,
            NetworkEnabled: false));

        result.ExitCode.Should().NotBe(0);
        File.Exists(Path.Combine(_runA, "config-link")).Should().BeFalse();
        (await File.ReadAllTextAsync(configPath)).Should().Be(originalConfig);
    }

    [Fact]
    public void ProcessNamespace_UsesSyntheticProcAndNeverBindsParentProc()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var arguments = executor.BuildProcessStartInfo(Command(_runA)).ArgumentList.ToArray();

        arguments.Should().ContainInOrder("--tmpfs", "/proc");
        string.Join('\n', arguments).Should().NotContain("--ro-bind\n/proc\n/proc");
        arguments.Should().ContainInOrder("--cap-drop", "ALL");
        arguments.Should().Contain("--clearenv");
    }

    [Fact]
    public async Task VariableAbsolutePathAndSymlinkCannotReadOrWriteSibling()
    {
        if (!OperatingSystem.IsLinux() || !KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        var secret = Path.Combine(_runB, "secret.txt");
        await File.WriteAllTextAsync(secret, "sibling-secret");
        Directory.CreateSymbolicLink(Path.Combine(_runA, "sibling-link"), _runB);

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var result = await executor.ExecuteAsync(new SandboxCommand(
            "cat \"$VICTIM\"; printf compromised > \"$VICTIM\"; cat sibling-link/secret.txt",
            _runA,
            new Dictionary<string, string> { ["VICTIM"] = secret },
            new SandboxFsPolicy([_runA], [], []),
            10_000,
            NetworkEnabled: false));

        result.ExitCode.Should().NotBe(0);
        result.Stdout.Should().NotContain("sibling-secret");
        (await File.ReadAllTextAsync(secret)).Should().Be("sibling-secret");
    }

    [Fact]
    public async Task LinkedWorktreeReadOperationsAndPlatformCommitRemainFunctional()
    {
        if (!OperatingSystem.IsLinux() || !KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        var repository = Path.Combine(_workspace, "repositories", "project");
        Directory.CreateDirectory(repository);
        Run("git", "-C", repository, "init");
        Run("git", "-C", repository, "config", "user.name", "Agentweaver Test");
        Run("git", "-C", repository, "config", "user.email", "agentweaver@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "before\n");
        Run("git", "-C", repository, "add", "tracked.txt");
        Run("git", "-C", repository, "commit", "-m", "initial");
        Directory.Delete(_runA);
        Run("git", "-C", repository, "worktree", "add", "-b", "run-a", _runA, "HEAD");

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var result = await executor.ExecuteAsync(new SandboxCommand(
            "printf after >> tracked.txt && git status --short && git diff -- tracked.txt",
            _runA,
            null,
            new SandboxFsPolicy([_runA], [], []),
            20_000,
            NetworkEnabled: false));

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("tracked.txt");
        var deniedCommit = await executor.ExecuteAsync(new SandboxCommand(
            "git add tracked.txt && git commit -m sandbox-commit",
            _runA,
            null,
            new SandboxFsPolicy([_runA], [], []),
            20_000,
            NetworkEnabled: false));
        deniedCommit.ExitCode.Should().NotBe(0, "shared git metadata is intentionally read-only");

        Run("git", "-C", _runA, "add", "tracked.txt");
        Run("git", "-C", _runA, "commit", "-m", "isolated");
        Run("git", "-C", _runA, "log", "-1", "--pretty=%s").Should().Be("isolated");
    }

    private static SandboxCommand Command(string workingDirectory) =>
        new(
            "echo ok",
            workingDirectory,
            null,
            new SandboxFsPolicy([workingDirectory], [], []),
            5000);

    private void RegisterRun(KataBwrapExecutor executor)
    {
        executor.RegisterTrustedWorkspace(_runA);
        executor.RegisterRuntimeHome(_runA, _runtimeHome);
    }

    private void AssertRuntimeHomeEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        environment["HOME"].Should().Be(Path.GetFullPath(_runtimeHome));
        environment["XDG_CACHE_HOME"].Should().Be(Path.Combine(_runtimeHome, ".cache"));
        environment["XDG_DATA_HOME"].Should().Be(Path.Combine(_runtimeHome, ".local", "share"));
        environment["XDG_CONFIG_HOME"].Should().Be(Path.Combine(_runtimeHome, ".config"));
    }

    private static Dictionary<string, string> ReadSetEnvironment(
        ICollection<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = arguments.ToArray();
        for (var index = 0; index + 2 < items.Length; index++)
        {
            if (items[index] != "--setenv")
                continue;
            values[items[index + 1]] = items[index + 2];
            index += 2;
        }
        return values;
    }

    private static void CreateRuntimeHome(string home)
    {
        Directory.CreateDirectory(Path.Combine(home, ".cache"));
        Directory.CreateDirectory(Path.Combine(home, ".local", "share"));
        Directory.CreateDirectory(Path.Combine(home, ".config"));
    }

    private static string Run(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, stderr);
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
