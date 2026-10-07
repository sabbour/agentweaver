using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentRuntimeBootstrapProfileRegistryTests
{
    [Fact]
    public void ResolveRequiresExactEnvironmentOwnerAndCurrentSandboxResource()
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var resource = new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", "claim-uid-a", 3);
        var profile = new EnvironmentRuntimeBootstrapProfile(
            "agenthost-v1",
            new Uri("https://bootstrap.example.test/configure"),
            new Uri("https://orchestrator.example.test/observations/runtime"));
        var registry = new EnvironmentRuntimeBootstrapProfileRegistry(
        [
            new EnvironmentRuntimeBootstrapProfileRegistration(owner, profile, resource)
        ]);

        Assert.Equal(profile, registry.Resolve(owner, profile.ProfileId, resource));
        AssertUnavailable(() => registry.Resolve(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-b"),
            profile.ProfileId,
            resource));
        AssertUnavailable(() => registry.Resolve(
            owner,
            profile.ProfileId,
            resource with { ResourceId = "claim-uid-b" }));
        AssertUnavailable(() => registry.Resolve(
            owner,
            profile.ProfileId,
            resource with { Generation = resource.Generation + 1 }));
        AssertUnavailable(() => registry.Resolve(owner, "unregistered-profile", resource));
    }

    [Theory]
    [InlineData("http://bootstrap.example.test/configure", "https://orchestrator.example.test/observations")]
    [InlineData("https://user@bootstrap.example.test/configure", "https://orchestrator.example.test/observations")]
    [InlineData("https://bootstrap.example.test/configure?next=other", "https://orchestrator.example.test/observations")]
    [InlineData("https://bootstrap.example.test/configure", "https://orchestrator.example.test/observations#fragment")]
    public void ProfilesRejectUntrustedEndpointUris(string configure, string observation)
    {
        var profile = new EnvironmentRuntimeBootstrapProfile(
            "agenthost-v1",
            new Uri(configure),
            new Uri(observation));

        Assert.Throws<ArgumentException>(() => profile.Validate());
    }

    private static void AssertUnavailable(Action resolve)
    {
        var exception = Assert.Throws<RuntimeAuthorizationException>(resolve);
        Assert.Equal("runtime_delivery_unavailable", exception.Code);
    }
}
