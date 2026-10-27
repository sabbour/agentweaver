using System.Net;
using System.Text.Json;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class P1ProjectSetupEndpointUnavailableTests
{
    [Fact]
    public async Task P1SetupEndpointReturnsClearUnavailableResponseWithoutAuthorityCapability()
    {
        var capability = P1ProjectSetupAuthorityCapability.CreateOptional(
            new ConfigurationBuilder().Build(),
            "Host=projects.db.example;Port=5432;Database=projects;Username=projects_runtime;SSL Mode=VerifyFull");
        Assert.False(capability.IsAvailable);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication("p1-test")
            .AddScheme<AuthenticationSchemeOptions, ProjectsConfigPostgresTests.P1SetupTestAuthenticationHandler>(
                "p1-test",
                _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(capability);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapP1ProjectSetupEndpoint();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/p1/invocations/{Guid.NewGuid():D}/setup");
        request.Headers.Add("X-Test-Subject", "approved-subject");
        request.Headers.Add("X-Test-Scope", "api.read projects.admin projects.orchestrator");
        request.Headers.Add("X-Agentweaver-Tenant", "approved-tenant");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "project_setup_authority_unavailable",
            problem.RootElement.GetProperty("code").GetString());
    }
}
