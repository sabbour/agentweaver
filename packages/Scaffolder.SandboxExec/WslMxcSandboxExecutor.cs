using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Sandbox backend discovered inside WSL2.
/// </summary>
internal enum WslSandboxBackend
{
    /// <summary>bubblewrap (bwrap) — filesystem + PID namespace isolation. Real isolation.</summary>
    Bwrap,

    /// <summary>unshare — user/mount/PID namespace isolation (no fs confinement). Real isolation.</summary>
    Unshare,

    /// <summary>No isolation tool available — run directly in WSL2 with a warning.</summary>
    Direct,
}

/// <summary>
/// Executes sandboxed commands inside WSL2 by wrapping the user command with a
/// discovered isolation tool (bwrap or unshare). Runs via
/// <c>wsl.exe -- bash -c &lt;wrapper&gt;</c>.
///
/// UPGRADE PATH — WSL 2.8.1+:
/// When WSL 2.8.x ships as a public release (currently only tagged on GitHub,
/// not yet in Windows Update / winget as of June 2026), this executor should
/// be replaced with <c>MxcSdk.SpawnSandboxAsync</c> using containment type
/// <c>"wslc"</c> (ContainmentBackend.Wslc). That path goes fully through the
/// mxc SDK and uses the Wslc SDK (wslcsdk.dll) to spawn OCI containers inside
/// WSL2 without needing a separate lxc-exec or bwrap invocation.
/// See: https://github.com/microsoft/WSL/releases/tag/2.8.11
/// Detected via: <c>PlatformSupport.AvailableMethods.Contains(SandboxingMethod.Wslc)</c>.
/// </summary>
internal sealed class WslMxcSandboxExecutor : ISandboxExecutor
{
    private readonly ILogger _logger;
    private readonly WslSandboxBackend _backend;

    public bool IsRealIsolation => _backend != WslSandboxBackend.Direct;

    public string BackendName => _backend switch
    {
        WslSandboxBackend.Bwrap => "wsl-bwrap",
        WslSandboxBackend.Unshare => "wsl-unshare",
        _ => "wsl-direct",
    };

    public string SelectionReason => _backend switch
    {
        WslSandboxBackend.Bwrap =>
            "WSL2 with bubblewrap (bwrap): workspace-confined filesystem + PID namespace isolation.",
        WslSandboxBackend.Unshare =>
            "WSL2 with unshare: user/mount/PID namespace isolation (no filesystem confinement).",
        _ =>
            "WSL2 without an isolation tool (bwrap/unshare not found): running directly with no isolation.",
    };

    // None of the WSL2 wrappers enforce a network allowlist — outbound is unrestricted
    // so that common tools (git, curl) keep working. Surface this as a warning.
    public bool HasNetworkWarning => true;

    public string? NetworkWarningMessage =>
        "WSL2 sandbox does not enforce a network allowlist; outbound network access is unrestricted.";

    internal WslMxcSandboxExecutor(ILogger logger, WslSandboxBackend backend)
    {
        _logger = logger;
        _backend = backend;
    }

    /// <summary>
    /// Probes for WSL2 and a usable isolation backend. Returns a configured executor,
    /// or null when wsl.exe is not available.
    /// </summary>
    internal static WslMxcSandboxExecutor? TryCreate(ILogger logger)
    {
        if (!IsWslAvailable())
            return null;

        var backend = DetectBackend();
        if (backend == WslSandboxBackend.Direct)
        {
            logger.LogWarning(
                "WslMxcSandboxExecutor: no isolation tool found in WSL2 (bwrap/unshare). " +
                "Falling back to direct execution with no isolation.");
        }
        else
        {
            logger.LogDebug("WslMxcSandboxExecutor: selected backend {Backend}.", backend);
        }

        return new WslMxcSandboxExecutor(logger, backend);
    }

    private static bool IsWslAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--status",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Discovers the best isolation tool available inside WSL2.
    /// Preference: bwrap (fs confinement) &gt; unshare (namespace isolation) &gt; direct.
    /// </summary>
    internal static WslSandboxBackend DetectBackend()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "if command -v bwrap >/dev/null 2>&1; then echo bwrap; " +
                "elif command -v unshare >/dev/null 2>&1; then echo unshare; " +
                "else echo direct; fi");

            using var proc = Process.Start(psi);
            if (proc is null) return WslSandboxBackend.Direct;
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return WslSandboxBackend.Direct;
            }

            var token = output.Trim();
            return token switch
            {
                "bwrap" => WslSandboxBackend.Bwrap,
                "unshare" => WslSandboxBackend.Unshare,
                _ => WslSandboxBackend.Direct,
            };
        }
        catch (Exception)
        {
            return WslSandboxBackend.Direct;
        }
    }

    // Maps a Windows absolute path to its WSL2 /mnt/<drive>/... equivalent.
    private static string MapToLinuxPath(string windowsPath)
    {
        if (windowsPath.Length >= 3
            && windowsPath[1] == ':'
            && (windowsPath[2] == '\\' || windowsPath[2] == '/'))
        {
            var drive = char.ToLowerInvariant(windowsPath[0]);
            var rest = windowsPath[3..].Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }
        return windowsPath.Replace('\\', '/');
    }

    // Safely single-quotes a string for embedding in a bash command.
    private static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Builds the bash payload (passed to <c>bash -c</c>) that runs the user command
    /// under the selected isolation backend. The user command is base64-encoded and
    /// decoded inside WSL2 to avoid any shell-injection through the command string.
    /// </summary>
    private static string BuildSandboxedCommand(
        string command, string workdirLinux, WslSandboxBackend backend)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        return backend switch
        {
            WslSandboxBackend.Bwrap => BuildBwrapCommand(b64, workdirLinux),
            WslSandboxBackend.Unshare => BuildUnshareCommand(b64, workdirLinux),
            _ => BuildDirectCommand(b64, workdirLinux),
        };
    }

    // bubblewrap: confine fs to the workspace, mount /usr + /etc read-only, recreate the
    // /bin, /lib, /sbin symlinks (Ubuntu ARM64 has /bin->usr/bin etc., NO /lib64), give a
    // private /proc, /dev and /tmp, and isolate the PID namespace. Verified on
    // Ubuntu 24.04 aarch64 WSL2 (bwrap 0.9.0).
    private static string BuildBwrapCommand(string b64, string workdirLinux)
    {
        var wd = ShellSingleQuote(workdirLinux);
        return
            "exec bwrap" +
            $" --bind {wd} {wd}" +
            " --ro-bind /usr /usr" +
            " --ro-bind /etc /etc" +
            " --symlink usr/bin /bin" +
            " --symlink usr/lib /lib" +
            " --symlink usr/sbin /sbin" +
            " --proc /proc" +
            " --dev /dev" +
            " --tmpfs /tmp" +
            $" --chdir {wd}" +
            " --unshare-pid" +
            " --new-session" +
            $" -- /bin/bash -c \"$(printf %s '{b64}' | base64 -d)\"";
    }

    // unshare: user/mount/PID namespace isolation. Does not confine the filesystem to the
    // workspace, so we cd into it first. Verified on Ubuntu 24.04 aarch64 WSL2.
    private static string BuildUnshareCommand(string b64, string workdirLinux)
    {
        var wd = ShellSingleQuote(workdirLinux);
        return
            $"cd {wd} && exec unshare --user --map-root-user --mount --pid --fork" +
            $" /bin/bash -c \"$(printf %s '{b64}' | base64 -d)\"";
    }

    // Direct: no isolation. Only used when neither bwrap nor unshare is present.
    private static string BuildDirectCommand(string b64, string workdirLinux)
    {
        var wd = ShellSingleQuote(workdirLinux);
        return $"cd {wd} && /bin/bash -c \"$(printf %s '{b64}' | base64 -d)\"";
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        var workdirLinux = MapToLinuxPath(command.WorkingDirectory);
        var payload = BuildSandboxedCommand(command.CommandLine, workdirLinux, _backend);

        _logger.LogDebug(
            "Executing sandbox command via {Backend}, length={Length}",
            BackendName, command.CommandLine.Length);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(payload);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start wsl.exe process.");

            const int stdoutMaxBytes = 4 * 1024 * 1024;
            const int stderrMaxBytes = 1 * 1024 * 1024;
            var stdoutTask = ReadBoundedAsync(proc.StandardOutput, stdoutMaxBytes, cts.Token);
            var stderrTask = ReadBoundedAsync(proc.StandardError, stderrMaxBytes, cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                throw;
            }

            var (stdout, stdoutTruncated) = await stdoutTask;
            var (stderr, stderrTruncated) = await stderrTask;
            var truncated = stdoutTruncated || stderrTruncated;

            stdout = SandboxOutputRedactor.Default.Redact(stdout);
            stderr = SandboxOutputRedactor.Default.Redact(stderr);

            return new SandboxExecResult(proc.ExitCode, stdout, stderr,
                TimedOut: false, OutputTruncated: truncated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.",
                TimedOut: true, OutputTruncated: false);
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            }
            proc?.Dispose();
        }
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(
            SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        int total = 0;
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int remaining = maxBytes - total;
            if (remaining <= 0) { truncated = true; break; }
            int take = Math.Min(read, remaining);
            sb.Append(buffer, 0, take);
            total += take;
            if (take < read) { truncated = true; break; }
        }
        return (sb.ToString(), truncated);
    }
}
