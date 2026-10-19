using Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Environment.Tests;

/// <summary>Checks that Environment connection routes can be built by ASP.NET Core.</summary>
public sealed class RemoteMcpConnectionEndpointRegistrationTests
{
    /// <summary>Builds the endpoint data source so unsupported inferred request bodies fail the test.</summary>
    [Fact]
    public async Task ConnectionRoutesBuildWithExplicitDeleteBodyContract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<RemoteMcpConnectionStore>();
        await using var app = builder.Build();

        app.MapRemoteMcpConnectionEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        var deleteEndpoint = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/api/projects/{projectId}/remote-mcp/connections/{connectionId:guid}" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Delete) == true);

        Assert.NotNull(deleteEndpoint.RequestDelegate);
    }
}
