extern alias agenthost;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PreviewRunner = agenthost::Agentweaver.AgentHost.PreviewRunner;
using PreviewRunnerOptions = agenthost::Agentweaver.AgentHost.PreviewRunnerOptions;

namespace Agentweaver.Tests.Preview;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "real /proc process-tree coverage requires Linux";
    }
}

/// <summary>
/// AgentHost preview port-discovery + legible-failure coverage (4th preview blocker, run 4d74955a):
/// <list type="bullet">
///   <item>PART A — <c>ParseListeningPortsFromProcNet</c> discovers LISTEN ports from the kernel
///   <c>/proc/net/tcp</c> and <c>/proc/net/tcp6</c> tables (dependency-free; no <c>ss</c> binary), so
///   the app's bound port is found even when node buffers stdout and even when it binds IPv6-any
///   (<c>::</c>, which surfaces only in tcp6).</item>
///   <item>PART B — <c>ObserveBoundPortAsync</c> returns a clean unhealthy observation carrying a
///   PRECISE reason (<c>no_listening_port_discovered</c> / <c>process_exited:*</c>) instead of throwing,
///   so PreviewStep emits a legible reason rather than an opaque HTTP 500.</item>
/// </list>
/// </summary>
public sealed class PreviewRunnerObserveTests
{
    private static PreviewRunner NewRunner() => new(
        Options.Create(new PreviewRunnerOptions { ObserveTimeoutSeconds = 1 }),
        NullLogger<PreviewRunner>.Instance);

    // ── PART A: /proc/net/tcp(6) parser ──────────────────────────────────────────

    [Fact]
    public void ParseProcNet_Tcp4_ExtractsListenPort_IgnoresNonListen()
    {
        // st 0A = LISTEN on port 0BB8 (=3000); st 01 = ESTABLISHED must be ignored.
        const string tcp4 =
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
            "   0: 0100007F:0BB8 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 12345 1 0000 100 0 0 10 0\n" +
            "   1: 0100007F:1F90 0100007F:C350 01 00000000:00000000 00:00000000 00000000  1000        0 12346 1 0000 100 0 0 10 0\n";

        var ports = PreviewRunner.ParseListeningPortsFromProcNet(tcp4);

        ports.Should().Contain(3000);
        ports.Should().NotContain(0x1F90, "0x1F90 (8080) is an ESTABLISHED socket, not LISTEN");
        ports.Should().HaveCount(1);
    }

    [Fact]
    public void ParseProcNet_Tcp6_ExtractsListenPort_FromIPv6AnyAddress()
    {
        // node's server.listen(port) binds :: (IPv6-any) on dual-stack Linux -> appears ONLY in tcp6.
        // 1388 hex = 5000, LISTEN (0A).
        const string tcp6 =
            "  sl  local_address                         remote_address                        st ...\n" +
            "   0: 00000000000000000000000000000000:1388 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000 0 22222 1 0000 100 0 0 10 0\n";

        var ports = PreviewRunner.ParseListeningPortsFromProcNet(tcp6);

        ports.Should().ContainSingle().Which.Should().Be(5000);
    }

    [Fact]
    public void ParseProcNet_HandlesEmptyOrHeaderOnly()
    {
        PreviewRunner.ParseListeningPortsFromProcNet(string.Empty).Should().BeEmpty();
        PreviewRunner.ParseListeningPortsFromProcNet(
            "  sl  local_address rem_address   st ...\n").Should().BeEmpty();
    }

    [Fact]
    public void ParseProcStat_ExtractsStartTime_WhenCommandContainsSpacesAndParentheses()
    {
        var stat = "123 (preview worker (child)) S "
            + string.Join(' ', Enumerable.Repeat("0", 18))
            + " 987654 0 0\n";

        PreviewRunner.TryParseProcessStartTime(stat, out var startTime).Should().BeTrue();
        startTime.Should().Be(987654);
    }

    // ── PART B: legible unhealthy observation (never throw / opaque 500) ──────────

    [Fact]
    public async Task ProbeHealth_SendsLocalhostHostHeader_MatchingGatewayRewrite()
    {
        // The preview gateway rewrites external traffic's Host to "localhost" (#312). The pod-local
        // readiness probe must send the SAME Host so readiness reflects real browser reachability
        // (a dev-server host allowlist block is caught here rather than only in the user's browser).
        var runner = NewRunner();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string? capturedHost = null;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            // Read until the end of the HTTP request headers (loopback GET arrives quickly).
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            foreach (var line in sb.ToString().Split("\r\n"))
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    capturedHost = line["Host:".Length..].Trim();

            var responseBytes = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(responseBytes);
            await stream.FlushAsync();
        });

        var result = await runner.ProbeHealthForTestAsync(port, "/", CancellationToken.None);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));

        result.Healthy.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        capturedHost.Should().Be("localhost",
            "the probe must send the same Host the preview gateway rewrites external traffic to");
    }

    [Fact]
    public async Task ObserveBoundPort_NoListeningPort_ReturnsUnhealthyWithPreciseReason()
    {
        var runner = NewRunner();
        // A process that stays alive but never listens on a port -> discovery finds nothing.
        var idleCommand = OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1 > nul" : "sleep 5";

        var started = await runner.StartPreviewProcessAsync(
            idleCommand, AppContext.BaseDirectory, "run-observe-noport", null, null, CancellationToken.None);

        var observation = await runner.ObserveBoundPortAsync(
            started.SessionId, TimeSpan.FromSeconds(1), "/", CancellationToken.None);

        observation.Healthy.Should().BeFalse();
        observation.Port.Should().Be(0);
        observation.Reason.Should().Be("no_listening_port_discovered");

        await runner.StopPreviewProcessAsync(started.SessionId, "test_cleanup", CancellationToken.None);
    }

    [Fact]
    public async Task ObserveBoundPort_ProcessExitedEarly_ReturnsUnhealthyWithExitReason()
    {
        var runner = NewRunner();
        // A process that exits immediately (before any port is observed).
        var started = await runner.StartPreviewProcessAsync(
            "exit 7", AppContext.BaseDirectory, "run-observe-exit", null, null, CancellationToken.None);

        // Give the Exited event a beat to fire so HasExited is observed.
        await Task.Delay(500);

        var observation = await runner.ObserveBoundPortAsync(
            started.SessionId, TimeSpan.FromSeconds(2), "/", CancellationToken.None);

        observation.Healthy.Should().BeFalse();
        observation.Port.Should().Be(0);
        observation.Reason.Should().StartWith("process_exited:");
    }

    [LinuxFact]
    public async Task ObserveBoundPort_LinuxProcessTree_FindsDescendantAndRejectsUnrelatedAndExitedPid()
    {
        await RequireNodeAsync();

        var fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"preview-proc-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var serverScript = Path.Combine(fixtureDirectory, "server.js");
        var parentScript = Path.Combine(fixtureDirectory, "parent.js");
        await File.WriteAllTextAsync(serverScript, """
            const http = require('http');
            const server = http.createServer((request, response) => {
              response.writeHead(200, { 'Content-Type': 'text/plain' });
              response.end('ok');
            });
            server.listen(0, '127.0.0.1', () => {
              console.log(`PORT=${server.address().port}`);
            });
            const shutdown = () => server.close(() => process.exit(0));
            process.on('SIGTERM', shutdown);
            process.on('SIGINT', shutdown);
            """);
        await File.WriteAllTextAsync(parentScript, """
            const { spawn } = require('child_process');
            const child = spawn(process.execPath, [process.argv[2]], { stdio: 'inherit' });
            const shutdown = () => {
              child.kill('SIGTERM');
              process.exit(0);
            };
            process.on('SIGTERM', shutdown);
            process.on('SIGINT', shutdown);
            setInterval(() => {}, 1000);
            """);

        using var unrelated = StartNodeProcess(serverScript, fixtureDirectory);
        var unrelatedOutput = await unrelated.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        unrelatedOutput.Should().StartWith("PORT=");
        var unrelatedPort = int.Parse(unrelatedOutput!["PORT=".Length..]);

        var runner = NewRunner();
        var command = $"node {QuoteForPosixShell(parentScript)} {QuoteForPosixShell(serverScript)}";
        var started = await runner.StartPreviewProcessAsync(
            command, fixtureDirectory, "run-linux-proc-tree", null, null, CancellationToken.None);
        var rootIdentity = PreviewRunner.CaptureProcessIdentityForTest(started.Pid);
        rootIdentity.StartTime.Should().NotBeNull();

        try
        {
            var portsForReusedIdentity =
                await PreviewRunner.SnapshotProcessTreeListeningPortsForTestAsync(
                    rootIdentity.Pid, rootIdentity.StartTime!.Value + 1, CancellationToken.None);
            portsForReusedIdentity.Should().BeEmpty(
                "the same PID with a different start time represents a different process");

            var observation = await runner.ObserveBoundPortAsync(
                started.SessionId, TimeSpan.FromSeconds(10), "/", CancellationToken.None);

            observation.Healthy.Should().BeTrue();
            observation.AppPort.Should().BePositive();
            observation.AppPort.Should().NotBe(unrelatedPort,
                "a socket owned by a process outside the preview process tree must not be attributed");
        }
        finally
        {
            await runner.StopPreviewProcessAsync(
                started.SessionId, "test_cleanup", CancellationToken.None);
            await StopProcessAsync(unrelated);
            Directory.Delete(fixtureDirectory, recursive: true);
        }

        var portsAfterExit = await PreviewRunner.SnapshotProcessTreeListeningPortsForTestAsync(
            rootIdentity.Pid, rootIdentity.StartTime, CancellationToken.None);
        portsAfterExit.Should().BeEmpty(
            "fds must not be trusted after the captured process identity exits or its PID is reused");
    }

    [LinuxFact]
    public async Task ObserveBoundPort_LinuxPrivateSession_FindsReparentedServer()
    {
        await RequireNodeAsync();

        var fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"preview-proc-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var serverScript = Path.Combine(fixtureDirectory, "server.js");
        var bootstrapScript = Path.Combine(fixtureDirectory, "bootstrap.js");
        await File.WriteAllTextAsync(serverScript, """
            const http = require('http');
            const server = http.createServer((request, response) => response.end('ok'));
            server.listen(0, '127.0.0.1');
            """);
        await File.WriteAllTextAsync(bootstrapScript, """
            const { spawn } = require('child_process');
            const child = spawn(process.execPath, [process.argv[2]], {
              detached: false,
              stdio: 'ignore',
            });
            child.unref();
            setTimeout(() => process.exit(0), 100);
            """);

        var runner = NewRunner();
        // The bootstrap exits after launching the server. The server is reparented out of the
        // shell's PPID tree but remains in the private setsid session established by PreviewRunner.
        var command = $"node {QuoteForPosixShell(bootstrapScript)} {QuoteForPosixShell(serverScript)} & tail -f /dev/null";
        var started = await runner.StartPreviewProcessAsync(
            command, fixtureDirectory, "run-linux-private-session", null, null, CancellationToken.None);

        try
        {
            var observation = await runner.ObserveBoundPortAsync(
                started.SessionId, TimeSpan.FromSeconds(10), "/", CancellationToken.None);

            observation.Healthy.Should().BeTrue();
            observation.AppPort.Should().BePositive();
        }
        finally
        {
            await runner.StopPreviewProcessAsync(
                started.SessionId, "test_cleanup", CancellationToken.None);
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    private static async Task RequireNodeAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        try
        {
            process.Start();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            throw new InvalidOperationException(
                "node is required for the Linux /proc process-tree E2E test");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "node is required for the Linux /proc process-tree E2E test");
    }

    private static Process StartNodeProcess(string script, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        return process;
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
            return;

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static string QuoteForPosixShell(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
