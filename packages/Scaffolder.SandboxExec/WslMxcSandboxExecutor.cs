using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;
using Sabbour.Mxc.Sdk.Sandbox;

namespace Scaffolder.SandboxExec;

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

    public bool IsRealIsolation => _backend != ContainmentBackend.WslUnshare || true;

    public string BackendName => _backend switch
    {
        ContainmentBackend.WslBubblewrap => "wsl-bwrap",
        ContainmentBackend.WslUnshare => "wsl-unshare",
        _ => "wsl-unknown",
    };

    public string SelectionReason => _backend switch
    {
        ContainmentBackend.WslBubblewrap =>
            "WSL2 with bubblewrap (bwrap): workspace-confined filesystem + PID namespace isolation.",
        ContainmentBackend.WslUnshare =>
            "WSL2 with unshare: user/mount/PID namespace isolation (no filesystem confinement).",
        _ =>
            "WSL2 backend.",
    };

    // None of the WSL2 wrappers enforce a network allowlist — outbound is unrestricted.
    public bool HasNetworkWarning => true;

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
