using System.Diagnostics;

namespace Agentweaver.SandboxExec;

/// <summary>Stable identifiers for the developer workloads a sandbox may be asked to run.</summary>
public static class SandboxCapabilityIds
{
    public const string NpmInstall = "npm_install";
    public const string NuGetRestore = "nuget_restore";
    public const string AptInstall = "apt_install";
    public const string ImageBuild = "image_build";
    public const string PreviewPortBinding = "preview_port_binding";
    public const string WingetInstall = "winget_install";
}

/// <summary>How a capability behaves on the executor that reported it.</summary>
public enum SandboxCapabilityState
{
    /// <summary>Available now on this executor, and covered by an executed acceptance test.</summary>
    Supported,

    /// <summary>The executor could support it, but a required component is missing right now.</summary>
    Unavailable,

    /// <summary>
    /// Structurally impossible on this operating system or isolation backend. The caller must not
    /// retry: it needs a different executor backend (for example a Windows executor for winget).
    /// </summary>
    UnsupportedOnPlatform,

    /// <summary>
    /// Not performed by this executor by design; a separate, differently-privileged component is
    /// required (image builds need a BuildKit sidecar, which the sandbox container is not).
    /// </summary>
    RequiresExternalService,
}

/// <summary>One capability, its state, why it is in that state, and what would change it.</summary>
/// <param name="Id">A <see cref="SandboxCapabilityIds"/> value.</param>
/// <param name="State">The state as measured on this executor.</param>
/// <param name="Detail">Why the capability is in that state, in reviewer-readable terms.</param>
/// <param name="Remediation">
/// The concrete path to support when <see cref="State"/> is not <see cref="SandboxCapabilityState.Supported"/>;
/// <c>null</c> when it is supported.
/// </param>
public sealed record SandboxCapability(
    string Id,
    SandboxCapabilityState State,
    string Detail,
    string? Remediation = null)
{
    public bool IsSupported => State == SandboxCapabilityState.Supported;
}

/// <summary>
/// The capability contract a sandbox executor publishes, so callers (and agents) discover what a run
/// can actually do instead of failing halfway through a task — and so unsupported operations are
/// declared explicitly rather than silently omitted.
///
/// <para><b>winget.</b> The Linux Kata executor cannot run winget: winget is a Windows-only package
/// manager (an MSIX-packaged App Installer component built on Windows APIs), and the sandbox is a
/// Linux container inside a Linux Kata VM. This is not a policy restriction that can be relaxed by
/// adding a capability or a mount — it needs a Windows executor backend running on Windows nodes,
/// selected per OS. Until that backend exists, the contract reports
/// <see cref="SandboxCapabilityState.UnsupportedOnPlatform"/> with the remediation, and callers can
/// branch on it. See <c>docs/deep-dive/sandbox-pod-execution.md</c> ("Capability contract").</para>
/// </summary>
public static class SandboxCapabilityProbe
{
    /// <summary>Marker file that the executor sidecar's writable-system-root helper is installed.</summary>
    public const string RunRootHelperPath = "/usr/local/bin/awx-run-root";

    /// <summary>
    /// Describes the capabilities of the executor backend named <paramref name="backendName"/>.
    /// </summary>
    /// <param name="backendName">The <see cref="ISandboxExecutor.BackendName"/> of the live executor.</param>
    /// <param name="writableSystemRootAvailable">
    /// Whether the executor can give a run a writable system root (required by apt-get/dpkg).
    /// </param>
    /// <param name="imageBuildEndpoint">
    /// The BuildKit endpoint the deployment configured for image builds, or <c>null</c> when image
    /// builds are not wired up.
    /// </param>
    public static IReadOnlyList<SandboxCapability> Describe(
        string backendName,
        bool writableSystemRootAvailable,
        string? imageBuildEndpoint = null)
    {
        var linux = OperatingSystem.IsLinux();
        var capabilities = new List<SandboxCapability>
        {
            new(SandboxCapabilityIds.NpmInstall,
                SandboxCapabilityState.Supported,
                "npm installs into the run's own workspace over the sandbox's HTTPS egress; no system root write is needed."),
            new(SandboxCapabilityIds.NuGetRestore,
                SandboxCapabilityState.Supported,
                "dotnet restore writes to the run's workspace and per-run NuGet cache over the sandbox's HTTPS egress."),
            new(SandboxCapabilityIds.PreviewPortBinding,
                SandboxCapabilityState.Supported,
                "A preview app binds a loopback port inside the sandbox; AgentHost forwards it to the preview Gateway, "
                + "because both containers of the run's pod share one network namespace."),
        };

        capabilities.Add(writableSystemRootAvailable
            ? new SandboxCapability(
                SandboxCapabilityIds.AptInstall,
                SandboxCapabilityState.Supported,
                "apt-get/dpkg run against a per-run writable system root: /usr and /var are overlays whose upper "
                + "layers live on a size-bounded tmpfs inside the run's own user namespace, and /etc is a per-run copy. "
                + "Installed packages persist for the run and are discarded with it; no other run, the AgentHost "
                + "container, or the node ever sees them.")
            : new SandboxCapability(
                SandboxCapabilityIds.AptInstall,
                SandboxCapabilityState.Unavailable,
                "The system root is read-only for this executor, so dpkg cannot take its lock.",
                $"Deploy an image that ships {RunRootHelperPath} and run on a kernel that allows unprivileged "
                + "user namespaces and tmpfs-backed overlay mounts."));

        capabilities.Add(string.IsNullOrWhiteSpace(imageBuildEndpoint)
            ? new SandboxCapability(
                SandboxCapabilityIds.ImageBuild,
                SandboxCapabilityState.RequiresExternalService,
                "No build daemon socket is present in this sandbox pod, so image builds are unavailable. "
                + "buildkitd cannot run in the sandbox container itself: it must create cgroups and perform "
                + "mounts for every build step, which requires CAP_SYS_ADMIN and CAP_NET_ADMIN, and the sandbox "
                + "container holds no capabilities at all (CapEff=0). Rootless buildkitd is not an escape hatch "
                + "here either: it needs a sub-UID range, and mapping a range requires newuidmap to carry file "
                + "capabilities, which the Kata guest filesystem cannot store (setcap reports 'Not supported').",
                "Patch the optional builder sidecar into the sandbox template "
                + "(k8s/optional/sandbox-buildkit-sidecar.yaml). It requires a namespace whose PodSecurity level "
                + "admits CAP_SYS_ADMIN, CAP_NET_ADMIN and CAP_SYS_PTRACE for that one container; 'baseline' does "
                + "not.")
            : new SandboxCapability(
                SandboxCapabilityIds.ImageBuild,
                SandboxCapabilityState.Supported,
                $"Builds run on a BuildKit sidecar in this pod, reached over the pod-local socket {imageBuildEndpoint}. "
                + "Because the builder is inside the run's own Kata VM, its cache, history and content store are "
                + "scoped to this pod and are not reachable from any other run — there is no shared daemon and no "
                + "network credential to hand out. IMPORTANT LIMIT: build steps get their own empty network "
                + "namespace (loopback only), so a Dockerfile line that needs the network — RUN apt-get install, "
                + "RUN npm install — will fail with a DNS or connection error. Install dependencies in the sandbox "
                + "shell, which has normal pod networking, and COPY the result into the image. Base images still "
                + "pull, because the daemon fetches them itself. The sidecar is rootful (uid 0, CAP_SYS_ADMIN + "
                + "CAP_NET_ADMIN + CAP_SYS_PTRACE) because rootless BuildKit cannot work under Kata, but it is not "
                + "privileged, has no host namespaces, holds no service-account token or workload identity, and "
                + "runs under RuntimeDefault seccomp, and its capabilities are confined to the guest kernel. Build "
                + "steps themselves are runc-confined to the default capability set, and the daemon refuses the "
                + "security.insecure entitlement. Residual risk: a run reaching this socket "
                + "drives a root-capable daemon inside its own VM, so a buildkitd vulnerability would be a "
                + "root-in-guest compromise — bounded by the Kata VM, with no node access and no other run."));

        capabilities.Add(linux
            ? new SandboxCapability(
                SandboxCapabilityIds.WingetInstall,
                SandboxCapabilityState.UnsupportedOnPlatform,
                $"winget is a Windows-only package manager and cannot execute on the Linux executor '{backendName}'. "
                + "No capability, mount or policy change makes a Windows MSIX installer runnable in a Linux Kata VM.",
                "Use apt-get for Linux runs, or run the task on a Windows executor backend (Windows nodes with a "
                + "Windows sandbox implementation), selected by the run's target operating system.")
            : new SandboxCapability(
                SandboxCapabilityIds.WingetInstall,
                SandboxCapabilityState.Unavailable,
                "winget requires a Windows executor backend, which is not implemented yet.",
                "Implement and select the Windows executor backend for Windows runs."));

        return capabilities;
    }

    /// <summary>
    /// Whether this container can actually build a per-run writable system root. Checks the helper
    /// and the two kernel features it needs, so the contract reports measured state rather than an
    /// assumption.
    /// </summary>
    public static bool ProbeWritableSystemRoot(out string detail)
    {
        if (!OperatingSystem.IsLinux())
        {
            detail = "Writable system roots require Linux user namespaces.";
            return false;
        }
        if (!File.Exists(RunRootHelperPath))
        {
            detail = $"{RunRootHelperPath} is not installed in this image.";
            return false;
        }

        try
        {
            // Prove the namespace + tmpfs + overlay chain works here rather than trusting the kernel
            // version: Kata guest kernels differ from node kernels, and a failure must be visible as
            // an unavailable capability instead of a mid-run apt-get error.
            var psi = new ProcessStartInfo
            {
                FileName = "unshare",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "--user", "--map-root-user", "--mount", "--",
                         "/bin/sh", "-c",
                         "mount -t tmpfs -o size=8m tmpfs /mnt && mkdir -p /mnt/u /mnt/w && "
                         + "mount -t overlay probe -o lowerdir=/usr,upperdir=/mnt/u,workdir=/mnt/w /usr && "
                         + "touch /usr/.awx-probe",
                     })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                detail = "Could not start the writable-system-root probe.";
                return false;
            }
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { }
                detail = "The writable-system-root probe timed out.";
                return false;
            }
            if (process.ExitCode != 0)
            {
                detail = $"The writable-system-root probe failed: {stderr.Trim()}";
                return false;
            }

            detail = "User namespace, tmpfs and overlay mounts are available for a per-run writable system root.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"The writable-system-root probe could not run: {ex.Message}";
            return false;
        }
    }
}
