using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;

namespace Scaffolder.SandboxExec;

internal sealed class WslMxcSandboxExecutor : ISandboxExecutor
{
    private readonly ILogger _logger;
    private readonly string _lxcExecWslPath;

    public bool IsRealIsolation => true;
    public string BackendName => "wsl-lxc";
    public string SelectionReason => "WSL2 with lxc-exec isolation.";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal WslMxcSandboxExecutor(ILogger logger, string lxcExecWslPath)
    {
        _logger = logger;
        _lxcExecWslPath = lxcExecWslPath;
    }

    internal static bool IsWslAvailable()
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
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0) return false;
            // lxc-exec must be resolvable — either bundled (via WSL2 mount) or in WSL2 PATH.
            return ResolveLxcExecWslPath() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the lxc-exec binary path as seen from inside WSL2. Checks:
    ///   1. Assembly-adjacent bin/&lt;arch&gt;/lxc-exec (bundled Linux ELF, accessed via WSL2 /mnt/ mount).
    ///   2. lxc-exec in WSL2 PATH (e.g. already installed at /usr/local/bin/lxc-exec).
    /// Returns null when neither is available.
    /// </summary>
    internal static string? ResolveLxcExecWslPath()
    {
        // Priority 1: bundled Linux binary accessed via WSL2 /mnt/ mount.
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => "x64",
        };
        var asmDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        var winPath = System.IO.Path.Combine(asmDir, "bin", arch, "lxc-exec");
        if (System.IO.File.Exists(winPath))
        {
            var wslPath = MapToLinuxPath(winPath);
            // Verify WSL2 can actually reach and execute it.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = $"-- bash -c \"chmod +x '{wslPath}' && '{wslPath}' --version 2>/dev/null; echo ok\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null) goto tryPath;
                proc.WaitForExit(8000);
                var output = proc.StandardOutput.ReadToEnd();
                if (output.Contains("ok")) return wslPath;
            }
            catch { /* fall through */ }
        }

        tryPath:
        // Priority 2: lxc-exec in WSL2 PATH.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-- which lxc-exec",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(path) ? path : null;
        }
        catch { return null; }
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

    private static FilesystemPolicy BuildMxcFilesystemPolicy(SandboxFsPolicy policy, bool mapPaths)
    {
        IEnumerable<string> Map(IReadOnlyList<string> paths) =>
            mapPaths ? paths.Select(MapToLinuxPath) : paths;

        return new FilesystemPolicy
        {
            ReadwritePaths = [.. Map(policy.ReadWritePaths)],
            ReadonlyPaths = [.. Map(policy.ReadOnlyPaths)],
            DeniedPaths = [.. Map(policy.DeniedPaths)],
        };
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        var mappedWorkingDir = MapToLinuxPath(command.WorkingDirectory);

        var mxcPolicy = new SandboxPolicy
        {
            Version = "0.4.0-alpha",
            Network = new NetworkPolicy { AllowOutbound = false },
            Filesystem = BuildMxcFilesystemPolicy(command.FilesystemPolicy, mapPaths: true),
        };

        // Build the ContainerConfig via the SDK, then serialize + base64-encode it.
        // The command is carried inside the config blob — never passed as a raw wsl.exe argument
        // to prevent command injection (F8).
        ContainerConfig config = MxcSdk.BuildSandboxPayload(
            command.CommandLine,
            mxcPolicy,
            mappedWorkingDir,
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
                FileName = "wsl.exe",
                Arguments = $"-- '{_lxcExecWslPath}' --experimental --config-base64 {b64}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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
