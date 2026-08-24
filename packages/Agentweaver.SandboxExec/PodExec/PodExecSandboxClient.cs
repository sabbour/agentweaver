using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// AgentHost-side <see cref="ISandboxExecutor"/> that forwards every model-controlled command to the
/// executor sidecar container of the same Kata pod (<see cref="PodExecServer"/>).
///
/// <para>Nothing the model can influence is ever executed in the AgentHost container: the sidecar
/// owns the PID namespace, the mount namespace and the bubblewrap boundary, so an injected agent
/// cannot see, signal, trace, or read the environment of the AgentHost process that holds the run's
/// brokered GitHub token.</para>
/// </summary>
public sealed class PodExecSandboxClient : ISandboxExecutor, IRunWorkspaceRegistrar
{
    private readonly string _socketPath;
    private readonly ILogger? _logger;
    private readonly string _relayCommand;
    private readonly string _relayAssembly;

    public PodExecSandboxClient(
        string? socketPath = null,
        ILogger? logger = null,
        string? relayCommand = null,
        string? relayAssembly = null)
    {
        _socketPath = PodExecEndpoint.ResolveSocketPath(socketPath);
        _logger = logger;
        _relayCommand = relayCommand ?? Environment.ProcessPath ?? "dotnet";
        _relayAssembly = relayAssembly ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;
    }

    public bool IsRealIsolation => true;
    public string BackendName => "kata-exec-sidecar";
    public string SelectionReason =>
        "Kata VM plus a dedicated executor sidecar container (own PID/mount namespace) running a fail-closed bubblewrap mount namespace per run.";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    /// <summary>
    /// Verifies that the sidecar is reachable, isolated, and able to build its mount namespace.
    /// Callers treat a failure as fatal — AgentHost refuses to start rather than executing a
    /// model-controlled command outside the boundary.
    /// </summary>
    public async Task<(bool Ok, string Detail)> ProbeAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var lastDetail = "executor sidecar socket was never reachable";

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var frame = await SendAsync(
                        new PodExecRequest
                        {
                            Op = PodExecOps.Probe,
                            CallerPidNamespace = KataBwrapExecutor.TryReadPidNamespace(),
                        },
                        deadline.Token)
                    .ConfigureAwait(false);
                lastDetail = frame.Detail ?? frame.Message ?? "no detail";
                if (frame.Ok)
                    return (true, lastDetail);
                return (false, lastDetail);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastDetail =
                    $"executor sidecar socket '{_socketPath}' is not answering ({ex.Message}); "
                    + "the pod must run the 'agentweaver-exec' container";
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return (false, lastDetail);
    }

    /// <summary>
    /// Asks the sidecar what it can actually do. The contract distinguishes capabilities that work
    /// here from ones that need an external service (image builds) or a different operating system
    /// (winget), so callers can refuse or reroute work instead of failing mid-task.
    /// </summary>
    public async Task<IReadOnlyList<PodExecCapability>> GetCapabilitiesAsync(
        CancellationToken ct = default)
    {
        var frame = await SendAsync(new PodExecRequest { Op = PodExecOps.Capabilities }, ct)
            .ConfigureAwait(false);
        return frame.Capabilities ?? [];
    }

    public void RegisterTrustedWorkspace(string workingDirectory) =>        SendAsync(
                new PodExecRequest
                {
                    Op = PodExecOps.RegisterWorkspace,
                    Workspace = workingDirectory,
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .ThrowIfFailed();

    public void RegisterRuntimeHome(string workingDirectory, string runtimeHome) =>
        SendAsync(
                new PodExecRequest
                {
                    Op = PodExecOps.RegisterHome,
                    Workspace = workingDirectory,
                    Home = runtimeHome,
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .ThrowIfFailed();

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command,
        CancellationToken ct = default)
    {
        try
        {
            var frame = await SendAsync(
                    new PodExecRequest
                    {
                        Op = PodExecOps.Exec,
                        CommandLine = command.CommandLine,
                        WorkingDirectory = command.WorkingDirectory,
                        Environment = command.Environment?.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal),
                        ReadWritePaths = [.. command.FilesystemPolicy.ReadWritePaths],
                        ReadOnlyPaths = [.. command.FilesystemPolicy.ReadOnlyPaths],
                        TimeoutMs = command.TimeoutMs,
                        NetworkEnabled = command.NetworkEnabled,
                    },
                    ct)
                .ConfigureAwait(false);

            if (frame.Type == PodExecFrameTypes.Error)
                return new SandboxExecResult(126, "", $"Command rejected: {frame.Message}", false, false);

            return new SandboxExecResult(
                frame.ExitCode,
                frame.Stdout ?? string.Empty,
                frame.Stderr ?? string.Empty,
                frame.TimedOut,
                frame.Truncated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: an unreachable or failing sidecar denies execution, it never falls back
            // to running the command in the AgentHost container.
            _logger?.LogError(ex, "Executor sidecar is unavailable; command denied.");
            return new SandboxExecResult(
                126,
                "",
                $"Command rejected: executor sidecar isolation unavailable: {ex.Message}",
                false,
                false);
        }
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct).ConfigureAwait(false);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    /// <summary>
    /// Starts a long-lived sandboxed process (browser preview) in the executor sidecar and returns a
    /// local relay process that carries its stdout/stderr and exit code.
    ///
    /// <para>The relay owns the supervising connection: if the relay is killed — or the AgentHost
    /// dies — the sidecar sees the disconnect and terminates the entire sandboxed process group, so
    /// the existing preview lifecycle semantics (die-with-parent, process-group teardown) are
    /// preserved across the container boundary.</para>
    /// </summary>
    public async Task<RemoteSupervisedProcess> StartSupervisedProcessAsync(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool networkEnabled,
        CancellationToken ct = default) =>
        await StartSupervisedProcessAsync(
                commandLine,
                workingDirectory,
                environment,
                networkEnabled,
                HandshakeTimeout,
                ct)
            .ConfigureAwait(false);

    /// <summary>
    /// How long to wait for the sidecar's <c>started</c>/<c>error</c> handshake (relayed by
    /// <see cref="PodExecRelay"/>) before giving up on a spawn. Generous headroom over
    /// <c>KataBwrapExecutor</c>'s ~10s sandbox-child resolution window plus bwrap/user-namespace
    /// setup under load.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Test seam: same as the public overload but with an injectable handshake timeout.
    /// </summary>
    internal async Task<RemoteSupervisedProcess> StartSupervisedProcessAsync(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool networkEnabled,
        TimeSpan handshakeTimeout,
        CancellationToken ct = default)
    {
        var handle = Guid.NewGuid().ToString("n");
        var request = new PodExecRequest
        {
            Op = PodExecOps.Spawn,
            Handle = handle,
            CommandLine = commandLine,
            WorkingDirectory = workingDirectory,
            Environment = environment?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            NetworkEnabled = networkEnabled,
        };

        var psi = new ProcessStartInfo
        {
            FileName = _relayCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(_relayAssembly)
            && !string.Equals(
                Path.GetFileNameWithoutExtension(_relayCommand),
                Path.GetFileNameWithoutExtension(_relayAssembly),
                StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(_relayAssembly);
        }
        psi.ArgumentList.Add(PodExecRelay.RelayArgument);
        psi.ArgumentList.Add(_socketPath);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the executor relay process.");

        try
        {
            // The spawn request travels over the relay's stdin so no command environment ever
            // appears in a process command line.
            await process.StandardInput.WriteLineAsync(PodExecJson.Serialize(request).AsMemory(), ct)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();

            // #849: block until the sidecar itself confirms the sandboxed process actually started
            // (bwrap launched, its sandbox child resolved) instead of reporting success the instant
            // the LOCAL relay process is running. Without this, a spawn that failed or was still
            // resolving on the sidecar side returned a "successful" handle whose PID belonged only to
            // the relay — observe_bound_port and health_check then had nothing real to find and
            // failed with no actionable evidence.
            await AwaitStartHandshakeAsync(process, handle, handshakeTimeout, ct).ConfigureAwait(false);
            return new RemoteSupervisedProcess(process, handle, this);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the single handshake line <see cref="PodExecRelay"/> writes to its own stdout once the
    /// sidecar's <c>started</c> or <c>error</c> frame arrives, and turns a timeout, an error marker,
    /// or an early relay exit into a thrown exception. Must run BEFORE the caller starts treating the
    /// relay's stdout as forwarded workload logs (see <see cref="PodExecRelay.RunAsync"/>), since it
    /// consumes exactly the first line of that stream.
    /// </summary>
    private static async Task AwaitStartHandshakeAsync(
        Process process,
        string handle,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(timeout);
        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out after {timeout.TotalSeconds:0}s waiting for the executor sidecar to confirm " +
                $"sandboxed process (handle={handle}) started; the sidecar may still be resolving the " +
                "sandbox child or bwrap may be stuck.");
        }

        if (line is null)
        {
            var exitDetail = process.HasExited
                ? $"exit code {SafeExitCode(process)}"
                : "stream closed";
            throw new InvalidOperationException(
                $"Executor sidecar closed the connection ({exitDetail}) before confirming sandboxed " +
                $"process (handle={handle}) started.");
        }

        if (line.StartsWith(PodExecRelay.HandshakeErrorMarkerPrefix, StringComparison.Ordinal))
        {
            var detail = line[PodExecRelay.HandshakeErrorMarkerPrefix.Length..];
            throw new InvalidOperationException(
                $"Executor sidecar rejected sandboxed process (handle={handle}): {detail}");
        }

        if (!string.Equals(line, PodExecRelay.HandshakeReadyMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Executor sidecar sent an unexpected handshake for sandboxed process (handle={handle}): '{line}'.");
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    /// <summary>Listening TCP ports owned by a spawned session's sandboxed process group.</summary>
    public async Task<IReadOnlyList<int>> GetListeningPortsAsync(
        string handle,
        CancellationToken ct = default)
    {
        try
        {
            var frame = await SendAsync(
                    new PodExecRequest { Op = PodExecOps.Ports, Handle = handle },
                    ct)
                .ConfigureAwait(false);

            // #849 review: the sidecar now fails closed (an Error frame) instead of an empty port
            // list when the session cannot be scanned at all (e.g. it was started with
            // networkEnabled=false, so its sockets are not visible in the sidecar's own network
            // namespace). Surfacing that distinctly here — rather than folding it into the same
            // "[]" the caller sees for "not listening yet" — is the whole point of the fix below:
            // the catch clause logs the real reason instead of a generic transport failure.
            if (frame.Type == PodExecFrameTypes.Error)
                throw new InvalidOperationException(frame.Message ?? "Executor sidecar port query failed.");

            return frame.Ports ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning (not Debug, #849): a failed port query silently returned [] here, which
            // observe_bound_port/health_check then reported as "no listening port discovered" with no
            // way to tell a transport failure apart from the app genuinely not listening yet. Logging
            // at Warning makes the real cause (e.g. a stale handle, a sidecar restart) visible without
            // needing debug-level logging enabled in production.
            _logger?.LogWarning(ex, "Executor sidecar port query failed for handle {Handle}.", handle);
            return [];
        }
    }

    /// <summary>Terminates a spawned session's sandboxed process group (SIGTERM, then SIGKILL).</summary>
    public async Task StopAsync(string handle, TimeSpan grace, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(
                    new PodExecRequest
                    {
                        Op = PodExecOps.Stop,
                        Handle = handle,
                        GraceMs = (int)Math.Clamp(grace.TotalMilliseconds, 0, 60_000),
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing the relay connection already terminates the group in the sidecar.
            _logger?.LogDebug(ex, "Executor sidecar stop request failed for handle {Handle}.", handle);
        }
    }

    private async Task<PodExecFrame> SendAsync(PodExecRequest request, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var authenticated = request with { Token = ReadToken() };
        await writer.WriteLineAsync(PodExecJson.Serialize(authenticated).AsMemory(), ct).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Executor sidecar closed the connection without responding.");
        return PodExecJson.Deserialize<PodExecFrame>(line)
            ?? throw new InvalidOperationException("Executor sidecar returned an unreadable frame.");
    }

    private string ReadToken() => PodExecRelay.ReadToken(_socketPath);

    /// <summary>A sandboxed long-lived process supervised through the executor sidecar.</summary>
    public sealed record RemoteSupervisedProcess(
        Process Process,
        string Handle,
        PodExecSandboxClient Client)
    {
        public Task<IReadOnlyList<int>> GetListeningPortsAsync(CancellationToken ct = default) =>
            Client.GetListeningPortsAsync(Handle, ct);

        public Task StopAsync(TimeSpan grace, CancellationToken ct = default) =>
            Client.StopAsync(Handle, grace, ct);
    }
}

internal static class PodExecFrameExtensions
{
    public static void ThrowIfFailed(this PodExecFrame frame)
    {
        if (frame.Type == PodExecFrameTypes.Error)
            throw new InvalidOperationException(frame.Message ?? "Executor sidecar rejected the request.");
    }
}
