using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Agentweaver.SandboxFs;
using Microsoft.Extensions.Logging;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Executes commands in a private bubblewrap mount namespace inside the run's Kata VM.
/// The Kata VM remains the pod boundary; bubblewrap removes the shared PVC root and binds
/// only this run's declared roots into the child process namespace.
/// </summary>
public sealed class KataBwrapExecutor : ISandboxExecutor
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
    public string BackendName => "kata-bwrap-fs";
    public string SelectionReason =>
        "Kata VM plus a fail-closed bubblewrap mount namespace scoped to this run.";
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
    public static bool TryProbeAvailability(out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "Kata bubblewrap isolation is supported only on Linux.";
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
            foreach (var argument in new[]
                     {
                         "--unshare-user",
                         "--unshare-pid",
                         "--die-with-parent",
                         "--tmpfs", "/proc",
                         "--ro-bind", "/usr", "/usr",
                         "--symlink", "usr/bin", "/bin",
                         "--symlink", "usr/lib", "/lib",
                         "--symlink", "usr/lib64", "/lib64",
                         "--symlink", "usr/sbin", "/sbin",
                         "--cap-drop", "ALL",
                         "--", "/bin/true",
                     })
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

    internal ProcessStartInfo BuildProcessStartInfo(SandboxCommand command)
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
            "--unshare-pid",
            "--unshare-ipc",
            "--unshare-uts",
            "--die-with-parent",
            "--new-session",
            "--cap-drop",
            "ALL");
        if (!command.NetworkEnabled)
            Add(psi, "--unshare-net");

        Add(psi, "--tmpfs", "/proc");
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
}
