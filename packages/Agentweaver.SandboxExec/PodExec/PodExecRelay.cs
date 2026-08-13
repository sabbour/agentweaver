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

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var frame = PodExecJson.Deserialize<PodExecFrame>(line);
            if (frame is null)
                continue;

            switch (frame.Type)
            {
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
                    await Console.Error.WriteLineAsync($"executor relay: {frame.Message}").ConfigureAwait(false);
                    return 126;
            }
        }

        // The sidecar closed the connection without a terminal frame: treat it as a failed sandbox.
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
