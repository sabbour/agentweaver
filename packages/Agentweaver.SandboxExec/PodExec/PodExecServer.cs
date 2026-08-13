using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.SandboxFs;
using Microsoft.Extensions.Logging;

namespace Agentweaver.SandboxExec.PodExec;

/// <summary>
/// The executor sidecar daemon. It runs as a second container in the run's Kata pod — same VM, own
/// PID namespace, own mount namespace, non-root, all capabilities dropped, <c>RuntimeDefault</c>
/// seccomp — and is the only process that ever launches model-controlled commands.
///
/// <para>The AgentHost never executes model-controlled commands in its own container, so a
/// sandboxed process cannot see, signal, or inspect the AgentHost process tree at all; the
/// remaining per-run filesystem scoping is applied by <see cref="KataBwrapExecutor"/> inside this
/// container.</para>
///
/// <para>Reachability: an <c>AF_UNIX</c> socket on a pod-private <c>emptyDir</c> that is mounted in
/// the AgentHost and executor containers only, is never bound into a sandboxed child's mount
/// namespace, and is additionally guarded by a 32-byte token written mode-0600 next to it.</para>
/// </summary>
public sealed class PodExecServer : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly string _tokenPath;
    private readonly ILogger? _logger;
    private readonly KataBwrapExecutor _executor;
    private readonly ConcurrentDictionary<string, SpawnedSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private Socket? _listener;
    private string _token = string.Empty;

    public PodExecServer(string? socketPath = null, ILogger? logger = null)
    {
        _socketPath = PodExecEndpoint.ResolveSocketPath(socketPath);
        _tokenPath = PodExecEndpoint.ResolveTokenPath(_socketPath);
        _logger = logger;
        _executor = new KataBwrapExecutor(logger);
    }

    public string SocketPath => _socketPath;

    /// <summary>
    /// Verifies the sandbox boundary this container is responsible for, publishes the token, and
    /// binds the socket. Any failure is fatal: the sidecar must never come up in a state where it
    /// would execute a command outside a real mount namespace.
    /// </summary>
    public void Start()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out var reason))
            throw new InvalidOperationException($"Executor sidecar isolation is unavailable: {reason}");

        var directory = Path.GetDirectoryName(Path.GetFullPath(_socketPath))
            ?? throw new InvalidOperationException($"Executor socket path '{_socketPath}' has no directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(_tokenPath, _token);
        TrySetOwnerOnlyFileMode(_tokenPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(64);
        TrySetOwnerOnlyFileMode(_socketPath);

        _logger?.LogInformation(
            "Executor sidecar listening on {SocketPath} (backend={Backend}, pidNamespace={PidNamespace}, uid={Uid}).",
            _socketPath,
            _executor.BackendName,
            KataBwrapExecutor.TryReadPidNamespace(),
            OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("UID") ?? "n/a" : "n/a");
    }

    /// <summary>Accepts connections until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var listener = _listener
            ?? throw new InvalidOperationException("Start() must be called before RunAsync().");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        while (!linked.Token.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger?.LogWarning(ex, "Executor sidecar accept failed.");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(connection, linked.Token), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket connection, CancellationToken ct)
    {
        using var stream = new NetworkStream(connection, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        PodExecRequest? request = null;
        try
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return;
            request = PodExecJson.Deserialize<PodExecRequest>(line);
            if (request is null)
            {
                await WriteAsync(writer, Error("Malformed executor request."), ct).ConfigureAwait(false);
                return;
            }
            if (!IsAuthorized(request.Token))
            {
                _logger?.LogWarning("Executor sidecar rejected an unauthenticated request (op={Op}).", request.Op);
                await WriteAsync(writer, Error("Executor request token is invalid."), ct).ConfigureAwait(false);
                return;
            }

            switch (request.Op)
            {
                case PodExecOps.Probe:
                    await HandleProbeAsync(request, writer, ct).ConfigureAwait(false);
                    break;
                case PodExecOps.RegisterWorkspace:
                    _executor.RegisterTrustedWorkspace(RequireValue(request.Workspace, "workspace"));
                    await WriteAsync(writer, new PodExecFrame { Type = PodExecFrameTypes.Ack, Ok = true }, ct)
                        .ConfigureAwait(false);
                    break;
                case PodExecOps.RegisterHome:
                    _executor.RegisterRuntimeHome(
                        RequireValue(request.Workspace, "workspace"),
                        RequireValue(request.Home, "home"));
                    await WriteAsync(writer, new PodExecFrame { Type = PodExecFrameTypes.Ack, Ok = true }, ct)
                        .ConfigureAwait(false);
                    break;
                case PodExecOps.Exec:
                    await HandleExecAsync(request, writer, ct).ConfigureAwait(false);
                    break;
                case PodExecOps.Spawn:
                    await HandleSpawnAsync(request, writer, reader, ct).ConfigureAwait(false);
                    break;
                case PodExecOps.Ports:
                    await HandlePortsAsync(request, writer, ct).ConfigureAwait(false);
                    break;
                case PodExecOps.Stop:
                    await HandleStopAsync(request, writer, ct).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(writer, Error($"Unknown executor op '{request.Op}'."), ct)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (SandboxViolationException ex)
        {
            _logger?.LogWarning(ex, "Executor sidecar denied a request (op={Op}).", request?.Op);
            await TryWriteAsync(writer, Error($"Sandbox policy violation: {ex.Message}")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Sidecar shutdown; the supervisor sees the connection drop and fails closed.
        }
        catch (OperationCanceledException ex)
        {
            // An internal deadline expired (for example sandbox startup). Never close silently:
            // the supervisor must see a reason instead of an unexplained disconnect.
            _logger?.LogError(ex, "Executor sidecar timed out handling a request (op={Op}).", request?.Op);
            await TryWriteAsync(writer, Error("Executor request timed out inside the sandbox executor."))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Executor sidecar failed a request (op={Op}).", request?.Op);
            await TryWriteAsync(writer, Error(ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private async Task HandleProbeAsync(PodExecRequest request, StreamWriter writer, CancellationToken ct)
    {
        var available = KataBwrapExecutor.TryProbeAvailability(out var reason);
        var pidNamespace = KataBwrapExecutor.TryReadPidNamespace();
        var details = new List<string> { reason };
        var ok = available;

        // Fail closed when the caller shares this container's PID namespace: that means the
        // process boundary this design depends on (a dedicated executor container, no
        // shareProcessNamespace) is not actually in place.
        if (pidNamespace is null)
        {
            ok = false;
            details.Add("executor sidecar could not read /proc/self/ns/pid");
        }
        else if (!string.IsNullOrWhiteSpace(request.CallerPidNamespace)
                 && string.Equals(request.CallerPidNamespace, pidNamespace, StringComparison.Ordinal))
        {
            ok = false;
            details.Add(
                $"AgentHost and executor share PID namespace {pidNamespace}; a dedicated executor container is required");
        }
        else
        {
            details.Add($"executor pid namespace {pidNamespace} is separate from the AgentHost's");
        }

        if (OperatingSystem.IsLinux() && GetEffectiveUserId() == 0)
        {
            ok = false;
            details.Add("executor sidecar must not run as root");
        }

        await WriteAsync(
            writer,
            new PodExecFrame
            {
                Type = PodExecFrameTypes.Probe,
                Ok = ok,
                Detail = string.Join("; ", details),
                Message = _executor.BackendName,
            },
            ct).ConfigureAwait(false);
    }

    private async Task HandleExecAsync(PodExecRequest request, StreamWriter writer, CancellationToken ct)
    {
        var command = ToCommand(request);
        var result = await _executor.ExecuteAsync(command, ct).ConfigureAwait(false);
        await WriteAsync(
            writer,
            new PodExecFrame
            {
                Type = PodExecFrameTypes.Result,
                ExitCode = result.ExitCode,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                TimedOut = result.TimedOut,
                Truncated = result.OutputTruncated,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a long-lived sandboxed process (browser preview) and streams its output for as long as
    /// the caller keeps the connection open. Losing the connection terminates the whole sandboxed
    /// process group, so an AgentHost crash can never strand a model-controlled server.
    /// </summary>
    private async Task HandleSpawnAsync(
        PodExecRequest request,
        StreamWriter writer,
        StreamReader reader,
        CancellationToken ct)
    {
        var handle = RequireValue(request.Handle, "handle");
        var supervised = await _executor.StartSupervisedProcessAsync(
                RequireValue(request.CommandLine, "commandLine"),
                RequireValue(request.WorkingDirectory, "workingDirectory"),
                request.Environment,
                request.NetworkEnabled,
                ct)
            .ConfigureAwait(false);

        var session = new SpawnedSession(handle, supervised);
        if (!_sessions.TryAdd(handle, session))
        {
            await TerminateAsync(session, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await WriteAsync(writer, Error($"Duplicate executor handle '{handle}'."), ct).ConfigureAwait(false);
            return;
        }

        await WriteAsync(
            writer,
            new PodExecFrame
            {
                Type = PodExecFrameTypes.Started,
                ProcessGroupId = supervised.WorkloadProcessGroupId,
            },
            ct).ConfigureAwait(false);

        var gate = new SemaphoreSlim(1, 1);
        var stdout = PumpAsync(supervised.Process.StandardOutput, PodExecFrameTypes.Stdout, writer, gate, ct);
        var stderr = PumpAsync(supervised.Process.StandardError, PodExecFrameTypes.Stderr, writer, gate, ct);
        var disconnect = WatchForDisconnectAsync(reader, ct);

        try
        {
            var exited = supervised.Process.WaitForExitAsync(ct);
            var completed = await Task.WhenAny(exited, disconnect).ConfigureAwait(false);
            if (completed == disconnect)
            {
                _logger?.LogInformation(
                    "Executor sidecar lost its supervisor connection for handle {Handle}; terminating the sandboxed process group.",
                    handle);
                await TerminateAsync(session, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await WriteAsync(
                writer,
                new PodExecFrame
                {
                    Type = PodExecFrameTypes.Exit,
                    ExitCode = supervised.Process.ExitCode,
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(handle, out _);
            await TerminateAsync(session, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private async Task HandlePortsAsync(PodExecRequest request, StreamWriter writer, CancellationToken ct)
    {
        var handle = RequireValue(request.Handle, "handle");
        if (!_sessions.TryGetValue(handle, out var session))
        {
            await WriteAsync(
                writer,
                new PodExecFrame { Type = PodExecFrameTypes.Ports, Ports = [] },
                ct).ConfigureAwait(false);
            return;
        }

        var ports = PodExecPortScanner.ListeningPortsForProcessGroup(session.ProcessGroupId);
        await WriteAsync(
            writer,
            new PodExecFrame { Type = PodExecFrameTypes.Ports, Ports = [.. ports] },
            ct).ConfigureAwait(false);
    }

    private async Task HandleStopAsync(PodExecRequest request, StreamWriter writer, CancellationToken ct)
    {
        var handle = RequireValue(request.Handle, "handle");
        if (_sessions.TryGetValue(handle, out var session))
        {
            await TerminateAsync(
                    session,
                    TimeSpan.FromMilliseconds(Math.Clamp(request.GraceMs, 0, 60_000)))
                .ConfigureAwait(false);
        }

        await WriteAsync(writer, new PodExecFrame { Type = PodExecFrameTypes.Ack, Ok = true }, ct)
            .ConfigureAwait(false);
    }

    private async Task TerminateAsync(SpawnedSession session, TimeSpan grace)
    {
        try
        {
            if (session.Supervised.Process.HasExited)
                return;

            PodExecSignals.SendProcessGroupSignal(session.ProcessGroupId, "TERM");
            using var graceCts = new CancellationTokenSource(grace);
            try
            {
                await session.Supervised.Process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PodExecSignals.SendProcessGroupSignal(session.ProcessGroupId, "KILL");
            }

            if (!session.Supervised.Process.HasExited)
                session.Supervised.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Executor sidecar could not terminate handle {Handle}.", session.Handle);
        }
    }

    private static async Task PumpAsync(
        StreamReader source,
        string frameType,
        StreamWriter writer,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        try
        {
            while (await source.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync(
                            PodExecJson.Serialize(
                                new PodExecFrame
                                {
                                    Type = frameType,
                                    Data = SandboxOutputRedactor.Default.Redact(line),
                                }))
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch
        {
            // Teardown closes the redirected stream or the supervisor connection.
        }
    }

    /// <summary>Completes when the supervising AgentHost connection is closed.</summary>
    private static async Task WatchForDisconnectAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is not null)
            {
                // The supervisor sends nothing after the spawn request; drain defensively.
            }
        }
        catch
        {
            // Any read failure is treated as a disconnect.
        }
    }

    private SandboxCommand ToCommand(PodExecRequest request) =>
        new(
            RequireValue(request.CommandLine, "commandLine"),
            RequireValue(request.WorkingDirectory, "workingDirectory"),
            request.Environment,
            new SandboxFsPolicy(
                request.ReadWritePaths ?? [],
                request.ReadOnlyPaths ?? [],
                []),
            request.TimeoutMs,
            request.NetworkEnabled);

    private bool IsAuthorized(string? token) =>
        !string.IsNullOrEmpty(_token)
        && !string.IsNullOrEmpty(token)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(_token),
            Encoding.UTF8.GetBytes(token));

    private static string RequireValue(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Executor request is missing '{name}'.")
            : value;

    private static PodExecFrame Error(string message) =>
        new() { Type = PodExecFrameTypes.Error, Message = message, Ok = false };

    private static async Task WriteAsync(StreamWriter writer, PodExecFrame frame, CancellationToken ct) =>
        await writer.WriteLineAsync(PodExecJson.Serialize(frame).AsMemory(), ct).ConfigureAwait(false);

    private static async Task TryWriteAsync(StreamWriter writer, PodExecFrame frame)
    {
        try
        {
            await writer.WriteLineAsync(PodExecJson.Serialize(frame)).ConfigureAwait(false);
        }
        catch
        {
            // The supervisor is gone; nothing to report to.
        }
    }

    private static void TrySetOwnerOnlyFileMode(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort: the emptyDir is already pod-private and unreachable from sandboxed children.
        }
    }

    private static int GetEffectiveUserId()
    {
        try
        {
            // /proc/self/status exposes "Uid: real effective saved fs".
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                    continue;
                var fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1 && int.TryParse(fields[1], out var euid))
                    return euid;
            }
        }
        catch
        {
            // Fall through: treat as non-root rather than blocking startup on an unreadable procfs.
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Values)
            await TerminateAsync(session, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _sessions.Clear();
        _listener?.Dispose();
        _shutdown.Dispose();
        try
        {
            if (File.Exists(_socketPath))
                File.Delete(_socketPath);
        }
        catch
        {
            // Pod teardown removes the emptyDir anyway.
        }
    }

    private sealed record SpawnedSession(string Handle, KataBwrapExecutor.SupervisedProcess Supervised)
    {
        public int ProcessGroupId => Supervised.WorkloadProcessGroupId;
    }
}

/// <summary>Process-group signalling used for fail-closed sandbox teardown.</summary>
internal static class PodExecSignals
{
    public static void SendProcessGroupSignal(int processGroupId, string signal)
    {
        if (processGroupId <= 0 || OperatingSystem.IsWindows())
            return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/kill",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-" + signal);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(
                "-" + processGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var kill = Process.Start(psi);
            kill?.WaitForExit(5000);
        }
        catch
        {
            // The caller escalates to Process.Kill(entireProcessTree).
        }
    }
}
