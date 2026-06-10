using Microsoft.Extensions.Logging;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Selects the appropriate ISandboxExecutor implementation based on the current
/// platform and available backends. Selection order:
///   1. Windows: MxcSandboxExecutor (processcontainer)
///   2. Windows: WslMxcSandboxExecutor (WSL2 + bwrap/unshare)
///   3. Linux:   LinuxNativeMxcSandboxExecutor (lxc-exec direct)
///   4. Fallback: PassthroughExecutor (deny-by-default)
/// </summary>
public static class SandboxExecutorFactory
{
    public static ISandboxExecutor Create(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            if (MxcSandboxExecutor.TryCreate(logger, out var mxc))
                return mxc!;

            var wsl = WslMxcSandboxExecutor.TryCreate(logger);
            if (wsl != null)
                return wsl;
        }

        if (OperatingSystem.IsLinux())
        {
            if (LinuxNativeMxcSandboxExecutor.IsLxcAvailable() != null)
                return new LinuxNativeMxcSandboxExecutor(logger);
        }

        var reason = OperatingSystem.IsWindows()
            ? "No isolation backend available (processcontainer unsupported, WSL2 not found)."
            : "No isolation backend available (lxc-exec not found on this Linux host).";

        logger.LogWarning(
            "SandboxExecutorFactory: falling back to PassthroughExecutor. Reason: {Reason}",
            reason);

        return new PassthroughExecutor(reason);
    }

    /// <summary>
    /// Creates a deny-by-default passthrough executor. Suitable for unit tests and
    /// environments where no real isolation backend is available.
    /// </summary>
    public static ISandboxExecutor CreatePassthrough(
        string reason = "no-isolation: passthrough-deny") =>
        new PassthroughExecutor(reason);
}
