using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;
using Sabbour.Mxc.Sdk.Sandbox;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Executes sandboxed commands inside WSL2 via <see cref="MxcSdk.SpawnWsl2SandboxAsync"/>.
/// Backend detection delegates to <see cref="MxcSdk.GetWsl2PlatformSupport"/>.
///
/// UPGRADE PATH — WSL 2.8.1+:
/// When WSL 2.8.x ships as a public release (currently only tagged on GitHub,
/// not yet in Windows Update / winget as of June 2026), this executor should
/// be replaced with <c>MxcSdk.SpawnSandboxAsync</c> using containment type
/// <c>"wslc"</c> (ContainmentBackend.Wslc). That path goes fully through the
/// mxc SDK and uses the Wslc SDK (wslcsdk.dll) to spawn OCI containers inside
/// WSL2 without needing a separate lxc-exec or bwrap invocation.
/// See: https://github.com/microsoft/WSL/releases/tag/2.8.11
/// Detected via: <c>MxcSdk.GetPlatformSupport().AvailableMethods.Contains(ContainmentBackend.Wslc)</c>.
/// </summary>
internal sealed class WslMxcSandboxExecutor : ISandboxExecutor
{
    private readonly ILogger _logger;
    private readonly ContainmentBackend _backend;

    public bool IsRealIsolation => _backend != ContainmentBackend.WslUnshare;

    public string BackendName => _backend switch
    {
        ContainmentBackend.WslBubblewrap => "wsl-bwrap",
        ContainmentBackend.WslUnshare => "wsl-unshare",
        _ => "wsl-unknown",
    };

    public string SelectionReason => _backend switch
    {
        ContainmentBackend.WslBubblewrap =>
            "WSL2 with bubblewrap (bwrap): workspace-confined filesystem + PID/user/network namespace isolation.",
        ContainmentBackend.WslUnshare =>
            "WSL2 with unshare: user/mount/PID namespace isolation (no filesystem confinement).",
        _ =>
            "WSL2 backend.",
    };

    public bool HasNetworkWarning => _backend != ContainmentBackend.WslBubblewrap;

    public string? NetworkWarningMessage =>
        "WSL2 sandbox does not enforce a network allowlist; outbound network access is unrestricted.";

    internal WslMxcSandboxExecutor(ILogger logger, ContainmentBackend backend)
    {
        _logger = logger;
        _backend = backend;
    }

    /// <summary>
    /// Probes for WSL2 and a usable isolation backend via the SDK.
    /// Returns a configured executor, or null when wsl.exe is not available or no tools found.
    /// </summary>
    internal static WslMxcSandboxExecutor? TryCreate(ILogger logger)
    {
        var support = MxcSdk.GetWsl2PlatformSupport();
        if (!support.IsSupported)
            return null;

        ContainmentBackend backend;
        if (support.AvailableMethods.Contains(ContainmentBackend.WslBubblewrap))
        {
            backend = ContainmentBackend.WslBubblewrap;
        }
        else if (support.AvailableMethods.Contains(ContainmentBackend.WslUnshare))
        {
            backend = ContainmentBackend.WslUnshare;
        }
        else
        {
            logger.LogWarning(
                "WslMxcSandboxExecutor: WSL2 is available but no isolation tool (bwrap/unshare) found.");
            return null;
        }

        logger.LogDebug("WslMxcSandboxExecutor: selected backend {Backend}.", backend);
        return new WslMxcSandboxExecutor(logger, backend);
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Executing sandbox command via {Backend}, length={Length}",
            BackendName, command.CommandLine.Length);

        if (_backend == ContainmentBackend.WslBubblewrap)
            return await ExecuteBwrapAsync(command, ct).ConfigureAwait(false);

        var policy = new SandboxPolicy
        {
            Version = "0.6.0-alpha",
            Filesystem = new FilesystemPolicy
            {
                ReadwritePaths = [command.WorkingDirectory],
            },
        };

        SandboxProcessResult result;
        try
        {
            result = await MxcSdk.SpawnWsl2SandboxAsync(
                command.CommandLine,
                policy,
                workingDirectory: command.WorkingDirectory,
                backend: _backend,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.",
                TimedOut: true, OutputTruncated: false);
        }

        var stdout = SandboxOutputRedactor.Default.Redact(result.Stdout);
        var stderr = SandboxOutputRedactor.Default.Redact(result.Stderr);

        return new SandboxExecResult(result.ExitCode, stdout, stderr,
            TimedOut: false, OutputTruncated: false);
    }

    internal static string BuildBwrapCommand(string command, bool networkEnabled = false)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
        // Replace broad --ro-bind /usr /usr with targeted mounts (Phase 6 alignment).
        return
            "wd=$(pwd -P); exec bwrap" +
            " --bind \"$wd\" /workspace" +
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

    private async Task<SandboxExecResult> ExecuteBwrapAsync(
        SandboxCommand command, CancellationToken ct)
    {
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
            psi.ArgumentList.Add("--cd");
            psi.ArgumentList.Add(command.WorkingDirectory);
            psi.ArgumentList.Add("--exec");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(BuildBwrapCommand(command.CommandLine, command.NetworkEnabled));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start wsl.exe process.");

            const int stdoutCap = 4 * 1024 * 1024;
            const int stderrCap = 1 * 1024 * 1024;
            var stdoutTask = ReadBoundedAsync(proc.StandardOutput, stdoutCap, cts.Token);
            var stderrTask = ReadBoundedAsync(proc.StandardError, stderrCap, cts.Token);

            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var (stdout, stdoutTrunc) = await stdoutTask.ConfigureAwait(false);
            var (stderr, stderrTrunc) = await stderrTask.ConfigureAwait(false);

            stdout = SandboxOutputRedactor.Default.Redact(stdout);
            stderr = SandboxOutputRedactor.Default.Redact(stderr);

            return new SandboxExecResult(proc.ExitCode, stdout, stderr,
                TimedOut: false, OutputTruncated: stdoutTrunc || stderrTrunc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.",
                TimedOut: true, OutputTruncated: false);
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
                try { proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        int total = 0;
        bool truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
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

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct).ConfigureAwait(false);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(
            SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }
}
