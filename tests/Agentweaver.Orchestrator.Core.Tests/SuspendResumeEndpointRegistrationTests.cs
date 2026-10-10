using System.Net;
using System.Text;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SuspendResumeEndpointRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalRuntimeOwnerControlsOnlyTheInternalSuspendRoute(bool configured)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve Projects services."));
        builder.Services.AddSingleton(new SessionSuspendResumeCoordinator());
        if (configured)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/"
            });
        var enabled = builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options);
        Assert.Equal(configured, enabled);
        await using var app = builder.Build();
        app.MapSuspendResumeEndpoints();
        if (enabled)
            app.MapRuntimeSuspendResumeEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        var publicRoutes = endpoints.Where(endpoint =>
            endpoint.RoutePattern.RawText!.StartsWith("/api/projects/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, publicRoutes.Length);
        Assert.All(publicRoutes, endpoint =>
        {
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
        });
        const string runtimePath = "/internal/runtime/suspend/require-current";
        Assert.Equal(configured, endpoints.Any(endpoint => endpoint.RoutePattern.RawText == runtimePath));

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (!configured)
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync(runtimePath,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await app.StopAsync();
    }
}
