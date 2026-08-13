using System.Net.Sockets;
using Agentweaver.SandboxExec;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// The image-build capability must be decided by the presence of a real build daemon socket, not by
/// an environment variable recording an operator's intention. A deployment that sets the variable
/// but never patches the builder sidecar in would otherwise advertise <c>image_build</c> on a
/// cluster where every build fails at connect time.
///
/// These tests bind a real unix socket rather than mocking the probe, because the whole point of
/// the change is that the executor observes the socket instead of trusting configuration.
/// </summary>
[Trait("Category", KataRuntimeGate.Category)]
public sealed class SandboxImageBuildSocketTests
{
    private const string SocketVariable = "AGENTWEAVER_IMAGE_BUILD_SOCKET";

    private static string NewSocketDirectory() =>
        Directory.CreateTempSubdirectory("awx-buildsock-").FullName;

    [SidecarLinuxFact]
    public void ImageBuild_IsUnavailableWhenNoBuilderSidecarPublishedASocket()
    {
        var directory = NewSocketDirectory();
        var previous = Environment.GetEnvironmentVariable(SocketVariable);
        try
        {
            // The directory exists (the pod always mounts it) but nothing is listening: this is
            // exactly the stock cluster where the optional sidecar was never patched in.
            Environment.SetEnvironmentVariable(SocketVariable, Path.Combine(directory, "buildkitd.sock"));

            var capability = new KataBwrapExecutor()
                .DescribeCapabilities()
                .Single(c => c.Id == SandboxCapabilityIds.ImageBuild);

            capability.State.Should().Be(
                SandboxCapabilityState.RequiresExternalService,
                "an absent socket must not be reported as a working builder");
            capability.Remediation.Should().NotBeNull();
            capability.Remediation!.Should().Contain("sandbox-buildkit-sidecar.yaml");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SocketVariable, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [SidecarLinuxFact]
    public void ImageBuild_BecomesSupportedOnlyWhenARealSocketIsListening()
    {
        var directory = NewSocketDirectory();
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        var previous = Environment.GetEnvironmentVariable(SocketVariable);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            Environment.SetEnvironmentVariable(SocketVariable, socketPath);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);

            var capability = new KataBwrapExecutor()
                .DescribeCapabilities()
                .Single(c => c.Id == SandboxCapabilityIds.ImageBuild);

            capability.State.Should().Be(SandboxCapabilityState.Supported);

            // The contract must keep disclosing the trade-off that buys this capability, so a
            // reviewer reading the capability response is never surprised by the rootful sidecar.
            capability.Detail.Should().Contain("not reachable from any other run");
            capability.Detail.Should().Contain("Residual risk");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SocketVariable, previous);
            listener.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
