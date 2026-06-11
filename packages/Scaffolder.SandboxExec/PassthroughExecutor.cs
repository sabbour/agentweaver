using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Executes commands directly via the host shell with no process isolation layer.
/// Selected when <c>direct: true</c> is set in <c>.scaffolder/settings.yml</c>,
/// or used as the fallback when no isolation backend is available.
/// Relies on deployment-level isolation (e.g. a container or restricted environment).
/// </summary>
public sealed class PassthroughExecutor : ISandboxExecutor
{
    private readonly ILogger? _logger;

    public bool IsRealIsolation => false;
    public string BackendName => "direct";
    public string SelectionReason { get; }
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    public PassthroughExecutor(string reason, ILogger? logger = null)
    {
        SelectionReason = reason;
        _logger = logger;
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        _logger?.LogDebug("PassthroughExecutor: running command length={Length}", command.CommandLine.Length);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = command.WorkingDirectory,
            };

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command.CommandLine);
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command.CommandLine);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (command.TimeoutMs > 0)
                cts.CancelAfter(command.TimeoutMs);

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start shell process.");

            const int cap = 4 * 1024 * 1024;
            var stdoutTask = ReadBoundedAsync(proc.StandardOutput, cap, cts.Token);
            var stderrTask = ReadBoundedAsync(proc.StandardError, cap / 4, cts.Token);

            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var (stdout, stdoutTrunc) = await stdoutTask;
            var (stderr, _) = await stderrTask;

            stdout = SandboxOutputRedactor.Default.Redact(stdout);
            stderr = SandboxOutputRedactor.Default.Redact(stderr);

            return new SandboxExecResult(proc.ExitCode, stdout, stderr,
                TimedOut: false, OutputTruncated: stdoutTrunc);
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

