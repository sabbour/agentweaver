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
    [Trait("Category", KataRuntimeGate.Category)]
    public void PreviewChildProcess_UsesRegisteredRuntimeHomeAndXdg()
    {
        if (!KataRuntimeGate.Available())
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
    [Trait("Category", KataRuntimeGate.Category)]
    public async Task ReadOnlyGitMountCannotBeHardLinkedIntoWritableWorkspace()
    {
        if (!KataRuntimeGate.Available())
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

    /// <summary>
    /// Process isolation is provided by the executor sidecar CONTAINER (its own runtime-created PID
    /// namespace and matching runtime-provided procfs), not by bubblewrap. The kernel's
    /// <c>mount_too_revealing()</c> check refuses a fresh procfs inside an unprivileged user
    /// namespace whenever the visible procfs has masked submounts — which every Kubernetes runtime,
    /// Kata included, always creates. So bubblewrap must bind the container's own procfs and must
    /// NOT claim a PID namespace it cannot back with a matching procfs.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void MountNamespace_BindsContainerProcfsAndNeverUnsharesPid()
    {
        if (!KataRuntimeGate.Available())
            return;

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var arguments = executor.BuildProcessStartInfo(Command(_runA)).ArgumentList.ToArray();

        arguments.Should().ContainInOrder("--bind", "/proc", "/proc");
        arguments.Should().NotContain("--proc");
        // --unshare-pid without a matching procfs is a silent-degradation trap: bubblewrap would
        // bind the outer /proc while the child's PIDs no longer match it.
        arguments.Should().NotContain("--unshare-pid");
        arguments.Should().Contain("--unshare-user");
        arguments.Should().ContainInOrder("--cap-drop", "ALL");
        arguments.Should().Contain("--clearenv");
    }

    /// <summary>
    /// The startup probe is the fail-closed gate for the sidecar; it must exercise exactly the same
    /// namespace flags the real command path uses, or the probe would pass where execution fails
    /// (which is precisely how the v0.18.0 production outage reached the cluster).
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void AvailabilityProbe_UsesTheSameNamespaceFlagsAsRealCommands()
    {
        if (!KataRuntimeGate.Available())
            return;

        var probe = KataBwrapExecutor.BuildAvailabilityProbeArguments();

        probe.Should().ContainInOrder("--bind", "/proc", "/proc");
        probe.Should().NotContain("--proc");
        probe.Should().NotContain("--unshare-pid");
        probe.Should().Contain("--unshare-user");
    }

    /// <summary>
    /// The workload must own the sandbox's stdout/stderr and its own session. Bubblewrap closes
    /// <c>--info-fd</c> after setup, so reusing fd 1 for it silently breaks every write the command
    /// makes to stdout ("write error: Bad file descriptor"), and an extra <c>setsid</c> wrapper
    /// would fork a grandchild that the executor cannot attribute on a kernel without
    /// <c>/proc/&lt;pid&gt;/task/&lt;pid&gt;/children</c>. Both were observed on real Kata.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public void SupervisedLaunch_KeepsWorkloadStdoutAndExecsItDirectlyInItsOwnSession()
    {
        if (!KataRuntimeGate.Available())
            return;

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var startInfo = executor.BuildProcessStartInfo(Command(_runA));
        var arguments = startInfo.ArgumentList.ToArray();

        startInfo.RedirectStandardOutput.Should().BeTrue();
        arguments.Should().NotContain("--info-fd");
        arguments.Should().Contain("--new-session");
        arguments.Should().NotContain("/usr/bin/setsid");
        var terminator = Array.IndexOf(arguments, "--");
        terminator.Should().BeGreaterThan(0);
        arguments[terminator + 1].Should().Be("/bin/bash");
    }

    /// <summary>
    /// Nothing reaps daemonised grandchildren for a sandbox that deliberately does not claim its
    /// own PID namespace, and .NET's <c>Kill(entireProcessTree)</c> cannot help because it walks the
    /// procfs children file the Kata guest kernel omits. The executor must terminate the run's
    /// process group itself.
    /// </summary>
    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public async Task CompletedCommand_LeavesNoDaemonisedProcessesBehind()
    {
        if (!KataRuntimeGate.Available())
            return;

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var marker = Path.Combine(_runA, "daemon.pid");
        var result = await executor.ExecuteAsync(new SandboxCommand(
            $"(sleep 300 & echo $! > {QuoteForPosixShell(marker)}) ; sleep 0.2",
            _runA,
            null,
            new SandboxFsPolicy([_runA], [], []),
            30_000,
            NetworkEnabled: false));

        result.ExitCode.Should().Be(0, result.Stderr);
        var daemonPid = int.Parse((await File.ReadAllTextAsync(marker)).Trim());

        // The pid is the executor-container-visible pid because the sandbox shares this PID
        // namespace by design; the process group kill must already have reaped it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists($"/proc/{daemonPid}") && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Directory.Exists($"/proc/{daemonPid}").Should().BeFalse(
            "a command that daemonises a background process must not leak it into the executor container");
    }

    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public async Task SandboxedProcess_RunsManagedCoreClrAndCannotReachSiblingRuns()
    {
        if (!KataRuntimeGate.Available())
            return;

        var app = Path.Combine(_runA, "managed-proc-probe");
        Directory.CreateDirectory(app);
        await File.WriteAllTextAsync(
            Path.Combine(app, "ManagedProcProbe.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(app, "Program.cs"),
            """
            var siblingRun = args[0];
            Console.WriteLine($"coreclr-ok pid={Environment.ProcessId}");
            return Directory.Exists(siblingRun) ? 17 : 0;
            """);
        Run("dotnet", "build", Path.Combine(app, "ManagedProcProbe.csproj"), "--nologo", "--verbosity", "quiet");

        var executor = new KataBwrapExecutor(protectedRoots: [_workspace]);
        RegisterRun(executor);
        var assembly = Path.Combine(app, "bin", "Debug", "net10.0", "ManagedProcProbe.dll");
        // The sibling path is passed through the environment, not the command line: the static
        // shared-mount guard rejects a literal cross-run path before the command ever runs, and
        // this test is about the mount namespace, not that guard (see
        // VariableAbsolutePathAndSymlinkCannotReadOrWriteSibling for the guard itself).
        var result = await executor.ExecuteAsync(new SandboxCommand(
            $"dotnet {QuoteForPosixShell(assembly)} \"$SIBLING_RUN\"",
            _runA,
            new Dictionary<string, string> { ["SIBLING_RUN"] = _runB },
            new SandboxFsPolicy([_runA], [], []),
            30_000,
            NetworkEnabled: true));

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("coreclr-ok");
    }

    [Fact]
    [Trait("Category", KataRuntimeGate.Category)]
    public async Task VariableAbsolutePathAndSymlinkCannotReadOrWriteSibling()
    {
        if (!KataRuntimeGate.Available())
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
    [Trait("Category", KataRuntimeGate.Category)]
    public async Task LinkedWorktreeReadOperationsAndPlatformCommitRemainFunctional()
    {
        if (!KataRuntimeGate.Available())
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
        environment["DOTNET_CLI_HOME"].Should().Be(Path.GetFullPath(_runtimeHome));
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

    private static string QuoteForPosixShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
