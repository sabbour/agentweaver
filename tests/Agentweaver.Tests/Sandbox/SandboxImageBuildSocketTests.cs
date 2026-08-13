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
/// the change is that the executor observes the socket instead of trusting configuration. The path
/// is injected through the constructor rather than through the process environment: mutating
/// <c>AGENTWEAVER_IMAGE_BUILD_SOCKET</c> globally made every executor built by a concurrently
/// running test bind-mount this test's temporary directory, which broke an unrelated preview
/// teardown proof on CI.
/// </summary>
[Trait("Category", KataRuntimeGate.Category)]
public sealed class SandboxImageBuildSocketTests
{
    private static SandboxCapability ImageBuildCapability(string socketPath) =>
        new KataBwrapExecutor(imageBuildSocket: socketPath)
            .DescribeCapabilities()
            .Single(capability => capability.Id == SandboxCapabilityIds.ImageBuild);

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
    public void ImageBuild_BecomesSupportedOnlyWhenARealSocketIsListening()
    {
        var directory = Directory.CreateTempSubdirectory("awx-buildsock-").FullName;
        var socketPath = Path.Combine(directory, "buildkitd.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);

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
