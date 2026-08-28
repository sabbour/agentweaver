using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Agentweaver.SandboxFs;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Executes commands directly via the host shell with no process isolation layer.
/// Selected when <c>direct: true</c> is set in <c>.agentweaver/settings.yml</c>,
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

        // #476 — PassthroughExecutor provides NO mount isolation (it is the Kata-mode executor and
        // relies on the pod's VM boundary), so it must consume the per-run filesystem policy itself:
        // reject absolute paths embedded in the command text that reach into the shared /workspace
        // PVC outside this run's own roots. Without this, a command whose declared working directory
        // stays inside the run's tree can still `cat /workspace/<other-project>/secrets` across runs.
        var (guardAllowed, guardReason) = SharedWorkspacePathGuard.Inspect(
            command.CommandLine, BuildAllowedRoots(command));
        if (!guardAllowed)
        {
            _logger?.LogWarning("PassthroughExecutor: rejected command by shared-mount guard: {Reason}", guardReason);
            return new SandboxExecResult(
                126, "", $"Command rejected: {guardReason}", TimedOut: false, OutputTruncated: false);
        }

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

            if (command.DirectExecution is { } directExecution)
            {
                SandboxCommandEnvironment.RemoveInheritedCommandHelperVariables(psi);
                psi.FileName = directExecution.Executable;
                foreach (var argument in directExecution.Arguments)
                    psi.ArgumentList.Add(argument);
            }
            else if (OperatingSystem.IsWindows())
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
            SandboxCommandEnvironment.ApplyToProcessStartInfo(psi, command.Environment);
            SandboxCommandEnvironment.ApplyToProcessStartInfo(psi, command.DirectExecution?.Environment);

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

    private static IReadOnlyList<string> BuildAllowedRoots(SandboxCommand command)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            roots.Add(command.WorkingDirectory);
        roots.AddRange(command.FilesystemPolicy.ReadWritePaths);
        roots.AddRange(command.FilesystemPolicy.ReadOnlyPaths);
        return roots;
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
