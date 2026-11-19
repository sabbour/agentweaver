extern alias ProjectsService;

using System.Net;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using ProjectsProgram = ProjectsService::Program;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task UnreadableInstallationSurvivesActualOwnerRestartAndRejectsUnsafeGrants()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var seed = test.CreateDbContext();
            var platform = await test.CreateReferenceAdminAsync(seed);
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = "restart-" + suffix;
            var subject = "owner-" + suffix;
            var authority = new ProjectsConfigPrivilegedAuthorityStore(test.CreateDbContextOptions(), TimeProvider.System);
            var tenantOwner = await SeedCallerAsync(seed, authority, subject, tenant, ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
            var project = await new ProjectsConfigService(seed, CreateProviderCatalog(), TimeProvider.System)
                .CreateProjectAsync(tenantOwner, "Restart test", CancellationToken.None);
            var connectionString = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
            }.ConnectionString;
            await using var runtime = NpgsqlDataSource.Create(connectionString);
            WebApplicationFactory<ProjectsProgram> Factory(bool enabled) =>
                new WebApplicationFactory<ProjectsProgram>().WithWebHostBuilder(builder =>
                {
                    foreach (var (key, value) in new Dictionary<string, string>
                        {
                            ["ConnectionStrings:ProjectsConfig"] = "Host=localhost;Username=fixture",
                            ["ProjectsConfig:Knowledge:BaseAddress"] = "https://knowledge.test",
                            ["ProjectsConfig:Authentication:Authority"] = TestIssuer,
                            ["ProjectsConfig:Authentication:Audience"] = "projects",
                            ["ProjectsConfig:Database:WorkloadIdentity:TenantId"] = Guid.NewGuid().ToString("D"),
                            ["ProjectsConfig:Database:WorkloadIdentity:ClientId"] = Guid.NewGuid().ToString("D"),
                            ["ProjectsConfig:Database:WorkloadIdentity:TokenFilePath"] =
                                Path.Combine(AppContext.BaseDirectory, "not-read-token"),
                            ["ProjectsConfig:ProviderReferenceReservationsEnabled"] = enabled.ToString(),
                        })
                        builder.UseSetting(key, value);
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<NpgsqlDataSource>();
                        services.AddSingleton(runtime);
                        services.AddAuthentication(options =>
                            {
                                options.DefaultAuthenticateScheme = ProjectSummaryTestAuthenticationHandler.SchemeName;
                                options.DefaultChallengeScheme = ProjectSummaryTestAuthenticationHandler.SchemeName;
                            })
                            .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                                ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    });
                });
            HttpClient Client(WebApplicationFactory<ProjectsProgram> factory, string actor, string tenantId, string? projectId = null)
            {
                var client = factory.CreateClient();
                client.DefaultRequestHeaders.Add("X-Test-Subject", actor);
                client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
                client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
                if (projectId is not null)
                    client.DefaultRequestHeaders.Add("X-Test-Bound-Project", projectId);
                return client;
            }

            var replayOperation = Guid.NewGuid();
            var replayId = "byok-" + replayOperation.ToString("N");
            var replayBody = SaveDraftBody(replayOperation, 0, "Existing")[..^1] +
                ",\"reference\":\"restart-" + suffix + "\",\"targetInstallationId\":\"" + isolated.InstallationId + "\"}";
            await test.ReferenceService(seed).SaveAsync(platform, ModelProviderBindingScope.Platform, null, replayId,
                new SaveModelProviderDraftRequest(replayOperation, 0, "Existing", "openai", "https://api.openai.com/v1", "test-model")
                {
                    Reference = "restart-" + suffix,
                    TargetInstallationId = isolated.InstallationId,
                }, CancellationToken.None);
            using (var before = Factory(false))
            using (var client = Client(before, platform.ActorId, platform.TenantId))
            using (var response = await client.GetAsync("/health/ready"))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var owner = await isolated.DataSource.OpenConnectionAsync();
            async Task ExecuteAsync(string sql)
            {
                await using var command = new NpgsqlCommand(sql, owner);
                await command.ExecuteNonQueryAsync();
            }
            await ExecuteAsync("REVOKE SELECT ON projects_config.installation_identity FROM \"agentweaver-id-projects-config\"");
            using (var after = Factory(false))
            using (var platformClient = Client(after, platform.ActorId, platform.TenantId))
            using (var projectClient = Client(after, subject, tenant, project.ProjectId))
            {
                async Task CheckOrdinaryAsync(HttpClient client, string path)
                {
                    var operation = Guid.NewGuid();
                    var id = "byok-" + operation.ToString("N");
                    var body = SaveDraftBody(operation, 0, "Ordinary");
                    foreach (var expected in new[] { 1, 1 })
                    {
                        using var request = SaveRequest(path, body);
                        using var response = await client.SendAsync(request);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        Assert.Equal(id, json.RootElement.GetProperty("id").GetString());
                        Assert.Equal(expected, json.RootElement.GetProperty("revision").GetInt32());
                    }
                    var update = SaveDraftBody(Guid.NewGuid(), 1, "Updated");
                    foreach (var expected in new[] { 2, 2 })
                    {
                        using var request = SaveRequest(path + "/" + id, update, HttpMethod.Put);
                        using var response = await client.SendAsync(request);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        Assert.Equal(expected, json.RootElement.GetProperty("revision").GetInt32());
                    }
                    Assert.Equal(2, await seed.ModelProviderDraftRevisions.CountAsync(row => row.ProviderId == id));
                }
                await CheckOrdinaryAsync(platformClient, "/api/platform/byok-providers");
                await CheckOrdinaryAsync(projectClient, "/api/projects/" + project.ProjectId + "/byok-providers");
                var createOperation = Guid.NewGuid();
                var createBody = SaveDraftBody(createOperation, 0, "New")[..^1] +
                    ",\"reference\":\"new-" + suffix + "\",\"targetInstallationId\":\"" + isolated.InstallationId + "\"}";
                foreach (var body in new[] { replayBody, createBody })
                {
                    using var request = SaveRequest("/api/platform/byok-providers", body);
                    using var response = await platformClient.SendAsync(request);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                    var text = await response.Content.ReadAsStringAsync();
                    Assert.Contains("model_provider_draft_unavailable", text);
                    Assert.DoesNotContain("permission", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("installation_identity", text);
                }
                Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(row => row.OperationId == replayOperation));
                Assert.Equal(1, await seed.ModelProviderDraftRevisions.CountAsync(row => row.ProviderId == replayId));
                Assert.False(await seed.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == createOperation));
                Assert.False(await seed.ModelProviderReferenceReservations.AnyAsync(row => row.OperationId == createOperation));
            }
            foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
            {
                await ExecuteAsync($"GRANT {privilege} ON projects_config.installation_identity TO \"agentweaver-id-projects-config\"");
                using var bad = Factory(false);
                var error = Assert.Throws<InvalidOperationException>(() => bad.CreateClient());
                Assert.Contains("runtime database principal", error.Message);
                await ExecuteAsync($"REVOKE {privilege} ON projects_config.installation_identity FROM \"agentweaver-id-projects-config\"");
            }
            await ExecuteAsync("ALTER TABLE projects_config.installation_identity OWNER TO \"agentweaver-id-projects-config\"");
            using (var bad = Factory(false))
            {
                var error = Assert.Throws<InvalidOperationException>(() => bad.CreateClient());
                Assert.Contains("runtime database principal", error.Message);
            }
            await ExecuteAsync("ALTER TABLE projects_config.installation_identity OWNER TO \"agentweaver-id-projects-config-migration\"");
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}
