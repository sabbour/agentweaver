using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class NativeAcsPolicyRequestGateTests
{
    [Fact]
    public void LimitsValidationPerIssuerAndActor()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var gate = new NativeAcsPolicyRequestGate(cache, TimeProvider.System);

        for (var index = 0; index < 6; index++)
            Assert.True(gate.TryAcquireCallerRequest("issuer-a", "actor-a"));

        Assert.False(gate.TryAcquireCallerRequest("issuer-a", "actor-a"));
        Assert.True(gate.TryAcquireCallerRequest("issuer-a", "actor-b"));
        Assert.True(gate.TryAcquireCallerRequest("issuer-b", "actor-a"));
    }

    [Fact]
    public void ValidationSlotRejectsConcurrentWorkWithoutWaiting()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var gate = new NativeAcsPolicyRequestGate(cache, TimeProvider.System);
        using var first = gate.TryEnterValidationSlot();

        Assert.NotNull(first);
        Assert.Null(gate.TryEnterValidationSlot());
        first.Dispose();

        using var next = gate.TryEnterValidationSlot();
        Assert.NotNull(next);
    }

    [Fact]
    public void PolicyRoutesBuildWhenPolicyServicesAreNotRegistered()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapNativeAcsPolicyEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Name: endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!,
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()))
            .OrderBy(route => route.Name, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            (Name: "ListPlatformNativeAcsPolicyRevisions", Method: "GET"),
            (Name: "ListProjectNativeAcsPolicies", Method: "GET"),
            (Name: "ListProjectNativeAcsPolicyRevisions", Method: "GET"),
            (Name: "ReadPlatformNativeAcsPolicy", Method: "GET"),
            (Name: "ReadPlatformNativeAcsPolicyRevision", Method: "GET"),
            (Name: "ReadProjectNativeAcsPolicy", Method: "GET"),
            (Name: "ReadProjectNativeAcsPolicyRevision", Method: "GET"),
            (Name: "ValidatePlatformNativeAcsPolicy", Method: "POST"),
            (Name: "ValidateProjectNativeAcsPolicy", Method: "POST"),
            (Name: "WritePlatformNativeAcsPolicy", Method: "PUT"),
        };

        Assert.Equal(expected, routes);
    }
}
