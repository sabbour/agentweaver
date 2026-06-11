using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Executes sandboxed commands on a native Linux host using bubblewrap (bwrap).
/// Used when lxc-exec is not available but bwrap is installed.
/// The workspace is bound read-write; /usr and selected /etc files are read-only.
/// Ubuntu ARM64 symlinks (bin→usr/bin etc.) are recreated — no /lib64 assumed.
/// Verified on Ubuntu 24.04 aarch64.
/// </summary>
internal sealed class LinuxBwrapExecutor : ISandboxExecutor
{
    private readonly ILogger _logger;

    public bool IsRealIsolation => true;
    public string BackendName => "linux-bwrap";
    public string SelectionReason => "Native Linux bubblewrap (bwrap): workspace-confined fs + PID namespace isolation.";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal LinuxBwrapExecutor(ILogger logger)
    {
        _logger = logger;
    }

    internal static bool IsBwrapAvailable()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bwrap",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";

    internal static string BuildBwrapPayload(string command, string workdir, bool networkEnabled = false)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        var wd = ShellSingleQuote(workdir);
        // Replace broad --ro-bind /usr /usr with targeted mounts (Phase 6 alignment).
        // /usr/share, /usr/include, /usr/src etc. are NOT needed at runtime.
        // --ro-bind-try is used for all mounts so missing paths are silently skipped
        // (graceful handling of minimal container environments).
        return
            "exec bwrap" +
            $" --bind {wd} /workspace" +
            " --ro-bind-try /usr/bin /usr/bin" +
            " --ro-bind-try /usr/lib /usr/lib" +
            " --ro-bind-try /usr/lib64 /usr/lib64" +
            " --ro-bind-try /usr/local/bin /usr/local/bin" +
            " --ro-bind-try /usr/local/lib /usr/local/lib" +
            " --ro-bind-try /etc/resolv.conf /etc/resolv.conf" +
            " --ro-bind-try /etc/passwd /etc/passwd" +
            " --ro-bind-try /etc/group /etc/group" +
            " --ro-bind-try /etc/nsswitch.conf /etc/nsswitch.conf" +
            " --symlink usr/bin /bin" +
            " --symlink usr/lib /lib" +
            " --symlink usr/sbin /sbin" +
            " --proc /proc" +
            " --dev /dev" +
            " --tmpfs /tmp" +
            " --tmpfs /home" +
            " --tmpfs /root" +
            " --chdir /workspace" +
            " --unshare-pid" +
            " --unshare-user" +
            (networkEnabled ? "" : " --unshare-net") +
            " --new-session" +
            $" -- /bin/bash -c \"$(printf %s '{b64}' | base64 -d)\"";
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        var payload = BuildBwrapPayload(command.CommandLine, command.WorkingDirectory, command.NetworkEnabled);
        _logger.LogDebug("Executing sandbox command via {Backend}, length={Length}",
            BackendName, command.CommandLine.Length);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(payload);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bwrap process.");

            const int stdoutCap = 4 * 1024 * 1024;
            const int stderrCap = 1 * 1024 * 1024;
            var stdoutTask = ReadBoundedAsync(proc.StandardOutput, stdoutCap, cts.Token);
            var stderrTask = ReadBoundedAsync(proc.StandardError, stderrCap, cts.Token);

            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var (stdout, stdoutTrunc) = await stdoutTask;
            var (stderr, stderrTrunc)  = await stderrTask;

            // Redact host worktree path before it can leak to the model.
            stdout = stdout.Replace(command.WorkingDirectory, "/workspace", StringComparison.Ordinal);
            stderr = stderr.Replace(command.WorkingDirectory, "/workspace", StringComparison.Ordinal);

            stdout = SandboxOutputRedactor.Default.Redact(stdout);
            stderr = SandboxOutputRedactor.Default.Redact(stderr);

            return new SandboxExecResult(proc.ExitCode, stdout, stderr,
                TimedOut: false, OutputTruncated: stdoutTrunc || stderrTrunc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.", TimedOut: true, OutputTruncated: false);
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
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
        yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(
        System.IO.StreamReader reader, int maxBytes, CancellationToken ct)
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
