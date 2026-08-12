extern alias agenthost;

using System.Net.Sockets;
using System.Text;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxExec.PodExec;
using FluentAssertions;
using AgentHostExecutorEntrypoints = agenthost::Agentweaver.AgentHost.AgentHostExecutorEntrypoints;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Covers the executor-sidecar boundary introduced to replace the impossible nested-procfs
/// bubblewrap design (see <c>docs/deep-dive/sandbox-pod-execution.md</c>). The protocol tests run
/// everywhere; the ones that actually execute sandboxed commands are Linux + bubblewrap gated.
/// </summary>
public sealed class PodExecSidecarTests
{
    [Fact]
    public void Endpoint_ResolvesConfiguredPathThenEnvironmentThenPodDefault()
    {
        PodExecEndpoint.ResolveSocketPath("/custom/exec.sock").Should().Be("/custom/exec.sock");
        PodExecEndpoint.ResolveSocketPath(null).Should().NotBeNullOrWhiteSpace();
        PodExecEndpoint.ResolveTokenPath("/var/run/agentweaver-exec/exec.sock")
            .Should().Be(Path.Combine(Path.GetFullPath("/var/run/agentweaver-exec"), "exec.token"));
    }

    [Fact]
    public void EntrypointArguments_AreOnlyHonouredWhenAPathFollowsTheMode()
    {
        AgentHostExecutorEntrypoints
            .ResolveSocketArgument(["--exec-agent", "/run/exec.sock"], "--exec-agent")
            .Should().Be("/run/exec.sock");
        AgentHostExecutorEntrypoints
            .ResolveSocketArgument(["--exec-agent", "--verbose"], "--exec-agent")
            .Should().BeNull();
        AgentHostExecutorEntrypoints
            .ResolveSocketArgument(["--exec-agent"], "--exec-agent")
            .Should().BeNull();
    }

    [SidecarLinuxFact]
    public async Task Server_RejectsRequestsWithoutThePodPrivateToken()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        await using var harness = PodExecTestHarness.StartServer(NewRoot());

        var frame = await SendRawAsync(
            harness.SocketPath,
            new PodExecRequest { Op = PodExecOps.Probe, Token = "not-the-token" });

        frame.Type.Should().Be(PodExecFrameTypes.Error);
        frame.Ok.Should().BeFalse();
        frame.Message.Should().Contain("token");
    }

    /// <summary>
    /// The probe is the deployment's fail-closed gate. In-process the caller shares the server's PID
    /// namespace, which is precisely the misconfiguration (single container, or
    /// <c>shareProcessNamespace: true</c>) the probe must refuse.
    /// </summary>
    [SidecarLinuxFact]
    public async Task Probe_FailsClosedWhenCallerSharesTheExecutorPidNamespace()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        await using var harness = PodExecTestHarness.StartServer(NewRoot());
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);

        var (ok, detail) = await client.ProbeAsync(TimeSpan.FromSeconds(10));

        ok.Should().BeFalse();
        detail.Should().Contain("share PID namespace");
    }

    [SidecarLinuxFact]
    public async Task Probe_FailsClosedWhenNoSidecarIsListening()
    {
        var client = new PodExecSandboxClient(
            Path.Combine(Path.GetTempPath(), $"awx-absent-{Guid.NewGuid():N}", "exec.sock"));

        var (ok, detail) = await client.ProbeAsync(TimeSpan.FromSeconds(2));

        ok.Should().BeFalse();
        detail.Should().NotBeNullOrWhiteSpace();
    }

    [SidecarLinuxFact]
    public async Task Execute_RunsInsideTheSidecarMountNamespaceAndCannotSeeSiblingRuns()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        var root = NewRoot();
        var (workspace, sibling) = CreateTwoRuns(root);
        await using var harness = PodExecTestHarness.StartServer(root);
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);
        client.RegisterTrustedWorkspace(workspace);
        client.RegisterRuntimeHome(workspace, CreateRuntimeHome(root));

        var result = await client.ExecuteAsync(new SandboxCommand(
            $"cat {Quote(Path.Combine(sibling, "secret.txt"))} || echo sibling-unreachable",
            workspace,
            null,
            new SandboxFsPolicy([workspace], [], []),
            30_000,
            NetworkEnabled: false));

        result.Stdout.Should().Contain("sibling-unreachable");
        result.Stdout.Should().NotContain("sibling-secret");
    }

    /// <summary>
    /// A sidecar that cannot serve a command must never fall back to running it in the caller's
    /// container. Exit code 126 ("command cannot execute") is the documented fail-closed signal.
    /// </summary>
    [Fact]
    public async Task Execute_FailsClosedWithExitCode126WhenTheSidecarIsUnreachable()
    {
        var client = new PodExecSandboxClient(
            Path.Combine(Path.GetTempPath(), $"awx-absent-{Guid.NewGuid():N}", "exec.sock"));

        var result = await client.ExecuteAsync(new SandboxCommand(
            "echo should-never-run",
            Path.GetTempPath(),
            null,
            new SandboxFsPolicy([Path.GetTempPath()], [], []),
            5_000,
            NetworkEnabled: false));

        result.ExitCode.Should().Be(126);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("executor sidecar isolation unavailable");
    }

    /// <summary>
    /// Preview output is the product surface for a spawned process: the workload must own a real
    /// stdout, and the sidecar must stream it to the supervisor line by line.
    /// </summary>
    [SidecarLinuxFact]
    public async Task SpawnedProcess_StreamsItsOwnStdoutToTheSupervisor()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        var root = NewRoot();
        var (workspace, _) = CreateTwoRuns(root);
        await using var harness = PodExecTestHarness.StartServer(root);
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);
        client.RegisterTrustedWorkspace(workspace);
        client.RegisterRuntimeHome(workspace, CreateRuntimeHome(root));

        var supervised = await client.StartSupervisedProcessAsync(
            "echo preview-listening; while :; do sleep 1; done",
            workspace,
            null,
            networkEnabled: false);

        try
        {
            var line = await supervised.Process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            line.Should().Be(
                "preview-listening",
                "the sandboxed process must inherit a working stdout, not one bubblewrap closed");
        }
        finally
        {
            await supervised.StopAsync(TimeSpan.FromSeconds(2));
            supervised.Process.Dispose();
        }
    }

    [SidecarLinuxFact]
    public async Task SpawnedProcessGroup_IsTerminatedWhenTheSupervisingRelayDies()
    {
        if (!KataBwrapExecutor.TryProbeAvailability(out _))
            return;

        var root = NewRoot();
        var (workspace, _) = CreateTwoRuns(root);
        var ready = Path.Combine(workspace, "ready.txt");
        var terminated = Path.Combine(workspace, "terminated.txt");
        var daemonPidFile = Path.Combine(workspace, "daemon.pid");
        await using var harness = PodExecTestHarness.StartServer(root);
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);
        client.RegisterTrustedWorkspace(workspace);
        client.RegisterRuntimeHome(workspace, CreateRuntimeHome(root));

        var supervised = await client.StartSupervisedProcessAsync(
            $"(sleep 300 & echo $! > {Quote(daemonPidFile)}) ; " +
            $"trap 'printf terminated > {Quote(terminated)}; exit 0' TERM; " +
            $"printf ready > {Quote(ready)}; while :; do sleep 1; done",
            workspace,
            null,
            networkEnabled: false);

        try
        {
            await WaitForFileAsync(ready, TimeSpan.FromSeconds(20));
            await WaitForFileAsync(daemonPidFile, TimeSpan.FromSeconds(20));
            var daemonPid = int.Parse(File.ReadAllText(daemonPidFile).Trim());

            // Killing the relay is the AgentHost-crash scenario: the sidecar sees the disconnect and
            // must reap the whole sandboxed process group rather than stranding it.
            supervised.Process.Kill(entireProcessTree: true);

            await WaitForFileAsync(terminated, TimeSpan.FromSeconds(20));
            File.ReadAllText(terminated).Should().Be("terminated");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Directory.Exists($"/proc/{daemonPid}") && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Directory.Exists($"/proc/{daemonPid}").Should().BeFalse(
                "process-group termination must also reap processes the workload daemonised");
        }
        finally
        {
            await supervised.StopAsync(TimeSpan.FromSeconds(2));
            supervised.Process.Dispose();
        }
    }

    private static async Task<PodExecFrame> SendRawAsync(string socketPath, PodExecRequest request)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(PodExecJson.Serialize(request));
        var line = await reader.ReadLineAsync();
        return PodExecJson.Deserialize<PodExecFrame>(line ?? "{}")!;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"podexec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static (string Workspace, string Sibling) CreateTwoRuns(string root)
    {
        var workspace = Path.Combine(root, "run-a");
        var sibling = Path.Combine(root, "run-b");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "sibling-secret");
        return (workspace, sibling);
    }

    private static string CreateRuntimeHome(string root)
    {
        var home = Path.Combine(root, "runtime-home");
        Directory.CreateDirectory(Path.Combine(home, ".cache"));
        Directory.CreateDirectory(Path.Combine(home, ".local", "share"));
        Directory.CreateDirectory(Path.Combine(home, ".config"));
        return home;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        File.Exists(path).Should().BeTrue($"the sandboxed workload should create {path}");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

public sealed class SidecarLinuxFactAttribute : FactAttribute
{
    public SidecarLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Executor-sidecar isolation is a Linux-only boundary.";
    }
}
