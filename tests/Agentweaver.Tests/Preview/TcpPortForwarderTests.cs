extern alias agenthost;

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TcpPortForwarder = agenthost::Agentweaver.AgentHost.TcpPortForwarder;
using NoPublicPortAvailableException = agenthost::Agentweaver.AgentHost.NoPublicPortAvailableException;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Coverage for the pod-local <see cref="TcpPortForwarder"/> (spec-006 preview-forwarder). The live
/// bug (run d6f9b040): a loopback-only app passed the pod's 127.0.0.1 health probe but failed Gateway
/// pod-IP reachability, yielding <c>registration_failed</c>. The forwarder must make a loopback-only
/// app reachable via a distinct public port WITHIN the Gateway-admitted range [3000,9000], and must
/// NOT fake reachability for a dead app.
/// </summary>
public sealed class TcpPortForwarderTests
{
    private const int RangeMin = 3000;
    private const int RangeMax = 9000;

    [Fact]
    public async Task LoopbackOnlyApp_IsReachableThroughPublicPort_InRange_OnDistinctPort()
    {
        // Arrange: a fake "app" that binds ONLY 127.0.0.1 (loopback) — exactly the failing case.
        using var app = new LoopbackEchoServer();
        app.Start();

        await using var forwarder = new TcpPortForwarder(app.Port, RangeMin, RangeMax, NullLogger.Instance);
        forwarder.Start();

        // BLOCKER #1: the public port MUST be inside the Gateway-admitted range and ≠ the app port.
        forwarder.PublicPort.Should().BeInRange(RangeMin, RangeMax);
        forwarder.PublicPort.Should().NotBe(app.Port);

        // Act: connect THROUGH the forwarder's public port and round-trip bytes.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.PublicPort);
        var stream = client.GetStream();
        var payload = Encoding.ASCII.GetBytes("ping");
        await stream.WriteAsync(payload);

        var buffer = new byte[4];
        var read = await ReadExactAsync(stream, buffer, TimeSpan.FromSeconds(5));

        // Assert: the forwarder bidirectionally pumped to the loopback-only app and back.
        read.Should().Be(4);
        Encoding.ASCII.GetString(buffer).Should().Be("PING");
    }

    [Fact]
    public async Task UnreachableApp_DoesNotFakeSuccess_ConnectionIsClosed()
    {
        // Arrange: forwarder pointed at a port with nothing listening (truly-unreachable app).
        var deadPort = GetFreePort();
        await using var forwarder = new TcpPortForwarder(deadPort, RangeMin, RangeMax, NullLogger.Instance);
        forwarder.Start();

        forwarder.PublicPort.Should().BeInRange(RangeMin, RangeMax);

        // Act: a client can connect to the public port, but the forwarder cannot reach the app,
        // so it closes the connection — the read returns 0 (never hangs, never fakes success).
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.PublicPort);
        var stream = client.GetStream();

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, cts.Token);

        read.Should().Be(0, "the forwarder must not fake reachability for a dead app");
    }

    [Fact]
    public async Task RangeExhausted_ThrowsDistinctNoPublicPortAvailable()
    {
        // Arrange: occupy the ONLY port in a single-port range so the scan finds nothing free.
        var busyPort = GetFreePort();
        using var occupier = new TcpListener(IPAddress.Any, busyPort);
        occupier.Start();

        // Act + Assert: the forwarder cannot bind any public port → distinct actionable failure.
        await using var forwarder = new TcpPortForwarder(1234, busyPort, busyPort, NullLogger.Instance);
        var act = forwarder.Start;

        act.Should().Throw<NoPublicPortAvailableException>();
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Minimal TCP server that binds ONLY 127.0.0.1 and echoes uppercased bytes.</summary>
    private sealed class LoopbackEchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => EchoAsync(client));
                }
            }
            catch { /* listener stopped */ }
        }

        private async Task EchoAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[256];
                    int n;
                    while ((n = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                    {
                        var upper = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(buffer, 0, n).ToUpperInvariant());
                        await stream.WriteAsync(upper.AsMemory(0, upper.Length), _cts.Token);
                    }
                }
            }
            catch { /* client closed */ }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}

