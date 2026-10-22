using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ProjectSummaryRoutesSerializeCurrentRoleNamesAndScopes()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "role-summary-api-" + suffix;
        var tenantId = "role-summary-tenant-" + suffix;
        var scopes = new[] { "api.read", "projects.admin" };
        var admin = await SeedCallerAsync(db, store, subject, tenantId, scopes,
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await projects.CreateProjectAsync(admin, "Role summary API", CancellationToken.None);
        await store.AssignRoleAsync(
            admin.MembershipId, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorityRole.Owner, "fixture");

        // Exercise the admitted service routes so the test covers HTTP serialization as well as owner values.
        using var host = await StartProjectSummaryEndpointHostAsync(fixture.DataSource);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", string.Join(' ', scopes));
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);

        using var listResponse = await client.GetAsync("/api/projects/");
        Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listedProject = Assert.Single(listDocument.RootElement.EnumerateArray(),
            item => item.GetProperty("projectId").GetString() == project.ProjectId);
        AssertContainsRole(listedProject, "tenant", tenantId, "tenantAdmin");
        AssertContainsRole(listedProject, "project", project.ProjectId, "owner");

        using var projectResponse = await client.GetAsync($"/api/projects/{project.ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, projectResponse.StatusCode);
        using var projectDocument = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync());
        AssertContainsRole(projectDocument.RootElement, "tenant", tenantId, "tenantAdmin");
        AssertContainsRole(projectDocument.RootElement, "project", project.ProjectId, "owner");
    }

    private static async Task<IHost> StartProjectSummaryEndpointHostAsync(Npgsql.NpgsqlDataSource dataSource) =>
        await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(ProjectSummaryTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                            ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddDbContext<ProjectsConfigDbContext>(options => options.UseNpgsql(
                        dataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddScoped<ProjectsConfigService>();
                    services.ConfigureHttpJsonOptions(options =>
                        options.SerializerOptions.Converters.Add(
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapProjectConfigEndpoints());
                });
            })
            .StartAsync();

    private static void AssertContainsRole(JsonElement summary, string resourceType, string resourceId, string role) =>
        Assert.Contains(summary.GetProperty("effectiveRoles").EnumerateArray(), item =>
            item.GetProperty("resourceType").GetString() == resourceType &&
            item.GetProperty("resourceId").GetString() == resourceId &&
            item.GetProperty("role").GetString() == role);

    private sealed class ProjectSummaryTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ProjectSummaryTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrEmpty(subject))
                return Task.FromResult(AuthenticateResult.NoResult());

            var scope = Request.Headers["X-Test-Scope"].ToString();
            var identity = new ClaimsIdentity(
                [new Claim("sub", subject), new Claim("scope", scope)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
