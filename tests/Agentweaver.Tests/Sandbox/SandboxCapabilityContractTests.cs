using System;
using System.Linq;
using Agentweaver.SandboxExec;
using FluentAssertions;
using Xunit;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// The capability contract is the answer to "what can a run actually do in the sandbox?".
///
/// It exists because the alternative — discovering halfway through a task that the executor cannot
/// perform an operation — is indistinguishable from a bug, and because an operation that the
/// platform genuinely cannot perform (winget on Linux) must be declared explicitly rather than
/// silently omitted from the list. These tests pin both properties.
/// </summary>
public class SandboxCapabilityContractTests
{
    private const string Backend = "kata-sidecar-bwrap-fs";

    private static SandboxCapability Capability(
        string id,
        bool writableSystemRoot = true,
        string? imageBuildEndpoint = null) =>
        SandboxCapabilityProbe
            .Describe(Backend, writableSystemRoot, imageBuildEndpoint)
            .Single(capability => capability.Id == id);

    [Fact]
    public void Contract_DeclaresEveryKnownCapabilityExactlyOnce()
    {
        var expected = new[]
        {
            SandboxCapabilityIds.NpmInstall,
            SandboxCapabilityIds.NuGetRestore,
            SandboxCapabilityIds.AptInstall,
            SandboxCapabilityIds.ImageBuild,
            SandboxCapabilityIds.PreviewPortBinding,
            SandboxCapabilityIds.WingetInstall,
        };

        var reported = SandboxCapabilityProbe.Describe(Backend, writableSystemRootAvailable: true);

        reported.Select(capability => capability.Id).Should().BeEquivalentTo(expected);
        reported.Select(capability => capability.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryUnsupportedCapability_ExplainsItselfAndOffersAPath()
    {
        var reported = SandboxCapabilityProbe.Describe(Backend, writableSystemRootAvailable: false);

        foreach (var capability in reported.Where(capability => !capability.IsSupported))
        {
            capability.Detail.Should().NotBeNullOrWhiteSpace(
                $"'{capability.Id}' must say why it is {capability.State}");
            capability.Remediation.Should().NotBeNullOrWhiteSpace(
                $"'{capability.Id}' must say what would make it supported");
        }
    }

    [Fact]
    public void EverySupportedCapability_OmitsRemediation()
    {
        var reported = SandboxCapabilityProbe.Describe(
            Backend,
            writableSystemRootAvailable: true,
            imageBuildEndpoint: "tcp://build-broker.agentweaver.svc:1234");

        foreach (var capability in reported.Where(capability => capability.IsSupported))
            capability.Remediation.Should().BeNull($"'{capability.Id}' needs no remediation");
    }

    /// <summary>
    /// winget is a Windows-only package manager, so no capability, mount or policy change can make
    /// it run inside a Linux Kata VM. The contract must therefore say <c>UnsupportedOnPlatform</c>
    /// — never "supported", and never absent — and must point at the Windows executor backend that
    /// a winget requirement actually needs.
    /// </summary>
    [Fact]
    public void Winget_IsDeclaredUnsupportedOnLinuxAndPointsAtAWindowsExecutor()
    {
        var winget = Capability(SandboxCapabilityIds.WingetInstall);

        winget.IsSupported.Should().BeFalse();
        if (OperatingSystem.IsLinux())
        {
            winget.State.Should().Be(SandboxCapabilityState.UnsupportedOnPlatform);
            winget.Detail.Should().Contain("Windows-only");
            winget.Detail.Should().Contain(Backend);
        }

        winget.Remediation.Should().Contain("Windows executor");
    }

    /// <summary>
    /// A caller must be able to distinguish "retry later / different config" from "this will never
    /// work here", because only the second means the work has to move to another executor.
    /// </summary>
    [Fact]
    public void UnsupportedOnPlatform_IsReservedForCapabilitiesNoConfigurationCanEnable()
    {
        var everythingAvailable = SandboxCapabilityProbe.Describe(
            Backend,
            writableSystemRootAvailable: true,
            imageBuildEndpoint: "tcp://build-broker.agentweaver.svc:1234");

        everythingAvailable
            .Where(capability => capability.State == SandboxCapabilityState.UnsupportedOnPlatform)
            .Select(capability => capability.Id)
            .Should()
            .BeEquivalentTo(
                OperatingSystem.IsLinux() ? [SandboxCapabilityIds.WingetInstall] : Array.Empty<string>(),
                "only winget is structurally impossible on the Linux executor");
    }

    [Fact]
    public void AptInstall_TracksWhetherTheRunCanGetAWritableSystemRoot()
    {
        Capability(SandboxCapabilityIds.AptInstall, writableSystemRoot: true)
            .State.Should().Be(SandboxCapabilityState.Supported);

        var unavailable = Capability(SandboxCapabilityIds.AptInstall, writableSystemRoot: false);
        unavailable.State.Should().Be(SandboxCapabilityState.Unavailable);
        unavailable.Remediation.Should().Contain(SandboxCapabilityProbe.RunRootHelperPath);
    }

    /// <summary>
    /// Image builds are not "unsupported": they are performed by a separate, differently-privileged
    /// builder sidecar. The distinction matters because the remediation is a deployment change, not
    /// a different operating system — and because the sandbox container itself must never gain the
    /// privileges BuildKit needs.
    /// </summary>
    [Fact]
    public void ImageBuild_RequiresABuilderSidecarUntilOneIsPresent()
    {
        var withoutBuilder = Capability(SandboxCapabilityIds.ImageBuild);
        withoutBuilder.State.Should().Be(SandboxCapabilityState.RequiresExternalService);
        withoutBuilder.Detail.Should().Contain("CAP_SYS_ADMIN");
        withoutBuilder.Detail.Should().Contain("CAP_NET_ADMIN");
        withoutBuilder.Remediation.Should().Contain("k8s/optional/sandbox-buildkit-sidecar.yaml");

        var withBuilder = Capability(
            SandboxCapabilityIds.ImageBuild,
            imageBuildEndpoint: "unix:///run/buildkit/buildkitd.sock");
        withBuilder.State.Should().Be(SandboxCapabilityState.Supported);

        // The builder is per-pod, so the cross-run channel the shared broker had is gone. Callers
        // must be told that the boundary is the Kata VM, and told the residual risk that remains.
        withBuilder.Detail.Should().Contain("not reachable from any other run");
        withBuilder.Detail.Should().Contain("Residual risk");

        // A capability that is "Supported" but silently drops network from every RUN step would
        // strand an agent mid-task on `RUN apt-get install`, which is precisely the failure the
        // contract exists to prevent. The limit must be part of the declaration, not just the docs.
        withBuilder.Detail.Should().Contain("RUN apt-get install");
        withBuilder.Detail.Should().Contain("loopback only");
        withBuilder.Detail.Should().Contain("Base images still pull");
    }

    /// <summary>
    /// npm, NuGet and preview port binding need nothing beyond the run's own workspace and the
    /// pod's shared network namespace, so they must not regress into a state that makes callers
    /// route them elsewhere.
    /// </summary>
    [Theory]
    [InlineData(SandboxCapabilityIds.NpmInstall)]
    [InlineData(SandboxCapabilityIds.NuGetRestore)]
    [InlineData(SandboxCapabilityIds.PreviewPortBinding)]
    public void WorkspaceOnlyCapabilities_AreSupportedRegardlessOfSystemRootAvailability(string id)
    {
        Capability(id, writableSystemRoot: false).IsSupported.Should().BeTrue();
        Capability(id, writableSystemRoot: true).IsSupported.Should().BeTrue();
    }
}
