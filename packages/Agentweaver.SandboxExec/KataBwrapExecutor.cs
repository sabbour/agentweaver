using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentweaver.SandboxFs;
using Microsoft.Extensions.Logging;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Executes commands in a private bubblewrap mount namespace, running <b>inside the executor
/// sidecar container</b> of the run's Kata pod (<see cref="PodExec.PodExecServer"/> is its only
/// production caller).
///
/// Layered boundary:
/// <list type="bullet">
///   <item>the Kata VM isolates the pod from the node;</item>
///   <item>the executor sidecar container supplies the PID namespace, the runtime-provided procfs,
///     and a mount namespace that the AgentHost process never shares;</item>
///   <item>bubblewrap removes the shared workspace PVC root and binds only this run's declared roots
///     into every model-controlled child.</item>
/// </list>
///
/// <para><b>Why no <c>--unshare-pid</c> / <c>--proc</c> here.</b> Mounting a fresh procfs inside an
/// unprivileged user namespace requires a fully visible procfs. Every Kubernetes container runtime —
/// including Kata's guest agent — masks <c>/proc/kcore</c>, <c>/proc/keys</c>, <c>/proc/timer_list</c>,
/// <c>/proc/interrupts</c> and read-only-binds <c>/proc/bus|fs|irq|sys|sysrq-trigger</c>, so the kernel's
/// <c>mount_too_revealing()</c> check refuses the mount with <c>EPERM</c>
/// (<c>bwrap: Can't mount proc on /newroot/proc: Operation not permitted</c>). The only ways to make a
/// nested procfs mount succeed are to unmask procfs, add <c>CAP_SYS_ADMIN</c>, or fabricate a
/// synthetic <c>/proc</c> — all of which weaken the boundary. The process boundary is therefore taken
/// from the sidecar container (a real, runtime-created PID namespace) and bubblewrap binds that
/// container's own procfs, which contains only this run's executor processes.</para>
/// </summary>
public sealed class KataBwrapExecutor : ISandboxExecutor, IRunWorkspaceRegistrar
{
    private static readonly string[] SystemReadOnlyRoots = ["/usr", "/etc", "/opt"];
    private static readonly string[] PodPrivateReadWriteRoots = ["/tmp", "/var/tmp"];
    private readonly ILogger? _logger;
    private readonly IReadOnlyList<string> _protectedRoots;
    private readonly ConcurrentDictionary<string, IReadOnlyList<MountSpec>> _trustedWorkspaces =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _runtimeHomes =
        new(StringComparer.Ordinal);

    public bool IsRealIsolation => true;
    public string BackendName => "kata-sidecar-bwrap-fs";
    public string SelectionReason =>
        "Kata VM, executor sidecar PID/mount namespace, and a fail-closed bubblewrap mount namespace scoped to this run.";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    public KataBwrapExecutor(
        ILogger? logger = null,
        IReadOnlyList<string>? protectedRoots = null)
    {
        _logger = logger;
        _protectedRoots = (protectedRoots ?? ResolveProtectedRoots())
            .Select(Path.GetFullPath)
            .ToArray();
    }

    /// <summary>
    /// Proves that the current Linux security context can create the mount namespace used by
    /// this executor. Production startup fails closed when this probe fails.
    /// </summary>
    /// <remarks>
    /// The probe intentionally mirrors the production argument shape, including the
    /// <c>--bind /proc /proc</c> that replaced the previously-attempted private <c>--proc</c> mount
    /// (see the type remarks: a nested procfs mount is refused by the kernel in every masked-procfs
    /// container, which is what broke the Kata warm pool). It therefore fails for the same reasons a
    /// real command would fail, rather than for a probe-only reason.
    /// </remarks>
    /// <summary>
    /// The exact bubblewrap argument vector used by the startup availability probe. Exposed so tests
    /// can assert that the probe and the real command path claim the same namespaces — a probe that
    /// is weaker than execution would pass in CI and fail in production (v0.18.0), and a probe that
    /// is stronger would refuse to start a perfectly safe pod.
    /// </summary>
    internal static string[] BuildAvailabilityProbeArguments() =>
    [
        "--unshare-user",
        "--unshare-ipc",
        "--unshare-uts",
        "--die-with-parent",
        "--bind", "/proc", "/proc",
        "--ro-bind", "/usr", "/usr",
        "--symlink", "usr/bin", "/bin",
        "--symlink", "usr/lib", "/lib",
        "--symlink", "usr/lib64", "/lib64",
        "--symlink", "usr/sbin", "/sbin",
        "--cap-drop", "ALL",
        "--", "/bin/true",
    ];

    public static bool TryProbeAvailability(out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "Kata bubblewrap isolation is supported only on Linux.";
            return false;
        }
        if (!File.Exists("/usr/bin/setsid"))
        {
            reason = "Kata bubblewrap isolation requires /usr/bin/setsid.";
            return false;
        }
        if (!Directory.Exists("/proc/self"))
        {
            reason = "Kata bubblewrap isolation requires a mounted procfs.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bwrap",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in BuildAvailabilityProbeArguments())
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                reason = "Could not start bwrap.";
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                reason = "bwrap capability probe timed out.";
                return false;
            }

            var stderr = process.StandardError.ReadToEnd().Trim();
            reason = process.ExitCode == 0
                ? "bwrap mount-namespace probe succeeded."
                : $"bwrap mount-namespace probe exited {process.ExitCode}: {stderr}";
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            reason = $"bwrap mount-namespace probe failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns this process's PID-namespace identity (<c>/proc/self/ns/pid</c>), or null when it
    /// cannot be read. The executor sidecar and the AgentHost compare these values at startup so a
    /// deployment that accidentally collapses the two into one PID namespace fails closed.
    /// </summary>
    public static string? TryReadPidNamespace()
    {
        try
        {
            var link = new FileInfo("/proc/self/ns/pid").LinkTarget;
            return string.IsNullOrWhiteSpace(link) ? null : link;
        }
        catch
        {
            return null;
        }
    }

    public Process CreateProcess(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool networkEnabled)
    {
        var command = new SandboxCommand(
            commandLine,
            workingDirectory,
            environment,
            new SandboxFsPolicy([workingDirectory], [], []),
            TimeoutMs: 0,
            NetworkEnabled: networkEnabled);
        return new Process
        {
            StartInfo = BuildProcessStartInfo(command),
            EnableRaisingEvents = true,
        };
    }

    public async Task<SupervisedProcess> StartSupervisedProcessAsync(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool networkEnabled,
        CancellationToken ct = default)
    {
        var command = new SandboxCommand(
            commandLine,
            workingDirectory,
            environment,
            new SandboxFsPolicy([workingDirectory], [], []),
            TimeoutMs: 0,
            NetworkEnabled: networkEnabled);
        var process = new Process
        {
            StartInfo = BuildProcessStartInfo(command, reportChildPid: true),
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start bwrap.");

            var sandboxInitPid = await ReadChildPidAsync(process.StandardOutput, ct)
                .ConfigureAwait(false);
            var workloadProcessGroupId = await ResolveWorkloadProcessGroupAsync(
                    sandboxInitPid,
                    ct)
                .ConfigureAwait(false);
            return new SupervisedProcess(process, sandboxInitPid, workloadProcessGroupId);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures linked-worktree metadata before any model-controlled tool can mutate the worktree.
    /// Later command/preview launches use only this immutable registration and never re-read
    /// sandbox-writable <c>.git</c>/<c>commondir</c> pointer files.
    /// </summary>
    public void RegisterTrustedWorkspace(string workingDirectory)
    {
        var workspace = ValidateMountSource(workingDirectory);
        if (_protectedRoots.Any(root => IsSamePath(workspace, root)))
        {
            throw new SandboxViolationException(
                workspace, string.Join(", ", _protectedRoots),
                "the shared workspace root cannot be registered as a run workspace");
        }

        var metadata = ResolveLinkedWorktreeMounts(workspace, _protectedRoots);
        if (!_trustedWorkspaces.TryAdd(workspace, metadata))
        {
            var registered = _trustedWorkspaces[workspace];
            if (!registered.SequenceEqual(metadata))
            {
                throw new SandboxViolationException(
                    workspace, workspace, "trusted workspace metadata changed after registration");
            }
        }
    }

    /// <summary>
    /// Registers the exact run-scoped HOME created by the platform for a workspace. The mapping is
    /// immutable and is the only source used for HOME/XDG environment values and mounts.
    /// </summary>
    public void RegisterRuntimeHome(string workingDirectory, string runtimeHome)
    {
        var workspace = ValidateMountSource(workingDirectory);
        var home = ValidateMountSource(runtimeHome);
        if (!Directory.Exists(home))
        {
            throw new SandboxViolationException(
                home, workspace, "registered runtime HOME must be a directory");
        }
        if (IsSamePath(home, workspace)
            || IsDescendant(home, workspace)
            || IsDescendant(workspace, home))
        {
            throw new SandboxViolationException(
                home, workspace, "registered runtime HOME must be disjoint from the run workspace");
        }

        foreach (var path in RuntimeHomeDirectories(home))
        {
            if (!Directory.Exists(path))
            {
                throw new SandboxViolationException(
                    path, home, "registered runtime HOME is missing an authoritative XDG directory");
            }
        }

        if (!_runtimeHomes.TryAdd(workspace, home)
            && !IsSamePath(_runtimeHomes[workspace], home))
        {
            throw new SandboxViolationException(
                home, workspace, "registered runtime HOME changed after registration");
        }
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command,
        CancellationToken ct = default)
    {
        var (guardAllowed, guardReason) = SharedWorkspacePathGuard.Inspect(
            command.CommandLine,
            command.FilesystemPolicy.ReadWritePaths
                .Concat(command.FilesystemPolicy.ReadOnlyPaths)
                .Append(command.WorkingDirectory)
                .ToArray(),
            _protectedRoots);
        if (!guardAllowed)
        {
            return new SandboxExecResult(
                126, "", $"Command rejected: {guardReason}", false, false);
        }

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = BuildProcessStartInfo(command),
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            if (!process.Start())
                throw new InvalidOperationException("Failed to start bwrap.");

            const int stdoutCap = 4 * 1024 * 1024;
            const int stderrCap = 1 * 1024 * 1024;
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, stdoutCap, cts.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, stderrCap, cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var (stdout, stdoutTruncated) = await stdoutTask.ConfigureAwait(false);
            var (stderr, stderrTruncated) = await stderrTask.ConfigureAwait(false);
            return new SandboxExecResult(
                process.ExitCode,
                SandboxOutputRedactor.Default.Redact(stdout),
                SandboxOutputRedactor.Default.Redact(stderr),
                false,
                stdoutTruncated || stderrTruncated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.", true, false);
        }
        catch (Win32Exception ex)
        {
            _logger?.LogError(ex, "Kata bwrap isolation could not start; command denied.");
            return new SandboxExecResult(
                126, "", $"Command rejected: Kata filesystem isolation unavailable: {ex.Message}", false, false);
        }
        finally
        {
            if (process is not null && !process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
        }
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct).ConfigureAwait(false);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    internal ProcessStartInfo BuildProcessStartInfo(
        SandboxCommand command,
        bool reportChildPid = false)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Kata bubblewrap isolation requires Linux.");

        var mounts = BuildMountPlan(command);
        var environment = BuildChildEnvironment(command);
        var psi = new ProcessStartInfo
        {
            FileName = "bwrap",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Add(psi,
            "--unshare-user",
            "--unshare-ipc",
            "--unshare-uts",
            "--die-with-parent",
            "--new-session",
            "--cap-drop",
            "ALL");
        if (reportChildPid)
            Add(psi, "--info-fd", "1");
        if (!command.NetworkEnabled)
            Add(psi, "--unshare-net");

        // The executor sidecar container owns the PID namespace (see the type remarks); bind its
        // runtime-provided procfs instead of mounting a nested one, which the kernel refuses in any
        // masked-procfs container. The bound procfs only ever contains this run's sandbox processes.
        Add(psi, "--bind", "/proc", "/proc");
        Add(psi, "--dev", "/dev", "--tmpfs", "/run");
        foreach (var root in SystemReadOnlyRoots.Where(Directory.Exists))
            Add(psi, "--ro-bind", root, root);
        Add(psi,
            "--symlink", "usr/bin", "/bin",
            "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/sbin", "/sbin");
        if (Directory.Exists("/usr/lib64"))
            Add(psi, "--symlink", "usr/lib64", "/lib64");

        foreach (var mount in mounts)
        {
            foreach (var parent in ParentDirectories(mount.Target))
                Add(psi, "--dir", parent);
            Add(psi, mount.ReadOnly ? "--ro-bind" : "--bind", mount.Source, mount.Target);
        }

        Add(psi, "--clearenv");
        foreach (var pair in environment)
            Add(psi, "--setenv", pair.Key, pair.Value);

        Add(psi, "--chdir", Path.GetFullPath(command.WorkingDirectory));
        if (reportChildPid)
            Add(psi, "--", "/usr/bin/setsid", "/bin/bash", "-c", command.CommandLine);
        else
            Add(psi, "--", "/bin/bash", "-c", command.CommandLine);

        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        return psi;
    }

    internal IReadOnlyList<MountSpec> BuildMountPlan(SandboxCommand command)
    {
        var mounts = new List<MountSpec>();
        var (trustedWorkspace, runtimeHome) = ResolveExecutionRegistration(command.WorkingDirectory);

        AddMount(mounts, trustedWorkspace, readOnly: false);
        AddMount(mounts, runtimeHome, readOnly: false);

        foreach (var path in command.FilesystemPolicy.ReadWritePaths)
            AddMount(mounts, path, readOnly: false);
        foreach (var path in command.FilesystemPolicy.ReadOnlyPaths)
            AddMount(mounts, path, readOnly: true);

        foreach (var path in PodPrivateReadWriteRoots.Where(Directory.Exists))
            AddMount(mounts, path, readOnly: false);

        foreach (var mount in _trustedWorkspaces[trustedWorkspace])
            AddMount(mounts, mount.Source, mount.ReadOnly);

        return CollapseMounts(mounts);
    }

    internal IReadOnlyDictionary<string, string> BuildChildEnvironment(SandboxCommand command)
    {
        var (_, runtimeHome) = ResolveExecutionRegistration(command.WorkingDirectory);
        var environment = DefaultEnvironment(runtimeHome);
        if (command.Environment is not null)
        {
            foreach (var pair in command.Environment)
                environment[pair.Key] = pair.Value;
        }

        ApplyAuthoritativeRuntimeHome(environment, runtimeHome);
        return environment;
    }

    private (string Workspace, string RuntimeHome) ResolveExecutionRegistration(
        string workingDirectory)
    {
        var fullWorkingDirectory = ValidateMountSource(workingDirectory);
        if (_protectedRoots.Any(root =>
                IsSamePath(fullWorkingDirectory, root)
                || IsDescendant(root, fullWorkingDirectory)))
        {
            throw new SandboxViolationException(
                fullWorkingDirectory,
                string.Join(", ", _protectedRoots),
                "mounting a protected shared root or its ancestor is not permitted");
        }

        var trustedWorkspace = _trustedWorkspaces.Keys
            .Where(root =>
                IsSamePath(fullWorkingDirectory, root)
                || IsDescendant(fullWorkingDirectory, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        if (trustedWorkspace is null)
        {
            var reason = _protectedRoots.Any(root => IsDescendant(fullWorkingDirectory, root))
                ? "workspace under a protected shared mount was not registered before model execution"
                : "workspace was not registered before model execution";
            throw new SandboxViolationException(
                fullWorkingDirectory,
                string.Join(", ", _protectedRoots),
                reason);
        }

        if (!_runtimeHomes.TryGetValue(trustedWorkspace, out var runtimeHome))
        {
            throw new SandboxViolationException(
                trustedWorkspace,
                trustedWorkspace,
                "runtime HOME was not registered before model execution");
        }

        return (trustedWorkspace, runtimeHome);
    }

    private void AddMount(List<MountSpec> mounts, string path, bool readOnly)
    {
        var fullPath = ValidateMountSource(path);
        if (_protectedRoots.Any(root =>
                IsSamePath(fullPath, root) || IsSameOrDescendant(root, fullPath)))
        {
            throw new SandboxViolationException(
                fullPath,
                string.Join(", ", _protectedRoots),
                "mounting a protected shared root or its ancestor is not permitted");
        }

        mounts.Add(new MountSpec(fullPath, fullPath, readOnly));
    }

    private static string ValidateMountSource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            throw new SandboxViolationException(
                fullPath, fullPath, "filesystem-policy mount root does not exist");
        }

        RejectReparsePointsInPath(fullPath);
        if (File.Exists(fullPath) &&
            new FileInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SandboxViolationException(
                fullPath, fullPath, "filesystem-policy mount file is a symbolic link");
        }
        return SandboxPathValidator.ValidateSandboxRoot(fullPath);
    }

    private static void RejectReparsePointsInPath(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath)
            ?? throw new SandboxViolationException(fullPath, fullPath, "mount root has no filesystem root");
        var relative = Path.GetRelativePath(current, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                continue;
            if (new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new SandboxViolationException(
                    fullPath, fullPath, $"mount root traverses symbolic link or junction '{current}'");
            }
        }
    }

    private static IReadOnlyList<MountSpec> ResolveLinkedWorktreeMounts(
        string workingDirectory,
        IReadOnlyList<string> protectedRoots)
    {
        var dotGit = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(dotGit))
        {
            RejectReparsePointsInPath(dotGit);
            return [new MountSpec(dotGit, dotGit, ReadOnly: true)];
        }
        if (!File.Exists(dotGit))
            return [];

        var pointer = ReadSmallText(dotGit);
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return [];

        var gitDirValue = pointer[prefix.Length..].Trim();
        var gitDirectory = Path.GetFullPath(
            Path.IsPathRooted(gitDirValue)
                ? gitDirValue
                : Path.Combine(workingDirectory, gitDirValue));
        if (!Directory.Exists(gitDirectory))
            throw new SandboxViolationException(dotGit, workingDirectory, "linked-worktree git directory is missing");

        var backlinkFile = Path.Combine(gitDirectory, "gitdir");
        if (!File.Exists(backlinkFile))
            throw new SandboxViolationException(dotGit, workingDirectory, "linked-worktree git backlink is missing");
        var backlinkValue = ReadSmallText(backlinkFile);
        var backlink = Path.GetFullPath(
            Path.IsPathRooted(backlinkValue)
                ? backlinkValue
                : Path.Combine(gitDirectory, backlinkValue));
        if (!IsSamePath(backlink, dotGit))
        {
            throw new SandboxViolationException(
                dotGit, workingDirectory, "linked-worktree git backlink does not identify this workspace");
        }

        var commonDirectory = gitDirectory;
        var commonDirFile = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirFile))
        {
            var commonValue = ReadSmallText(commonDirFile);
            commonDirectory = Path.GetFullPath(
                Path.IsPathRooted(commonValue)
                    ? commonValue
                    : Path.Combine(gitDirectory, commonValue));
        }

        RejectReparsePointsInPath(gitDirectory);
        RejectReparsePointsInPath(commonDirectory);
        var metadataRoot = protectedRoots.FirstOrDefault(root =>
            IsDescendant(gitDirectory, root) && IsDescendant(commonDirectory, root));
        if (metadataRoot is null ||
            !IsDescendant(gitDirectory, Path.Combine(commonDirectory, "worktrees")))
        {
            throw new SandboxViolationException(
                    dotGit,
                    workingDirectory,
                    "linked-worktree metadata is outside the trusted shared repository layout");
        }
        return
        [
            new MountSpec(dotGit, dotGit, ReadOnly: true),
            new MountSpec(gitDirectory, gitDirectory, ReadOnly: true),
            new MountSpec(commonDirectory, commonDirectory, ReadOnly: true),
        ];
    }

    private static IReadOnlyList<MountSpec> CollapseMounts(IEnumerable<MountSpec> mounts)
    {
        var ordered = mounts
            .GroupBy(mount => mount.Source, StringComparer.Ordinal)
            .Select(group => new MountSpec(
                group.Key,
                group.Key,
                group.All(mount => mount.ReadOnly)))
            .OrderBy(mount => mount.Source.Length)
            .ToList();
        var result = new List<MountSpec>();
        foreach (var mount in ordered)
        {
            var ancestor = result.FirstOrDefault(existing => IsDescendant(mount.Source, existing.Source));
            if (ancestor is not null && ancestor.ReadOnly == mount.ReadOnly)
                continue;
            result.Add(mount);
        }
        return result;
    }

    private static Dictionary<string, string> DefaultEnvironment(string runtimeHome) =>
        new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = runtimeHome,
            ["XDG_CACHE_HOME"] = Path.Combine(runtimeHome, ".cache"),
            ["XDG_DATA_HOME"] = Path.Combine(runtimeHome, ".local", "share"),
            ["XDG_CONFIG_HOME"] = Path.Combine(runtimeHome, ".config"),
            ["USER"] = "appuser",
            ["LOGNAME"] = "appuser",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["TMPDIR"] = "/tmp",
            ["TMP"] = "/tmp",
            ["TEMP"] = "/tmp",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = "/dev/null",
        };

    private static void ApplyAuthoritativeRuntimeHome(
        IDictionary<string, string> environment,
        string runtimeHome)
    {
        environment["HOME"] = runtimeHome;
        environment["XDG_CACHE_HOME"] = Path.Combine(runtimeHome, ".cache");
        environment["XDG_DATA_HOME"] = Path.Combine(runtimeHome, ".local", "share");
        environment["XDG_CONFIG_HOME"] = Path.Combine(runtimeHome, ".config");
    }

    private static IEnumerable<string> RuntimeHomeDirectories(string runtimeHome)
    {
        yield return Path.Combine(runtimeHome, ".cache");
        yield return Path.Combine(runtimeHome, ".local", "share");
        yield return Path.Combine(runtimeHome, ".config");
    }

    private static IReadOnlyList<string> ResolveProtectedRoots()
    {
        var configured = Environment.GetEnvironmentVariable(SharedWorkspacePathGuard.ProtectedRootsEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
            return SharedWorkspacePathGuard.DefaultProtectedRoots;

        var roots = configured.Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathRooted)
            .ToArray();
        return roots.Length == 0 ? SharedWorkspacePathGuard.DefaultProtectedRoots : roots;
    }

    private static string ReadSmallText(string path)
    {
        const long maxMetadataBytes = 16 * 1024;
        var info = new FileInfo(path);
        if (info.Length > maxMetadataBytes)
        {
            throw new SandboxViolationException(
                path, path, $"git metadata pointer exceeds {maxMetadataBytes} bytes");
        }
        return File.ReadAllText(path).Trim();
    }

    private static IEnumerable<string> ParentDirectories(string path)
    {
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path);
             !string.IsNullOrEmpty(parent) && parent != Path.GetPathRoot(parent);
             parent = Path.GetDirectoryName(parent))
        {
            parents.Push(parent);
        }
        return parents;
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private static bool IsDescendant(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedRoot, Path.GetPathRoot(normalizedRoot), StringComparison.Ordinal))
            return !IsSamePath(normalizedPath, normalizedRoot);
        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
    }

    private static bool IsSameOrDescendant(string path, string root) =>
        IsSamePath(path, root) || IsDescendant(path, root);

    private static void Add(ProcessStartInfo psi, params string[] arguments)
    {
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
    }

    internal static int ParseChildPid(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
            throw new InvalidOperationException("bwrap did not report its sandbox child PID.");

        try
        {
            using var document = JsonDocument.Parse(info);
            if (document.RootElement.TryGetProperty("child-pid", out var childPid)
                && childPid.TryGetInt32(out var pid)
                && pid > 0)
            {
                return pid;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"bwrap returned invalid child process metadata: {info}", ex);
        }

        throw new InvalidOperationException(
            $"bwrap did not report a valid sandbox child PID: {info}");
    }

    private static async Task<int> ReadChildPidAsync(StreamReader reader, CancellationToken ct)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(TimeSpan.FromSeconds(5));
        var info = new StringBuilder();
        while (info.Length < 16 * 1024)
        {
            var line = await reader.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
            if (line is null)
                break;
            info.AppendLine(line);

            try
            {
                return ParseChildPid(info.ToString());
            }
            catch (InvalidOperationException ex) when (ex.InnerException is JsonException)
            {
                // --info-fd emits pretty-printed JSON; keep reading until the object is complete.
            }
        }

        return ParseChildPid(info.ToString());
    }

    private static async Task<int> ResolveWorkloadProcessGroupAsync(
        int sandboxInitPid,
        CancellationToken ct)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(TimeSpan.FromSeconds(5));
        var childrenPath = $"/proc/{sandboxInitPid}/task/{sandboxInitPid}/children";
        while (true)
        {
            startupCts.Token.ThrowIfCancellationRequested();
            try
            {
                var child = File.ReadAllText(childrenPath)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, out var pid) ? pid : 0)
                    .FirstOrDefault(pid => pid > 0);
                if (child > 0 && IsProcessGroupLeader(child))
                    return child;
            }
            catch (IOException)
            {
                // The bwrap init process may not have forked the workload yet.
            }
            catch (UnauthorizedAccessException)
            {
                // Retry until the startup deadline; production startup is fail-closed.
            }

            await Task.Delay(10, startupCts.Token).ConfigureAwait(false);
        }
    }

    private static bool IsProcessGroupLeader(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                return false;
            var fields = stat[(commandEnd + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 2
                && int.TryParse(fields[2], out var processGroupId)
                && processGroupId == pid;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        int maxBytes,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        var total = 0;
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var remaining = maxBytes - total;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }
            var take = Math.Min(read, remaining);
            output.Append(buffer, 0, take);
            total += take;
            if (take < read)
            {
                truncated = true;
                break;
            }
        }
        return (output.ToString(), truncated);
    }

    internal sealed record MountSpec(string Source, string Target, bool ReadOnly);

    public sealed record SupervisedProcess(
        Process Process,
        int SandboxInitPid,
        int WorkloadProcessGroupId);
}
