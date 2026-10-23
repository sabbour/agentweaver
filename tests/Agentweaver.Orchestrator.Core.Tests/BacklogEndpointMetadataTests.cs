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
        builder.Services.AddSingleton<OrchestratorOptions>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<BacklogOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinationOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinatorDecisionOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<EventsAddressedMessageClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinatorRunSelectionContextStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        using var app = builder.Build();
        app.MapBacklogEndpoints();

        const string dependencyPath =
            "/api/projects/{projectId}/runs/{runId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}";
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route =>
                route.RoutePattern.RawText == dependencyPath &&
                route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                    "DELETE", StringComparer.Ordinal) == true);

        Assert.Equal(["DELETE"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var accepts = Assert.IsAssignableFrom<IAcceptsMetadata>(
            endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
        Assert.Equal(typeof(BacklogGraphRevisionRequest), accepts.RequestType);
        Assert.False(accepts.IsOptional);

        var promotion = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(route => route.RoutePattern.RawText == "/api/projects/{projectId}/runs/{runId}/backlog/promotions");
        Assert.Equal(typeof(PromoteBacklogWorkPlanRequest),
            promotion.Metadata.GetMetadata<IAcceptsMetadata>()!.RequestType);
    }

    [Fact]
    public void ProjectBacklogMutationRoutesHaveRequiredRevisionBodies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<OrchestratorOptions>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<BacklogOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinationOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinatorDecisionOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<EventsAddressedMessageClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinatorRunSelectionContextStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        using var app = builder.Build();
        app.MapBacklogEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(route => route.RoutePattern.RawText?.StartsWith(
                "/api/projects/{projectId}/backlog/tasks/", StringComparison.Ordinal) == true)
            .ToArray();

        AssertRoute(routes, "/api/projects/{projectId}/backlog/tasks/{taskId}/state",
            "PUT", typeof(SetBacklogTaskStateRequest));
        AssertRoute(routes, "/api/projects/{projectId}/backlog/tasks/{taskId}/archive",
            "POST", typeof(BacklogTaskRevisionRequest));
        AssertRoute(routes,
            "/api/projects/{projectId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}",
            "PUT", typeof(BacklogGraphRevisionRequest));
        AssertRoute(routes,
            "/api/projects/{projectId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}",
            "DELETE", typeof(BacklogGraphRevisionRequest));
    }

    private static void AssertRoute(
        IReadOnlyCollection<RouteEndpoint> routes,
        string path,
        string method,
        Type requestType)
    {
        var endpoint = Assert.Single(routes, route =>
            route.RoutePattern.RawText == path &&
            route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method, StringComparer.Ordinal) == true);
        var accepts = Assert.IsAssignableFrom<IAcceptsMetadata>(
            endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
        Assert.Equal(requestType, accepts.RequestType);
        Assert.False(accepts.IsOptional);
    }
}
