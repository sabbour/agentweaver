using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed class SandboxPodObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
    private static readonly EnvironmentGenerationFence Fence = new(
        new("tenant", "project", "run", "environment"), 3);
    private static readonly ProviderResourceRef Resource = new(ProviderSeam.Sandbox, "agent-sandbox", "claim-uid", 4);
    private static readonly Guid Operation = Guid.Parse("a39c8071-1bcd-4096-bd5c-51680bfb3d77");
    private static readonly SandboxDescribeRequest Request = new(
        Fence, Resource, 5,
        new("agent-sandbox", "1.0.0", 1, "options",
            JsonSerializer.SerializeToElement(new { secret = "must-not-leak" }),
            JsonSerializer.SerializeToElement(new { recoverySecret = "must-not-leak" })),
        Now.AddSeconds(-10));
    private static readonly SandboxPodObservation Pod = new(
        1, Now, Fence, Resource, 5, Operation, new string('a', 64),
        "namespace", "claim-uid", "sandbox-uid", "pod-name", "pod-uid", "42", 1, "Running",
        new("namespace", "workspace-pvc", "pvc-uid", "/workspace/project", false), true);

    [Theory]
    [InlineData("/pod-observation")]
    [InlineData("/p1-creation-binding")]
    public void PodObservationRouteRequiresAuthenticationAndIsReadOnly(string suffix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EnvironmentLifecycleManager>();
        builder.Services.AddScoped<EnvironmentEgressManager>();
        builder.Services.AddScoped<EnvironmentWorkspaceVolumeManager>();
        builder.Services.AddScoped<EnvironmentSandboxManager>();
        using var app = builder.Build();
        app.MapEnvironmentEndpoints();
        var route = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText!.EndsWith(
                "/runtime-bootstrap/profiles/{profileId}" + suffix, StringComparison.Ordinal));
        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.DoesNotContain(route.Metadata, item => item is IAllowAnonymous);
        Assert.Equal(["GET"], route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public void ObservationPinsDistinctIdentitiesAndDoesNotSerializeProviderSecrets()
    {
        Assert.Equal(Pod, Pod.ValidateFor(Request));
        var observation = Observation();
        Assert.Equal(observation, observation.ValidateFor(Request));
        var json = JsonSerializer.Serialize(observation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("must-not-leak", json);
        Assert.DoesNotContain("recoverySecret", json);
        Assert.DoesNotContain("optionsSnapshot", json);
        Assert.DoesNotContain("invocationId", json);
        Assert.DoesNotContain("mountedStorageVerified", json);
        Assert.Contains("\"podUid\":\"pod-uid\"", json);
        Assert.Contains("\"claimUid\":\"claim-uid\"", json);
        Assert.Contains("\"sandboxUid\":\"sandbox-uid\"", json);
        Assert.Contains("\"isolationConfigurationVerified\":true", json);
    }

    [Fact]
    public void ObservationRejectsForeignOwnersStaleFencesAndMissingIdentity()
    {
        foreach (var invalid in new[]
        {
            Pod with { ContractVersion = 2 },
            Pod with { ObservedAt = Request.LeaseCreatedAt.AddTicks(-1) },
            Pod with { EnvironmentFence = new(new("foreign", "project", "run", "environment"), 3) },
            Pod with { EnvironmentFence = new(Fence.Owner, 4) },
            Pod with { Resource = Resource with { Generation = 5 } },
            Pod with { FencingGeneration = 6 },
            Pod with { ProvisionOperationId = Guid.Empty },
            Pod with { PodUid = "" },
            Pod with { PodResourceVersion = "" },
            Pod with { PodGeneration = 0 },
            Pod with { ClaimUid = "foreign" },
            Pod with { ClusterIdentity = "" },
            Pod with { PodPhase = "credentials-must-not-leak" },
            Pod with { WorkspaceAttachment = Pod.WorkspaceAttachment! with { Namespace = "foreign" } }
        })
            Assert.Throws<ArgumentException>(() => invalid.ValidateFor(Request));
        Assert.Throws<ArgumentException>(() => (Observation() with
        {
            RuntimeEndpoint = new(new("https://runtime.namespace.svc:8443/"), "10.0.0.1", "other-pod", "service")
        }).ValidateFor(Request));
        Assert.Throws<ArgumentException>(() => (Observation() with
        {
            ProvisionOperationId = Guid.NewGuid()
        }).ValidateFor(Request));
    }

    [Fact]
    public void StrictReadRejectsMissingAttachmentIsolationExpiredOrMismatchedCurrentPlacement()
    {
        var context = new EnvironmentRuntimeReadinessContext(
            1, new(1, "tenant", "project", "run", "environment", 3, 5, 5, 7, Now.AddMinutes(1),
                Resource, new(Operation), new("placement"), "profile",
                new("https://runtime.example/runtime/v1/configure"), new("https://runtime.example/")),
            Request.LeaseCreatedAt, Observation(), new(30, 30, 30, 30, 30, 120), "/workspace/project");
        Assert.Same(Pod, EnvironmentRuntimePlacementReader.RequirePodObservation(context, Now));
        foreach (var invalid in new EnvironmentRuntimeReadinessContext?[]
        {
            null,
            context with { Observation = Observation() with { PodObservation = null } },
            context with { Observation = Observation() with { PodObservation = Pod with { WorkspaceAttachment = null } } },
            context with { Observation = Observation() with { PodObservation = Pod with { IsolationConfigurationVerified = false } } },
            context with { Placement = context.Placement with { CurrentFencingGeneration = 6 } },
            context with { Placement = context.Placement with { EnvironmentId = "foreign" } },
            context with { Placement = context.Placement with { LeaseExpiresAt = Now } },
            context with { WorkspaceMountPath = "/different" },
            context with { Observation = Observation() with { PodObservation = Pod with { ObservedAt = Now.AddSeconds(1) } } }
        })
        {
            var error = Assert.Throws<RuntimeAuthorizationException>(() =>
                EnvironmentRuntimePlacementReader.RequirePodObservation(invalid, Now));
            Assert.Equal("runtime_pod_observation_unavailable", error.Code);
        }
    }

    private static SandboxObservation Observation() =>
        new(Resource, SandboxObservedState.Pending, 5, true, true, null, [],
            ProvisionOperationId: Operation) { PodObservation = Pod };
}
