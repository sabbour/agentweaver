using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void ProfileResolutionRouteIsAnAuthenticatedPost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EnvironmentRuntimeBootstrapDelivery>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        using var app = builder.Build();

        app.MapEnvironmentRuntimeBootstrap();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText!.EndsWith(
                "/runtime-bootstrap/profiles/{profileId}/resolve", StringComparison.Ordinal));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public void ReplacementPinsTheAcceptedActorSelectionAndExactSourceProfileCopy()
    {
        var (ownerContext, source, replacement, _, _, _) = CreateReplacement();
        var profileId = replacement.Registration.Profile.ProfileId;

        Assert.True(replacement.MatchesOwnerContext(ownerContext));
        Assert.True(replacement.MatchesSourceProfile(source, profileId));
        Assert.Equal(replacement, replacement.Validate());
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var reloaded = JsonSerializer.Deserialize<EnvironmentRuntimeBootstrapProfileReplacement>(
            JsonSerializer.Serialize(replacement, jsonOptions), jsonOptions)!.Validate();
        Assert.True(reloaded.MatchesOwnerContext(ownerContext));
        Assert.True(reloaded.MatchesSourceProfile(source, profileId));
        Assert.False(replacement.MatchesOwnerContext(ownerContext with
        {
            ActorId = "22222222-2222-4222-8222-222222222222"
        }));
        Assert.False(replacement.MatchesOwnerContext(ownerContext with
        {
            AcceptedSelectionHash = new string('b', 64)
        }));
        Assert.False(replacement.MatchesSourceProfile(source with
        {
            Profile = source.Profile with
            {
                ObservationEndpoint = new Uri("https://orchestrator-changed.example.test/observations")
            }
        }, profileId));

        var changedProfile = replacement.Registration.Profile with
        {
            McpServer = new("changed-mcp", new Uri("https://mcp-changed.example.test/"))
        };
        var changedCopy = replacement with
        {
            Registration = replacement.Registration with { Profile = changedProfile },
            RegistrationProfileConfigurationHash =
                EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(changedProfile)
        };
        Assert.False(changedCopy.MatchesSourceProfile(source, profileId));
        Assert.Throws<ArgumentException>(() =>
            (replacement with { RegistrationProfileConfigurationHash = new string('d', 64) }).Validate());
    }

    [Fact]
    public void ReplacementProfileIdIncludesRequestedSourceActorAndSelection()
    {
        var (ownerContext, source, _, placement, endpoint, _) = CreateReplacement();
        string CreateId(
            string? requestedSourceProfileId = null,
            string? actorId = null,
            string? selectionHash = null) =>
            EnvironmentRuntimePlacementReader.CreateReplacementProfileId(
                new(ownerContext.TenantId, ownerContext.ProjectId, ownerContext.RunId, placement.EnvironmentId),
                requestedSourceProfileId ?? source.Profile.ProfileId,
                source.Profile.ProfileId,
                ownerContext.ActorIssuer,
                actorId ?? ownerContext.ActorId,
                selectionHash ?? ownerContext.AcceptedSelectionHash,
                placement,
                endpoint);

        var id = CreateId();
        Assert.NotEqual(id, CreateId(requestedSourceProfileId: "prior-profile"));
        Assert.NotEqual(id, CreateId(actorId: "22222222-2222-4222-8222-222222222222"));
        Assert.NotEqual(id, CreateId(selectionHash: new string('b', 64)));
    }

    private static void AssertUnavailable(Action resolve)
    {
        var exception = Assert.Throws<RuntimeAuthorizationException>(resolve);
        Assert.Equal("runtime_delivery_unavailable", exception.Code);
    }

    private static (
        RuntimeOwnerContext OwnerContext,
        EnvironmentRuntimeBootstrapProfileRegistration Source,
        EnvironmentRuntimeBootstrapProfileReplacement Replacement,
        EnvironmentSandboxPlacementProjectionV1 Placement,
        SandboxRuntimeHttpsEndpoint Endpoint,
        EnvironmentRuntimeBootstrapProfile Profile) CreateReplacement()
    {
        const string actorIssuer = "https://runtime-owner.example.test/";
        const string actorId = "11111111-1111-4111-8111-111111111111";
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var sourceProfile = new EnvironmentRuntimeBootstrapProfile(
            "agenthost-v1",
            new Uri("https://bootstrap.example.test/configure"),
            new Uri("https://orchestrator.example.test/observations/runtime"))
        {
            McpServer = new("accepted-mcp", new Uri("https://mcp.example.test/"))
        };
        var source = new EnvironmentRuntimeBootstrapProfileRegistration(owner, sourceProfile,
            new(ProviderSeam.Sandbox, "agent-sandbox", "claim-original", 1));
        var ownerContext = new RuntimeOwnerContext(
            1, actorIssuer, actorId, owner.TenantId, owner.ProjectId, owner.RunId,
            "session-a", "agent-a", "model-a", "turn-a", 1, 1, 1, "catalog-a",
            new string('a', 64), 1, 1, 1, 1);
        var endpoint = new SandboxRuntimeHttpsEndpoint(
            new Uri("https://agenthost-runtime.example.test:8443/"),
            "10.21.0.4", "pod-a", "service-a")
        {
            ReceiverConfigureEndpoint =
                new Uri("https://agenthost-runtime.example.test:8443/runtime/v1/configure")
        };
        var placement = new EnvironmentSandboxPlacementProjectionV1(
            1, owner.TenantId, owner.ProjectId, owner.RunId, owner.EnvironmentId,
            2, 2, 2, 2, DateTimeOffset.UtcNow.AddMinutes(5), true, SandboxLeaseState.Active,
            new(ProviderSeam.Sandbox, "agent-sandbox", "claim-current", 2),
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("runtime-pool-a"));
        var replacementProfileId = EnvironmentRuntimePlacementReader.CreateReplacementProfileId(
            owner, sourceProfile.ProfileId, sourceProfile.ProfileId,
            ownerContext.ActorIssuer, ownerContext.ActorId, ownerContext.AcceptedSelectionHash,
            placement, endpoint);
        var replacementProfile = sourceProfile with
        {
            ProfileId = replacementProfileId,
            ConfigureEndpoint = endpoint.ReceiverConfigureEndpoint
                ?? throw new InvalidOperationException("The controlled endpoint has no configure address.")
        };
        var registration = new EnvironmentRuntimeBootstrapProfileRegistration(
            owner, replacementProfile, placement.Resource);
        var replacement = new EnvironmentRuntimeBootstrapProfileReplacement(
            1, sourceProfile.ProfileId, sourceProfile.ProfileId, actorIssuer, actorId,
            ownerContext.AcceptedSelectionHash,
            EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(sourceProfile),
            EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(replacementProfile),
            registration, 2, 2, 2, 2, placement.LeaseExpiresAt, endpoint).Validate();
        return (ownerContext, source, replacement, placement, endpoint, sourceProfile);
    }
}
