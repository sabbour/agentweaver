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
[Trait("Category", KataRuntimeGate.Category)]
public sealed class PodExecSidecarTests
{
    /// <summary>
    /// Self-check for the bwrap-enabled CI job: when <c>AGENTWEAVER_REQUIRE_BWRAP=1</c> is set the
    /// runtime must really be there, so a broken or missing bubblewrap install fails the required
    /// gate instead of turning every sandboxed proof into a silent early return.
    /// </summary>
    [Fact]
    public void KataRuntimeGate_HasARealBubblewrapRuntimeWhenTheGateRequiresOne()
    {
        if (!KataRuntimeGate.IsRequired)
            return;

        KataRuntimeGate.BwrapAvailable(out var detail)
            .Should().BeTrue($"{KataRuntimeGate.RequireVariable}=1 promises a usable runtime, but {detail}");
    }

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
        if (!KataRuntimeGate.Available())
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
        if (!KataRuntimeGate.Available())
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
        if (!KataRuntimeGate.Available())
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
        if (!KataRuntimeGate.Available())
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
        if (!KataRuntimeGate.Available())
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

    /// <summary>
    /// Regression for a cleanup flaw found in review: when the command itself finishes normally
    /// (the wrapper exits on its own, not because the relay disconnected) but leaves a backgrounded
    /// descendant running, <c>TerminateAsync</c> used to return early as soon as it saw the wrapper's
    /// <c>Process.HasExited</c>, so the descendant was never signalled at all, and the spawn handler
    /// drained the workload's inherited stdout/stderr pipes -- which that descendant still held open
    /// -- before ever attempting cleanup, so the connection hung instead of ever reaching the Exit
    /// frame. Both must now happen in the opposite order: reap the recorded group first, then drain.
    /// </summary>
    [SidecarLinuxFact]
    public async Task SpawnedProcessGroup_IsReapedWhenTheCommandExitsWhileADescendantIsStillRunning()
    {
        if (!KataRuntimeGate.Available())
            return;

        var root = NewRoot();
        var (workspace, _) = CreateTwoRuns(root);
        var daemonPidFile = Path.Combine(workspace, "daemon.pid");
        var doneFile = Path.Combine(workspace, "done.txt");
        await using var harness = PodExecTestHarness.StartServer(root);
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);
        client.RegisterTrustedWorkspace(workspace);
        client.RegisterRuntimeHome(workspace, CreateRuntimeHome(root));

        // The command backgrounds a long-lived descendant and then exits on its own -- no relay
        // disconnect, no long-running loop keeping the wrapper alive. This is the shape a
        // model-controlled build server or watcher takes: `npm run dev &` followed by the invoking
        // shell returning. The trailing `sleep 0.2` (matching
        // KataBwrapExecutorTests.CompletedCommand_LeavesNoDaemonisedProcessesBehind) keeps the
        // sandboxed wrapper alive long enough for procfs-based child discovery to observe it before
        // it exits; without it the resolution race is indistinguishable from -- and would mask --
        // the cleanup regression this test targets.
        var supervised = await client.StartSupervisedProcessAsync(
            $"(sleep 300 & echo $! > {Quote(daemonPidFile)}) ; sleep 0.2 ; printf done > {Quote(doneFile)}",
            workspace,
            null,
            networkEnabled: false);

        try
        {
            await WaitForFileAsync(doneFile, TimeSpan.FromSeconds(20));
            await WaitForFileAsync(daemonPidFile, TimeSpan.FromSeconds(20));
            var daemonPid = int.Parse(File.ReadAllText(daemonPidFile).Trim());

            // The relay must observe a terminal Exit frame promptly. Before the fix, draining the
            // still-open stdout/stderr pipes ahead of cleanup meant this would hang until the test
            // itself timed out, because the backgrounded daemon never closed them.
            await supervised.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Directory.Exists($"/proc/{daemonPid}") && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Directory.Exists($"/proc/{daemonPid}").Should().BeFalse(
                "the spawn path must reap the recorded process group even though the wrapper had "
                + "already exited on its own");
        }
        finally
        {
            await supervised.StopAsync(TimeSpan.FromSeconds(2));
            supervised.Process.Dispose();
        }
    }

    /// <summary>
    /// Same regression as <see cref="SpawnedProcessGroup_IsReapedWhenTheCommandExitsWhileADescendantIsStillRunning"/>
    /// for the one-shot exec path, exercised through the sidecar socket protocol (not the direct
    /// <c>KataBwrapExecutor</c> unit test) so the <c>HandleExecAsync</c> boundary itself is covered.
    /// </summary>
    [SidecarLinuxFact]
    public async Task Execute_ReapsADaemonisedDescendantThroughTheSidecarBoundary()
    {
        if (!KataRuntimeGate.Available())
            return;

        var root = NewRoot();
        var (workspace, _) = CreateTwoRuns(root);
        var daemonPidFile = Path.Combine(workspace, "daemon.pid");
        await using var harness = PodExecTestHarness.StartServer(root);
        var client = PodExecTestHarness.CreateClient(harness.SocketPath);
        client.RegisterTrustedWorkspace(workspace);
        client.RegisterRuntimeHome(workspace, CreateRuntimeHome(root));

        var result = await client.ExecuteAsync(new SandboxCommand(
                // The trailing `sleep 0.2` (matching
                // KataBwrapExecutorTests.CompletedCommand_LeavesNoDaemonisedProcessesBehind) keeps
                // the sandboxed wrapper alive long enough for procfs-based child discovery to
                // observe it before it exits.
                $"(sleep 300 & echo $! > {Quote(daemonPidFile)}) ; sleep 0.2 ; printf done",
                workspace,
                null,
                new SandboxFsPolicy([workspace], [], []),
                20_000,
                NetworkEnabled: false))
            .WaitAsync(TimeSpan.FromSeconds(25));

        result.ExitCode.Should().Be(0, result.Stderr);
        result.TimedOut.Should().BeFalse(
            "a daemonised descendant must not make the sidecar exec path hang or time out");

        var daemonPid = int.Parse(File.ReadAllText(daemonPidFile).Trim());
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Directory.Exists($"/proc/{daemonPid}") && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Directory.Exists($"/proc/{daemonPid}").Should().BeFalse(
            "the executor sidecar exec path must reap the run's process group too, not just the "
            + "directly-invoked executor covered by KataBwrapExecutorTests");
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
        // A required bwrap gate must never report success by skipping.
        if (!OperatingSystem.IsLinux() && !KataRuntimeGate.IsRequired)
            Skip = "Executor-sidecar isolation is a Linux-only boundary.";
    }
}
