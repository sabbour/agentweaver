using System.Diagnostics;
using System.Net.Sockets;
using Agentweaver.SandboxExec;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// The image-build capability must be decided by a real builder answering on the socket, not by an
/// environment variable recording an operator's intention and not by the mere existence of a socket
/// inode. A deployment that sets the variable but never patches the builder sidecar in, and a pod
/// whose builder crashed and left its socket file behind, would both otherwise advertise
/// <c>image_build</c> on a cluster where every build fails at connect time.
///
/// These tests bind real unix sockets rather than mocking the probe, because the whole point of
/// the change is that the executor observes the socket instead of trusting configuration. The path
/// is injected through the constructor rather than through the process environment: mutating
/// <c>AGENTWEAVER_IMAGE_BUILD_SOCKET</c> globally made every executor built by a concurrently
/// running test bind-mount this test's temporary directory, which broke an unrelated preview
/// teardown proof on CI.
/// </summary>
[Trait("Category", KataRuntimeGate.Category)]
public sealed class SandboxImageBuildSocketTests
{
    /// <summary>Nine bytes of empty SETTINGS frame — what a live gRPC server answers with.</summary>
    private static readonly byte[] SettingsFrame = [0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static SandboxCapability ImageBuildCapability(string socketPath) =>
        new KataBwrapExecutor(imageBuildSocket: socketPath)
            .DescribeCapabilities()
            .Single(capability => capability.Id == SandboxCapabilityIds.ImageBuild);

    /// <summary>
    /// Accepts one connection and writes <paramref name="reply"/>, standing in for whatever process
    /// happens to hold the socket path. Nothing here parses the probe's request: the probe is only
    /// entitled to conclude from what comes back.
    ///
    /// Runs on a dedicated background <see cref="Thread"/> rather than <see cref="Task.Run"/>: the
    /// probe's own receive timeout (750ms, see <c>KataBwrapExecutor.ImageBuildProbeTimeoutMs</c>) is
    /// sized for a healthy daemon answering in microseconds, not for this thread also having to wait
    /// its turn behind whatever else the shared ThreadPool is running for other parallel tests. A
    /// starved pool could delay <c>Accept()</c> past that budget and make a real builder look absent.
    /// Blocking here until the thread has actually started removes that scheduling variable, so the
    /// only latency left in the race is the accept-and-reply itself, comfortably inside the budget.
    /// </summary>
    private static Socket StartListener(string socketPath, byte[]? reply)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        using var started = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            started.Set();
            try
            {
                using var accepted = listener.Accept();
                if (reply is not null)
                {
                    accepted.Send(reply);
                }
            }
            catch (SocketException)
            {
                // The listener was disposed by the test; nothing to do.
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));

        return listener;
    }

    [SidecarLinuxFact]
    public void ImageBuild_IsUnavailableWhenNoBuilderSidecarPublishedASocket()
    {
        // The directory exists (the pod always mounts it) but nothing is listening: this is exactly
        // the stock cluster where the optional sidecar was never patched in.
        var directory = Directory.CreateTempSubdirectory("awx-buildsock-").FullName;
        try
        {
            var capability = ImageBuildCapability(Path.Combine(directory, "buildkitd.sock"));

            capability.State.Should().Be(
                SandboxCapabilityState.RequiresExternalService,
                "an absent socket must not be reported as a working builder");
            capability.Remediation.Should().NotBeNull();
            capability.Remediation!.Should().Contain("sandbox-buildkit-sidecar.yaml");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [SidecarLinuxFact]
    public void ImageBuild_IsUnavailableWhenTheBuilderDiedAndLeftItsSocketBehind()
    {
        // A crashed daemon does not unlink its socket. The inode outlives the process, so anything
        // that only asks "does this path exist" reports a builder that cannot be connected to.
        //
        // The "crash" has to happen in a real, separate process. Binding-then-disposing a Socket in
        // this test process used to leave the path behind, but since .NET 8 (dotnet/runtime#52103)
        // disposing a bound unix-socket Socket now unlinks its own path — which would make the very
        // cleanup this test needs to defeat run on its behalf, and the path would never exist for the
        // probe to observe. A child process killed with SIGKILL never runs that (or any) cleanup, so
        // the socket file it bound is left behind exactly as a daemon's would be after `kill -9`.
        var directory = Directory.CreateTempSubdirectory("awx-buildsock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        try
        {
            using var daemon = Process.Start(new ProcessStartInfo("python3", ["-c",
                "import os,socket,time\n" +
                "s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)\n" +
                "s.bind(os.environ['AWX_TEST_DEAD_SOCKET_PATH'])\n" +
                "s.listen(1)\n" +
                "time.sleep(60)\n"])
            {
                UseShellExecute = false,
                Environment = { ["AWX_TEST_DEAD_SOCKET_PATH"] = socketPath },
            })!;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(socketPath) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(20);
                }

                File.Exists(socketPath).Should().BeTrue(
                    "the simulated daemon must have bound the socket before it is killed");
            }
            finally
            {
                // SIGKILL on Linux: the process gets no chance to unlink the path itself.
                if (!daemon.HasExited)
                {
                    daemon.Kill(entireProcessTree: true);
                }

                daemon.WaitForExit();
            }

            File.Exists(socketPath).Should().BeTrue("the test needs a leftover socket inode to be meaningful");

            ImageBuildCapability(socketPath).State.Should().Be(
                SandboxCapabilityState.RequiresExternalService,
                "a socket nobody is accepting on is not a builder");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [SidecarLinuxFact]
    public void ImageBuild_IsUnavailableWhenSomethingOtherThanABuilderHoldsTheSocket()
    {
        // A sidecar that is still starting, or an unrelated process that took the path, accepts the
        // connection but never speaks the builder's protocol. Connect-level liveness alone would
        // call this a builder.
        var directory = Directory.CreateTempSubdirectory("awx-buildsock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        using var listener = StartListener(socketPath, "HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());
        try
        {
            ImageBuildCapability(socketPath).State.Should().Be(
                SandboxCapabilityState.RequiresExternalService,
                "a listener that is not a gRPC builder must not be advertised as one");
        }
        finally
        {
            listener.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [SidecarLinuxFact]
    public void ImageBuild_BecomesSupportedOnlyWhenARealBuilderAnswers()
    {
        var directory = Directory.CreateTempSubdirectory("awx-buildsock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        using var listener = StartListener(socketPath, SettingsFrame);
        try
        {
            var capability = ImageBuildCapability(socketPath);

            capability.State.Should().Be(SandboxCapabilityState.Supported);

            // The contract must keep disclosing the trade-off that buys this capability, so a
            // reviewer reading the capability response is never surprised by the rootful sidecar.
            capability.Detail.Should().Contain("not reachable from any other run");
            capability.Detail.Should().Contain("Residual risk");
        }
        finally
        {
            listener.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
