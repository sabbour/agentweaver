using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Executes sandboxed commands on a native Linux host using lxc-exec directly
/// (no WSL wrapper). Intended for cloud deployment scenarios (M1).
/// </summary>
internal sealed class LinuxNativeMxcSandboxExecutor : ISandboxExecutor
{
    private readonly ILogger _logger;
    private readonly string _lxcExecPath;

    public bool IsRealIsolation => true;
    public string BackendName => "lxc-native-linux";
    public string SelectionReason => $"Native Linux lxc-exec at {_lxcExecPath}.";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal LinuxNativeMxcSandboxExecutor(ILogger logger)
    {
        _logger = logger;
        _lxcExecPath = IsLxcAvailable()
            ?? throw new InvalidOperationException(
                "lxc-exec is not available; use LinuxNativeMxcSandboxExecutor.IsLxcAvailable() before constructing.");
    }

    /// <summary>
    /// Probes for lxc-exec at known absolute paths. Returns the path if found, null otherwise.
    /// PATH is never consulted.
    /// </summary>
    internal static string? IsLxcAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var candidates = new[]
        {
            "/usr/local/bin/lxc-exec",
            "/usr/bin/lxc-exec",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static FilesystemPolicy BuildMxcFilesystemPolicy(SandboxFsPolicy policy) =>
        new()
        {
            ReadwritePaths = [.. policy.ReadWritePaths],
            ReadonlyPaths = [.. policy.ReadOnlyPaths],
            DeniedPaths = [.. policy.DeniedPaths],
        };

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        var mxcPolicy = new SandboxPolicy
        {
            Version = "0.4.0-alpha",
            Network = new NetworkPolicy { AllowOutbound = false },
            Filesystem = BuildMxcFilesystemPolicy(command.FilesystemPolicy),
        };

        // Build the ContainerConfig via the SDK, then serialize + base64-encode it.
        // The command is carried inside the config blob — never passed as a raw argument
        // to prevent command injection (F8).
        ContainerConfig config = MxcSdk.BuildSandboxPayload(
            command.CommandLine,
            mxcPolicy,
            command.WorkingDirectory,
            null,
            "process");

        var json = JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        _logger.LogDebug("Executing sandbox command via {Backend}, length={Length}", BackendName, command.CommandLine.Length);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _lxcExecPath,
                Arguments = $"--experimental --config-base64 {b64}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    $"Failed to start {_lxcExecPath} process.");

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
