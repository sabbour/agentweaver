using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;
using Sabbour.Mxc.Sdk.Sandbox;

namespace Scaffolder.SandboxExec;

internal sealed class MxcSandboxExecutor : ISandboxExecutor
{
    private readonly string _binaryPath;
    private readonly ILogger _logger;
    private readonly SandboxPolicyEnrichment _enrichment;

    public bool IsRealIsolation => true;
    public string BackendName => "processcontainer";
    public string SelectionReason { get; }
    public bool HasNetworkWarning => OperatingSystem.IsWindows();
    public string? NetworkWarningMessage => OperatingSystem.IsWindows()
        ? "Sandbox running with unrestricted network on Windows (allowlist enforcement unavailable). Data exfiltration surface is open."
        : null;

    private MxcSandboxExecutor(
        string selectionReason,
        string binaryPath,
        SandboxPolicyEnrichment enrichment,
        ILogger logger)
    {
        SelectionReason = selectionReason;
        _binaryPath = binaryPath;
        _enrichment = enrichment;
        _logger = logger;
    }

    internal static bool TryCreate(
        ILogger logger,
        [NotNullWhen(true)] out MxcSandboxExecutor? executor)
    {
        executor = null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

        string? binaryPath = null;

        // Priority 1: MXC_BIN_DIR env var — must be an absolute path.
        var mxcBinDir = Environment.GetEnvironmentVariable("MXC_BIN_DIR");
        if (!string.IsNullOrEmpty(mxcBinDir))
        {
            if (!Path.IsPathRooted(mxcBinDir))
            {
                logger.LogWarning(
                    "MXC_BIN_DIR is not an absolute path; ignoring: {Path}", mxcBinDir);
            }
            else
            {
                var candidate = Path.Combine(mxcBinDir, arch, "wxc-exec.exe");
                if (File.Exists(candidate))
                    binaryPath = candidate;
                else
                    logger.LogWarning(
                        "MXC_BIN_DIR is set but wxc-exec.exe not found at: {Path}", candidate);
            }
        }

        // Priority 2: Assembly-adjacent bin\<arch>\wxc-exec.exe.
        if (binaryPath is null)
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            var candidate = Path.Combine(asmDir, "bin", arch, "wxc-exec.exe");
            if (File.Exists(candidate))
                binaryPath = candidate;
        }

        if (binaryPath is null)
        {
            logger.LogWarning(
                "wxc-exec.exe not found via MXC_BIN_DIR or assembly-adjacent bin\\{Arch}\\; " +
                "MxcSandboxExecutor unavailable. Set MXC_BIN_DIR to the directory containing " +
                "the mxc release binaries.", arch);
            return false;
        }

        // Integrity check: SHA-256 manifest if present.
        var manifestPath = Path.ChangeExtension(binaryPath, ".sha256");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestContent = File.ReadAllText(manifestPath).Trim();
                var expected = manifestContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                    .ToLowerInvariant();
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(binaryPath);
                var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"wxc-exec.exe SHA-256 mismatch. Expected: {expected}, Got: {actual}. " +
                        "The binary may be tampered or corrupted.");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to verify wxc-exec.exe SHA-256 manifest: {ex.Message}", ex);
            }
        }
        else
        {
            logger.LogWarning(
                "No wxc-exec.sha256 manifest found next to binary at {Path}; " +
                "skipping integrity check.", binaryPath);
        }

        // Platform support probe.
        PlatformSupport support;
        try
        {
            support = MxcSdk.GetPlatformSupport();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "MxcSdk.GetPlatformSupport() failed; MxcSandboxExecutor unavailable.");
            return false;
        }

        if (!support.IsSupported)
        {
            logger.LogInformation(
                "mxc platform not supported on this host: {Reason}", support.Reason);
            return false;
        }

        // The BaseContainer tier (Tier 1) requires ViVeTool velocity keys 61389575 and
        // 61155944 on Windows 11 25H2+. The probe reports the tier from the installed DLL
        // but cannot verify whether the velocity keys are active — that only fails at
        // runtime with E_NOTIMPL. Rather than silently selecting an executor that will
        // fail every run, refuse BaseContainer and let the factory fall through to WSL2
        // (which provides real lxc isolation and works unconditionally on this OS build).
        // Users who enable the velocity keys will get BaseContainer back: the probe will
        // return a higher tier (AppContainerBfs or AppContainerDacl) instead.
        if (support.IsolationTier == Sabbour.Mxc.Sdk.IsolationTier.BaseContainer)
        {
            logger.LogInformation(
                "mxc probe reports base-container tier which requires ViVeTool velocity keys " +
                "61389575+61155944 (not enabled on this host). Falling through to WSL2 backend. " +
                "Enable the velocity keys to use BaseContainer.");
            return false;
        }

        executor = new MxcSandboxExecutor(
            support.Reason ?? "processcontainer supported",
            binaryPath,
            SandboxPolicyEnrichment.BuildForWindows(),
            logger);
        return true;
    }

    private FilesystemPolicy BuildMxcFilesystemPolicy(SandboxFsPolicy policy) =>
        new()
        {
            ReadwritePaths = [.. policy.ReadWritePaths],
            ReadonlyPaths = [.. policy.ReadOnlyPaths],
            DeniedPaths = [.. policy.DeniedPaths],
        };

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        // Build policy with enrichment (cached at construction). The enrichment provides a
        // selective tool-path allowlist via PolicyDiscovery.GetAvailableToolsPolicy(), replacing
        // the broad /usr bind that Copilot CLI does NOT use (Phase 6 alignment).
        var enrichedFsPolicy = SandboxFsPolicyBuilder.Build(
            command.WorkingDirectory,
            Array.Empty<string>(),
            _enrichment);

        // Merge in any additional RW/RO paths from the command's explicit filesystem policy.
        var mergedRw = enrichedFsPolicy.ReadWritePaths.Union(command.FilesystemPolicy.ReadWritePaths).ToList();
        var mergedRo = enrichedFsPolicy.ReadOnlyPaths.Union(command.FilesystemPolicy.ReadOnlyPaths).ToList();
        var mergedPolicy = new SandboxFsPolicy(mergedRw, mergedRo, command.FilesystemPolicy.DeniedPaths);

        var policy = new SandboxPolicy
        {
            // Schema 0.5.0-alpha: improved path normalization over 0.4.0-alpha.
            Version = "0.5.0-alpha",
            Network = new NetworkPolicy { AllowOutbound = command.NetworkEnabled },
            Filesystem = new FilesystemPolicy
            {
                ReadwritePaths = [.. mergedPolicy.ReadWritePaths],
                ReadonlyPaths = [.. mergedPolicy.ReadOnlyPaths],
                DeniedPaths = [.. mergedPolicy.DeniedPaths],
                ClearPolicyOnExit = true,
            },
            TimeoutMs = command.TimeoutMs > 0 ? command.TimeoutMs : null,
        };

        var opts = new SandboxSpawnOptions
        {
            UsePty = false,
            ExecutablePath = _binaryPath,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (command.TimeoutMs > 0)
            cts.CancelAfter(command.TimeoutMs);

        try
        {
            var result = await MxcSdk.SpawnSandboxAsync(
                command.CommandLine,
                policy,
                opts,
                command.WorkingDirectory,
                cancellationToken: cts.Token);

            var stdout = StripExecutorArtifact(result.Stdout ?? "");
            var stderr = result.Stderr ?? "";

            const int cap = 4 * 1024 * 1024;
            var truncated = false;
            if (stdout.Length > cap)
            {
                stdout = stdout[..cap];
                truncated = true;
            }

            stdout = SandboxOutputRedactor.Default.Redact(stdout);
            stderr = SandboxOutputRedactor.Default.Redact(stderr);

            return new SandboxExecResult(result.ExitCode, stdout, stderr,
                TimedOut: false, OutputTruncated: truncated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SandboxExecResult(-1, "", "Timed out.",
                TimedOut: true, OutputTruncated: false);
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

    // Strip the executor path artifact from stdout (GAP-2).
    // Some builds of the Mxc SDK append the executor binary path as a trailing stdout line.
    private static string StripExecutorArtifact(string stdout)
    {
        var lines = stdout.Split('\n').ToList();
        while (lines.Count > 0)
        {
            var last = lines[^1].Trim();
            if (last.Length > 0
                && (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || last.TrimEnd('\r').EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                && (last.Contains('\\') || last.Contains('/')))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else
            {
                break;
            }
        }
        return string.Join('\n', lines);
    }
}
