using Microsoft.Extensions.Logging;
using Sabbour.Mxc.Sdk;

namespace Agentweaver.SandboxExec;

/// <summary>
/// Selects the appropriate ISandboxExecutor implementation based on the current
/// platform and available backends. Selection order:
///   1. Windows: MxcSandboxExecutor (processcontainer)
///   2. Windows: WslMxcSandboxExecutor (WSL2 + bwrap/unshare)
///   3. Linux:   LinuxBwrapExecutor (selective bubblewrap mounts)
///   4. Linux:   LinuxNativeMxcSandboxExecutor (lxc-exec direct)
///   5. Fallback: PassthroughExecutor (direct execution, no isolation)
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
            // Use the SDK's platform probe — same approach as Windows.
            // ComputeLinuxSupport() checks bwrap + lxc in PATH.
            // The bundled lxc-exec is also checked as a separate fallback.
            var support = MxcSdk.GetPlatformSupport();

            // Prefer bubblewrap: our bwrap executor uses a selective mount allowlist.
            if (LinuxBwrapExecutor.IsBwrapAvailable() ||
                support.AvailableMethods.Contains(ContainmentBackend.Bubblewrap))
            {
                logger.LogInformation(
                    "SandboxExecutorFactory: selected linux-bwrap.");
                return new LinuxBwrapExecutor(logger);
            }

            // Fall back to lxc only when bwrap is unavailable.
            var lxcPath = LinuxNativeMxcSandboxExecutor.IsLxcAvailable();
            if (lxcPath != null || support.AvailableMethods.Contains(ContainmentBackend.Lxc))
            {
                logger.LogInformation(
                    "SandboxExecutorFactory: selected lxc-native-linux (SDK tier={Tier}).",
                    support.IsolationTier);
                return new LinuxNativeMxcSandboxExecutor(logger);
            }

            logger.LogWarning(
                "SandboxExecutorFactory: GetPlatformSupport() found no usable Linux backend " +
                "(methods={Methods}). Falling through to passthrough.",
                string.Join(",", support.AvailableMethods));
        }

        var reason = OperatingSystem.IsWindows()
            ? "No isolation backend available (processcontainer unsupported, WSL2 not found)."
            : "No isolation backend available (bwrap/lxc-exec not found on this Linux host).";

        logger.LogWarning(
            "SandboxExecutorFactory: falling back to PassthroughExecutor. Reason: {Reason}",
            reason);

        return new PassthroughExecutor(reason);
    }

    /// <summary>
    /// Creates a direct passthrough executor that runs commands with no isolation.
    /// Suitable for unit tests and environments where no real isolation backend is
    /// available; relies on deployment-level isolation instead.
    /// </summary>
    public static ISandboxExecutor CreatePassthrough(
        string reason = "no-isolation: passthrough-direct") =>
        new PassthroughExecutor(reason);

    /// <summary>
    /// True when the process is running inside a Kubernetes cluster
    /// (<c>KUBERNETES_SERVICE_HOST</c> environment variable is set by the kubelet).
    /// When true, the API project overrides this factory's result with
    /// <c>KubernetesSandboxExecutor</c> via a later DI registration.
    /// </summary>
    public static bool IsInCluster =>
        Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is not null;
}
