using System.Net.Sockets;
using System.Text;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// The AgentHost-side relay for a long-lived sandboxed process. It is started by
/// <see cref="PodExecSandboxClient.StartSupervisedProcessAsync"/>, reads one spawn request from its
/// stdin, holds the supervising connection to the executor sidecar for the lifetime of the sandboxed
/// process, and mirrors the sandboxed process's stdout/stderr and exit code as its own.
///
/// <para>Keeping a real local child process means the preview lifecycle (output pumping, exit
/// detection, teardown on AgentHost shutdown) is unchanged, while the sandboxed process itself lives
/// in the executor sidecar's namespaces. Killing the relay drops the connection, which the sidecar
/// treats as an order to terminate the sandboxed process group.</para>
/// </summary>
public static class PodExecRelay
{
    public const string RelayArgument = "--exec-relay";

    /// <summary>
    /// First line the relay writes to its own stdout once the sidecar confirms the sandboxed
    /// process actually started (issue #849: <see cref="PodExecSandboxClient.StartSupervisedProcessAsync"/>
    /// used to return successfully — and report a real, but meaningless, local relay PID — the moment
    /// the relay process itself launched, without ever checking whether the sidecar's spawn (bwrap
    /// startup, sandbox-child resolution) actually succeeded. A spawn that failed or a sidecar that was
    /// still resolving the sandbox child left the caller believing the preview process was live while
    /// no port, log line, or process ever existed on the executor side. This marker — and
    /// <see cref="HandshakeErrorMarkerPrefix"/> — let the caller block on the sidecar's own
    /// <c>started</c>/<c>error</c> frame before reporting success.</para>
    /// </summary>
    public const string HandshakeReadyMarker = "\u0001AGENTWEAVER_PODEXEC_READY\u0001";

    /// <summary>
    /// Prefix for the single handshake line the relay writes when the sidecar reports (or the
    /// connection ends before reporting) that the spawn failed. The remainder of the line is the
    /// failure detail.
    /// </summary>
    public const string HandshakeErrorMarkerPrefix = "\u0001AGENTWEAVER_PODEXEC_ERROR\u0001:";

    /// <summary>Runs the relay loop. Returns the sandboxed process's exit code.</summary>
    public static async Task<int> RunAsync(string? socketPath, CancellationToken ct = default)
    {
        var resolvedSocketPath = PodExecEndpoint.ResolveSocketPath(socketPath);
        var requestLine = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            await Console.Error.WriteLineAsync("executor relay: no spawn request on stdin").ConfigureAwait(false);
            return 126;
        }

        var request = PodExecJson.Deserialize<PodExecRequest>(requestLine);
        if (request is null)
        {
            await Console.Error.WriteLineAsync("executor relay: malformed spawn request").ConfigureAwait(false);
            return 126;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(resolvedSocketPath), ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var authenticated = request with { Token = ReadToken(resolvedSocketPath) };
        await writer.WriteLineAsync(PodExecJson.Serialize(authenticated).AsMemory(), ct).ConfigureAwait(false);

        // Emits the handshake line exactly once, whether the sidecar confirmed the spawn started or
        // reported/implied a failure. The caller (StartSupervisedProcessAsync) reads this single line
        // before it ever begins treating StandardOutput as forwarded workload logs, so it must be
        // written before ANY real stdout frame is relayed.
        var handshakeSent = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var frame = PodExecJson.Deserialize<PodExecFrame>(line);
            if (frame is null)
                continue;

            switch (frame.Type)
            {
                case PodExecFrameTypes.Started:
                    if (!handshakeSent)
                    {
                        handshakeSent = true;
                        await Console.Out.WriteLineAsync(HandshakeReadyMarker).ConfigureAwait(false);
                        await Console.Out.FlushAsync(ct).ConfigureAwait(false);
                    }
                    break;
                case PodExecFrameTypes.Stdout:
                    await Console.Out.WriteLineAsync(frame.Data ?? string.Empty).ConfigureAwait(false);
                    await Console.Out.FlushAsync(ct).ConfigureAwait(false);
                    break;
                case PodExecFrameTypes.Stderr:
                    await Console.Error.WriteLineAsync(frame.Data ?? string.Empty).ConfigureAwait(false);
                    await Console.Error.FlushAsync(ct).ConfigureAwait(false);
                    break;
                case PodExecFrameTypes.Exit:
                    return frame.ExitCode;
                case PodExecFrameTypes.Error:
                    if (!handshakeSent)
                    {
                        handshakeSent = true;
                        await Console.Out.WriteLineAsync(HandshakeErrorMarkerPrefix + (frame.Message ?? "unknown executor error"))
                            .ConfigureAwait(false);
                        await Console.Out.FlushAsync(ct).ConfigureAwait(false);
                    }
                    await Console.Error.WriteLineAsync($"executor relay: {frame.Message}").ConfigureAwait(false);
                    return 126;
            }
        }

        // The sidecar closed the connection without ever confirming the spawn: treat it as a failed
        // sandbox and make sure the caller — which is blocked reading the handshake line — hears about
        // it instead of hanging until its own timeout.
        if (!handshakeSent)
        {
            await Console.Out.WriteLineAsync(
                    HandshakeErrorMarkerPrefix + "executor sidecar closed the connection before confirming the sandboxed process started")
                .ConfigureAwait(false);
            await Console.Out.FlushAsync(ct).ConfigureAwait(false);
        }
        return 126;
    }

    /// <summary>
    /// Reads the pod-private executor token written next to the socket. Absence is fatal — the
    /// caller fails closed rather than talking to an unauthenticated endpoint.
    /// </summary>
    public static string ReadToken(string socketPath)
    {
        var tokenPath = PodExecEndpoint.ResolveTokenPath(socketPath);
        if (!File.Exists(tokenPath))
            throw new FileNotFoundException($"Executor sidecar token '{tokenPath}' is not available.", tokenPath);
        return File.ReadAllText(tokenPath).Trim();
    }
}
