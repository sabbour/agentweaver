using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// Wire contract between the AgentHost container and the in-pod executor sidecar
/// (<see cref="PodExecServer"/>). One request per connection, newline-delimited JSON in both
/// directions, over the selected pod-private transport.
///
/// The token directory is never bound into a sandboxed child's mount namespace, so
/// model-controlled processes cannot authenticate to either transport. Every request carries the
/// pod-private token that the server writes there at startup (mode 0600).
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

/// <summary>Transport used only between the AgentHost and executor containers in one pod.</summary>
public enum PodExecTransport
{
    UnixDomainSocket,
    LoopbackTcp,
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
    public const string TransportEnvVar = "AGENTWEAVER_EXEC_TRANSPORT";
    public const string TcpPortEnvVar = "AGENTWEAVER_EXEC_TCP_PORT";
    public const string DefaultDirectory = "/var/run/agentweaver-exec";
    public const string SocketFileName = "exec.sock";
    public const string TokenFileName = "exec.token";
    public const int DefaultTcpPort = 18081;

    public static string ResolveSocketPath(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var fromEnvironment = System.Environment.GetEnvironmentVariable(SocketPathEnvVar);
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.Combine(DefaultDirectory, SocketFileName)
            : fromEnvironment;
    }

    /// <summary>
    /// Resolves the in-pod transport. Unix sockets remain the default for existing non-Kata
    /// deployments; Kata manifests explicitly select authenticated loopback TCP.
    /// </summary>
    public static PodExecTransport ResolveTransport(string? configured = null)
    {
        var value = configured ?? Environment.GetEnvironmentVariable(TransportEnvVar);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "unix", StringComparison.OrdinalIgnoreCase)
            ? PodExecTransport.UnixDomainSocket
            : string.Equals(value, "tcp", StringComparison.OrdinalIgnoreCase)
                ? PodExecTransport.LoopbackTcp
                : throw new InvalidOperationException(
                    $"{TransportEnvVar} must be 'unix' or 'tcp' when configured.");
    }

    /// <summary>Resolves the loopback-only TCP listener port and rejects invalid nonprivileged ports.</summary>
    public static int ResolveTcpPort(int? configured = null)
    {
        var value = configured?.ToString() ?? Environment.GetEnvironmentVariable(TcpPortEnvVar);
        if (string.IsNullOrWhiteSpace(value))
            return DefaultTcpPort;
        if (!int.TryParse(value, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException(
                $"{TcpPortEnvVar} must be an integer between 1024 and 65535 when configured.");
        return port;
    }

    public static string ResolveTokenPath(string socketPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(socketPath)) ?? DefaultDirectory,
            TokenFileName);
}

/// <summary>Creates authenticated PodExec transport sockets without permitting non-loopback TCP.</summary>
internal static class PodExecTransportConnection
{
    public static Socket CreateListener(
        PodExecTransport transport,
        string socketPath,
        int tcpPort)
    {
        if (transport == PodExecTransport.UnixDomainSocket)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            return listener;
        }

        var tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcpListener.Bind(new IPEndPoint(IPAddress.Loopback, tcpPort));
        return tcpListener;
    }

    public static Task ConnectAsync(
        Socket socket,
        PodExecTransport transport,
        string socketPath,
        int tcpPort,
        CancellationToken ct) =>
        transport == PodExecTransport.UnixDomainSocket
            ? socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).AsTask()
            : socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, tcpPort), ct).AsTask();

    public static Socket CreateClient(PodExecTransport transport) =>
        transport == PodExecTransport.UnixDomainSocket
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
}
