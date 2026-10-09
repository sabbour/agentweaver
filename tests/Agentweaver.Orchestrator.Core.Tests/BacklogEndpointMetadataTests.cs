using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class BacklogEndpointMetadataTests
{
    [Fact]
    public void DeleteDependencyRouteHasMaterializableRequestBodyMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapBacklogEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => route.RoutePattern.RawText ==
                "/api/projects/{projectId}/runs/{runId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}");

        Assert.Equal(["DELETE"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
    }
}
