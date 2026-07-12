extern alias agenthost;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PreviewRunner = agenthost::Agentweaver.AgentHost.PreviewRunner;
using PreviewRunnerOptions = agenthost::Agentweaver.AgentHost.PreviewRunnerOptions;

namespace Agentweaver.Tests.Preview;

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

    // ── PART B: legible unhealthy observation (never throw / opaque 500) ──────────

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
}
