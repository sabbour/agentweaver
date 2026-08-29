using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// Wire contract between the AgentHost container and the in-pod executor sidecar
/// (<see cref="PodExecServer"/>). One request per connection, newline-delimited JSON in both
/// directions, over a Unix domain socket on a pod-private <c>emptyDir</c>.
///
/// The socket is never bound into a sandboxed child's mount namespace, so model-controlled
/// processes cannot reach it. Every request additionally carries the pod-private token that the
/// server writes next to the socket at startup (mode 0600).
/// </summary>
public static class PodExecOps
{
    public const string Probe = "probe";
    public const string RegisterWorkspace = "register-workspace";
    public const string RegisterHome = "register-home";
    public const string Exec = "exec";
    public const string Spawn = "spawn";
    public const string Ports = "ports";
    public const string Stop = "stop";
    public const string Capabilities = "capabilities";
}

/// <summary>Frame type discriminators emitted by <see cref="PodExecServer"/>.</summary>
public static class PodExecFrameTypes
{
    public const string Result = "result";
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
    public const string Exit = "exit";
    public const string Started = "started";
    public const string Ack = "ack";
    public const string Ports = "ports";
    public const string Probe = "probe";
    public const string Error = "error";
    public const string Capabilities = "capabilities";
}

/// <summary>One capability as reported over the executor protocol.</summary>
public sealed record PodExecCapability
{
    public string Id { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? Remediation { get; init; }
}

/// <summary>A single executor-sidecar request.</summary>
public sealed record PodExecRequest
{
    public string Op { get; init; } = string.Empty;
    public string? Token { get; init; }
    public string? Handle { get; init; }
    public string? Workspace { get; init; }
    public string? Home { get; init; }
    public string? CommandLine { get; init; }
    public string? DirectExecutable { get; init; }
    public List<string>? DirectArguments { get; init; }
    public Dictionary<string, string>? DirectEnvironment { get; init; }
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
    public List<string>? ReadWritePaths { get; init; }
    public List<string>? ReadOnlyPaths { get; init; }
    public int TimeoutMs { get; init; }
    public bool NetworkEnabled { get; init; }
    public int GraceMs { get; init; } = 5000;

    /// <summary>
    /// The caller's <c>/proc/self/ns/pid</c> link value. The probe fails closed when it matches the
    /// sidecar's own PID namespace, which is what a mis-set <c>shareProcessNamespace: true</c> or a
    /// single-container deployment would produce.
    /// </summary>
    public string? CallerPidNamespace { get; init; }
}

/// <summary>A single response frame. Streaming ops emit many; one-shot ops emit one.</summary>
public sealed record PodExecFrame
{
    public string Type { get; init; } = string.Empty;
    public string? Data { get; init; }
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Truncated { get; init; }
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public string? Message { get; init; }
    public int ProcessGroupId { get; init; }
    public List<int>? Ports { get; init; }
    public bool Ok { get; init; }
    public string? Detail { get; init; }
    public List<PodExecCapability>? Capabilities { get; init; }
}

/// <summary>Shared serialization settings — compact, camelCase, no indentation.</summary>
public static class PodExecJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Filesystem locations of the pod-private executor IPC endpoint.</summary>
public static class PodExecEndpoint
{
    public const string SocketPathEnvVar = "AGENTWEAVER_EXEC_SOCKET";
    public const string DefaultDirectory = "/var/run/agentweaver-exec";
    public const string SocketFileName = "exec.sock";
    public const string TokenFileName = "exec.token";

    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>
    /// Filesystem types that cannot carry a cross-container <c>AF_UNIX</c> rendezvous point.
    ///
    /// <para>Regression detector for #1008. AKS upgraded the katapool node image on 2026-08-27,
    /// bringing Kata 3.32.0, where upstream flipped <c>disable_guest_empty_dir</c> from <c>false</c>
    /// to <c>true</c>. A default <c>emptyDir</c> therefore stopped being a directory the guest agent
    /// creates and became a host directory re-exported over virtio-fs with a per-container share
    /// path. A pathname socket is matched by inode identity in the connecting task's own kernel, so
    /// the peer container sees the file with <c>S_ISSOCK</c> set and still gets <c>ECONNREFUSED</c>.
    /// The failure is silent, which is why it is worth naming at startup.</para>
    /// </summary>
    private static readonly string[] SharedFilesystemTypes =
        ["virtiofs", "fuse.virtiofs", "virtio-fs", "9p", "fuse.9p", "fuse"];

    /// <summary>Operator-facing remediation for a socket directory that cannot host the rendezvous.</summary>
    public const string RendezvousRemediation =
        "put the executor IPC volume on a filesystem the guest owns — for Kata, an 'emptyDir' with "
        + "'medium: Memory' (see k8s/base/sandbox-template-agenthost.yaml), which Kata handles "
        + "independently of 'disable_guest_empty_dir'.";

    public static string ResolveSocketPath(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var fromEnvironment = System.Environment.GetEnvironmentVariable(SocketPathEnvVar);
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.Combine(DefaultDirectory, SocketFileName)
            : fromEnvironment;
    }

    public static string ResolveTokenPath(string socketPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(socketPath)) ?? DefaultDirectory,
            TokenFileName);

    /// <summary>The directory that holds the socket and its token.</summary>
    public static string ResolveDirectory(string socketPath) =>
        Path.GetDirectoryName(Path.GetFullPath(socketPath)) ?? DefaultDirectory;

    /// <summary>
    /// Reports whether <paramref name="socketPath"/> lives on a filesystem that can host a
    /// cross-container <c>AF_UNIX</c> rendezvous point. Returns <c>true</c> when the answer cannot
    /// be determined (no procfs, unreadable mount table, non-Linux host): the check exists to catch
    /// a known-broken pod configuration, never to invent a new reason to refuse service.
    /// </summary>
    public static bool CanHostCrossContainerRendezvous(string socketPath, out string detail)
    {
        detail = string.Empty;
        string mountInfo;
        try
        {
            if (!File.Exists(MountInfoPath))
                return true;
            mountInfo = File.ReadAllText(MountInfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        var directory = ResolveRealPath(ResolveDirectory(socketPath));
        var filesystemType = ResolveFilesystemType(mountInfo, directory);
        if (filesystemType is null)
            return true;

        if (!SharedFilesystemTypes.Contains(filesystemType, StringComparer.OrdinalIgnoreCase))
        {
            detail = filesystemType;
            return true;
        }

        detail =
            $"the executor IPC directory '{directory}' is on '{filesystemType}', a shared filesystem "
            + $"that cannot host a cross-container AF_UNIX socket (issue #1008): {RendezvousRemediation}";
        return false;
    }

    /// <summary>
    /// Resolves the filesystem type backing <paramref name="path"/> from the contents of
    /// <c>/proc/self/mountinfo</c> by longest mount-point prefix. Internal so the shared-filesystem
    /// classification can be asserted against captured, real mount tables.
    /// </summary>
    internal static string? ResolveFilesystemType(string mountInfo, string path)
    {
        string? best = null;
        var bestLength = -1;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "36 35 0:59 / /run/exec rw,relatime shared:1 - virtiofs none rw": optional fields sit
            // between the mount point and the " - " separator, so split on the separator first.
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 5 || right.Length < 1)
                continue;

            var mountPoint = Unescape(left[4]);
            if (!IsPathWithin(path, mountPoint) || mountPoint.Length <= bestLength)
                continue;
            bestLength = mountPoint.Length;
            best = right[0];
        }

        return best;
    }

    private static bool IsPathWithin(string path, string mountPoint)
    {
        if (mountPoint == "/")
            return true;
        if (!path.StartsWith(mountPoint, StringComparison.Ordinal))
            return false;
        return path.Length == mountPoint.Length || path[mountPoint.Length] == '/';
    }

    /// <summary>mountinfo octal-escapes space, tab, newline and backslash.</summary>
    private static string Unescape(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);

    /// <summary>
    /// Resolves symlinked ancestors so the mount-table lookup matches. The pod mounts the IPC
    /// directory at <c>/var/run/agentweaver-exec</c>, and <c>/var/run</c> is a symlink to
    /// <c>/run</c>, so the mount is recorded as <c>/run/agentweaver-exec</c>.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var current = "/";
        foreach (var segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var target = (FileSystemInfo?)Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                    ?? File.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target is not null)
                    current = target.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unresolvable ancestor just means the literal path is the best answer available.
            }
        }

        return current;
    }
}
