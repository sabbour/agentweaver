using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Raised when the forwarder cannot bind any free port within the allowed public-port range
/// (spec-006 preview-forwarder). Surfaced as the distinct <c>no_public_port_available</c> outcome.
/// </summary>
internal sealed class NoPublicPortAvailableException(int min, int max)
    : Exception($"No free public port available in the allowed range [{min},{max}].");

/// <summary>
/// Pod-local TCP forwarder that GUARANTEES pod-IP reachability for a preview app regardless of how
/// the app binds (loopback-only OR all-interfaces) or which port it chose (spec-006 preview-forwarder).
///
/// <para>
/// Root cause it closes: the app is health-checked on <c>127.0.0.1:appPort</c> (loopback) but the
/// Gateway registers/probes <c>podIP:port</c> (routable). A loopback-only app therefore passed
/// observe yet failed registration with "nothing is listening on pod IP". The forwarder listens on
/// <c>0.0.0.0:PublicPort</c> — a platform-chosen FREE port scanned from the allowed range
/// <c>[PublicPortRangeMin, PublicPortRangeMax]</c>, always distinct from the app port — and
/// bidirectionally pumps every accepted connection to <c>127.0.0.1:appPort</c>. The public port is
/// what the platform registers with the Gateway; it is reachable on the pod IP no matter how the app
/// bound.
/// </para>
///
/// <para>
/// BLOCKER: the public port MUST fall inside the Gateway-admitted range. The Gateway rejects
/// out-of-range ports (<c>SandboxPreviewOptions.AllowedPortMin/Max</c>) and the sandbox NetworkPolicy
/// only admits ingress to <c>port 3000 endPort 9000</c> (<c>k8s/networkpolicy-sandbox.yaml</c>). An
/// OS-ephemeral port (~32768+) would be black-holed. The scan keeps the public port in-range.
/// </para>
/// </summary>
internal sealed class TcpPortForwarder : IAsyncDisposable
{
    private const int DefaultMaxConnections = 256;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly int _appPort;
    private readonly int _rangeMin;
    private readonly int _rangeMax;
    private readonly int _maxConnections;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connLimit;
    private readonly ConcurrentDictionary<Task, byte> _pumps = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _disposed;

    public TcpPortForwarder(
        int appPort, int rangeMin, int rangeMax, ILogger logger, int maxConnections = DefaultMaxConnections)
    {
        if (appPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(appPort), "App port must be between 1 and 65535.");
        if (rangeMin is <= 0 or > 65535 || rangeMax is <= 0 or > 65535 || rangeMin > rangeMax)
            throw new ArgumentOutOfRangeException(nameof(rangeMin), "Invalid public-port range.");
        _appPort = appPort;
        _rangeMin = rangeMin;
        _rangeMax = rangeMax;
        _maxConnections = Math.Max(1, maxConnections);
        _logger = logger;
        _connLimit = new SemaphoreSlim(_maxConnections, _maxConnections);
    }

    /// <summary>The platform-chosen, pod-IP-reachable, in-range port that fronts the app. Valid after <see cref="Start"/>.</summary>
    public int PublicPort { get; private set; }

    public int AppPort => _appPort;

    /// <summary>
    /// Binds a FREE port within <c>[rangeMin, rangeMax]</c> (skipping the app port) and launches the
    /// accept loop. Throws <see cref="NoPublicPortAvailableException"/> if the whole range is taken.
    /// </summary>
    public void Start()
    {
        _listener = BindInRange();
        PublicPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.LogInformation(
            "TcpPortForwarder: forwarding pod 0.0.0.0:{PublicPort} -> 127.0.0.1:{AppPort}", PublicPort, _appPort);
    }

    private TcpListener BindInRange()
    {
        var count = _rangeMax - _rangeMin + 1;
        // Random start offset spreads concurrent previews across the range to minimize collisions.
        var start = Random.Shared.Next(count);
        for (var i = 0; i < count; i++)
        {
            var port = _rangeMin + ((start + i) % count);
            if (port == _appPort)
                continue;

            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                try { listener.Stop(); } catch { /* ignore */ }
            }
        }

        throw new NoPublicPortAvailableException(_rangeMin, _rangeMax);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // listener stopped during disposal
            }
            catch (SocketException ex)
            {
                // A transient accept error (e.g. ECONNABORTED when a client RSTs between SYN and
                // accept) must NOT kill the listener for the whole session — only a real shutdown does.
                if (ct.IsCancellationRequested)
                    break;
                _logger.LogDebug(ex, "TcpPortForwarder: transient accept error on 0.0.0.0:{PublicPort}; continuing.", PublicPort);
                continue;
            }

            // Defensive concurrency cap: shed load rather than exhaust sockets/threads.
            if (!await _connLimit.WaitAsync(0, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("TcpPortForwarder: connection cap {Cap} reached; dropping inbound connection.", _maxConnections);
                inbound.Dispose();
                continue;
            }

            var pump = Task.Run(() => PumpConnectionAsync(inbound, ct));
            _pumps[pump] = 0;
            _ = pump.ContinueWith(t => _pumps.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task PumpConnectionAsync(TcpClient inbound, CancellationToken ct)
    {
        try
        {
            using (inbound)
            using (var outbound = new TcpClient())
            {
                await outbound.ConnectAsync(IPAddress.Loopback, _appPort, ct).ConfigureAwait(false);
                inbound.NoDelay = true;
                outbound.NoDelay = true;

                var clientSock = inbound.Client;
                var appSock = outbound.Client;

                // Half-close-aware bidirectional pump: when one direction completes, we Shutdown(Send)
                // on the destination so the peer sees a clean EOF and can flush its trailing bytes
                // (e.g. the rest of an HTML body) before the connection is torn down.
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var clientToApp = HalfDuplexCopyAsync(clientSock, appSock, connCts.Token);
                var appToClient = HalfDuplexCopyAsync(appSock, clientSock, connCts.Token);

                await Task.WhenAny(clientToApp, appToClient).ConfigureAwait(false);

                // Give the opposite direction a bounded grace to drain, then force-stop the laggard.
                connCts.CancelAfter(DrainTimeout);
                try { await Task.WhenAll(clientToApp, appToClient).ConfigureAwait(false); }
                catch { /* drain best-effort; sockets disposed below */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TcpPortForwarder: connection pump ended with error.");
        }
        finally
        {
            // Guard: DisposeAsync may have disposed the semaphore if a pump outlived the drain window.
            try { _connLimit.Release(); }
            catch (ObjectDisposedException) { /* forwarder torn down */ }
        }
    }

    /// <summary>Copies <paramref name="src"/> → <paramref name="dst"/> then half-closes the destination's send side.</summary>
    private static async Task HalfDuplexCopyAsync(Socket src, Socket dst, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int read;
                try { read = await src.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }

                if (read <= 0)
                    break;

                var offset = 0;
                while (offset < read)
                {
                    int sent;
                    try { sent = await dst.SendAsync(buffer.AsMemory(offset, read - offset), SocketFlags.None, ct).ConfigureAwait(false); }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }
                    if (sent <= 0)
                        return;
                    offset += sent;
                }
            }

            // Signal EOF to the peer so it can complete its response half.
            try { dst.Shutdown(SocketShutdown.Send); }
            catch { /* peer already gone */ }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _cts.Cancel(); } catch { /* best effort */ }
        try { _listener?.Stop(); } catch { /* best effort */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* best effort during shutdown */ }
        }

        // Drain in-flight pump tasks with a bounded timeout BEFORE disposing the semaphore/cts they
        // touch, so a still-unwinding pump never hits a disposed SemaphoreSlim.
        var outstanding = _pumps.Keys.ToArray();
        if (outstanding.Length > 0)
        {
            try { await Task.WhenAll(outstanding).WaitAsync(DrainTimeout).ConfigureAwait(false); }
            catch { /* best effort; the release guard covers any straggler */ }
        }

        _cts.Dispose();
        _connLimit.Dispose();
    }
}
