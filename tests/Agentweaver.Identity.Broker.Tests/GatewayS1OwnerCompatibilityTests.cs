extern alias OldProjectsConfig;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OldProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class GatewayModelProviderDraftPostgresTests
{
    [Fact]
    public async Task GatewayS1OwnerRejectsPinsAndPreservesReplayAndAccessChecks()
    {
        using (var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "S1-owner-provenance.json"))))
        {
            Assert.Equal("2d8606fa825e8e2d52f8d95e2f4066b148b78289",
                provenance.RootElement.GetProperty("sourceCommit").GetString());
            Assert.EndsWith("+" + provenance.RootElement.GetProperty("sourceCommit").GetString(),
                typeof(OldProjectsConfig::Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            Assert.Equal(provenance.RootElement.GetProperty("assemblySha256").GetString(),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    typeof(OldProjectsConfig::Program).Assembly.Location))));
            Assert.Equal(provenance.RootElement.GetProperty("dependenciesSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Agentweaver.Projects.Config.S1.deps.json")))));
        }
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var admin = NpgsqlDataSource.Create(connectionString);
        await using (var connection = await admin.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'agentweaver-id-projects-config-migration') THEN
                CREATE ROLE "agentweaver-id-projects-config-migration" LOGIN;
              END IF;
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'agentweaver-id-projects-config') THEN
                CREATE ROLE "agentweaver-id-projects-config" NOLOGIN;
              END IF;
            END $$;
            CREATE SCHEMA IF NOT EXISTS projects_config AUTHORIZATION "agentweaver-id-projects-config-migration";
            """, connection))
            await command.ExecuteNonQueryAsync();
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(admin, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        var migrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Options = "-c role=agentweaver-id-projects-config-migration",
        }.ConnectionString;
        await using (var migration = NpgsqlDataSource.Create(migrationConnection))
        {
            var migrationOptions = new DbContextOptionsBuilder<OldProjectsConfig::Agentweaver.Projects.Config.ProjectsConfigDbContext>()
                .UseNpgsql(migration, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", OldProjectsConfig::Agentweaver.Projects.Config.ProjectsConfigDbContext.Schema)).Options;
            await OldProjectsConfig::Agentweaver.Projects.Config.ProjectsConfigMigrator.MigrateAsync(migration, migrationOptions);
        }
        var role = "s1_runtime_" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var connection = await admin.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}' IN ROLE \"agentweaver-id-projects-config\"", connection))
            await command.ExecuteNonQueryAsync();
        var runtimeConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role, Password = password,
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(runtimeConnection);
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var actor = "s1-" + Guid.NewGuid().ToString("N");
        var tenant = "s1-tenant";
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var member = await authority.GrantMembershipAsync(issuer, actor, tenant, "fixture");
        var assignment = await authority.AssignRoleAsync(member.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin, "fixture");
        var otherActor = "s1-other-" + Guid.NewGuid().ToString("N");
        var otherMember = await authority.GrantMembershipAsync(issuer, otherActor, tenant, "fixture");
        await authority.AssignRoleAsync(otherMember.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin, "fixture");
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings__ProjectsConfig"] = "Host=localhost;Username=fixture",
            ["ProjectsConfig__Knowledge__BaseAddress"] = "https://knowledge.test",
            ["ProjectsConfig__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["ProjectsConfig__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["ProjectsConfig__Database__WorkloadIdentity__TokenFilePath"] = Path.GetFullPath("fixture-token"),
            ["ProjectsConfig__Authentication__Authority"] = IdentityBrokerWebApplicationFactory.Issuer,
            ["ProjectsConfig__Authentication__Audience"] = "https://api.test",
            ["ProjectsConfig__ProviderReferenceReservationsEnabled"] = "false",
            ["AgentHost__ModelBindingsRevision"] = "s1-fixture",
            ["AgentHost__ModelBindings__fixture-binding__SourceMode"] = "Byok",
            ["AgentHost__ModelBindings__fixture-binding__Enabled"] = "false",
        };
        var previous = settings.ToDictionary(item => item.Key, item => System.Environment.GetEnvironmentVariable(item.Key));
        foreach (var (name, value) in settings) System.Environment.SetEnvironmentVariable(name, value);
        try
        {
            using var factory = new WebApplicationFactory<OldProjectsConfig::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton(runtime);
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                    {
                        jwt.Authority = null;
                        jwt.MetadataAddress = string.Empty;
                        jwt.ConfigurationManager = null;
                        jwt.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true, ValidIssuer = issuer,
                            ValidateAudience = true, ValidAudience = "https://api.test",
                            ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                            ValidateLifetime = true, RequireSignedTokens = true,
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        };
                    });
                });
            });
            using var ownerClient = factory.CreateClient();
            using var owner = new HttpMessageInvoker(factory.Server.CreateHandler());
            await using var gateway = GatewayProductionResourceServer.Start(key,
                (request, cancellationToken) => owner.SendAsync(request, cancellationToken),
                identityAudience: "https://api.test");
            var token = CreateToken(key, actor, "api.read projects.admin");
            var operation = Guid.NewGuid();
            var unpinned = SaveBody(operation, 0, "S1 model")[..^1] + ",\"reference\":\"stable\"}";
            var pinned = unpinned[..^1] + ",\"targetInstallationId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
            async Task<(HttpStatusCode Status, string Body)> Send(string body, string? bearer = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/byok-providers")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? token);
                request.Headers.Add("X-Agentweaver-Tenant", tenant);
                using var response = await gateway.Client.SendAsync(request);
                Assert.True(response.Headers.CacheControl?.NoStore);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            Assert.Equal((HttpStatusCode.BadRequest, """{"code":"model_provider_draft_invalid_request"}"""),
                await Send(pinned));
            Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"code":"model_provider_draft_unavailable"}"""),
                await Send(unpinned));
            factory.Server.Services.GetRequiredService<IConfiguration>()["ProjectsConfig:ProviderReferenceReservationsEnabled"] = "true";
            var created = await Send(unpinned);
            Assert.Equal(HttpStatusCode.OK, created.Status);
            using (var body = JsonDocument.Parse(created.Body))
            {
                Assert.Equal("byok-" + operation.ToString("N"), body.RootElement.GetProperty("id").GetString());
                Assert.Equal("stable", body.RootElement.GetProperty("reference").GetString());
                Assert.Equal(1, body.RootElement.GetProperty("revision").GetInt64());
            }
            factory.Server.Services.GetRequiredService<IConfiguration>()["ProjectsConfig:ProviderReferenceReservationsEnabled"] = "false";
            var replay = await Send(unpinned);
            Assert.Equal(created, replay);
            Assert.Equal((HttpStatusCode.Conflict, """{"code":"model_provider_draft_conflict"}"""),
                await Send(unpinned, CreateToken(key, otherActor, "api.read projects.admin")));
            Assert.Equal((HttpStatusCode.Forbidden, """{"code":"forbidden"}"""),
                await Send(unpinned, CreateToken(key, actor, "api.read")));
            var blocked = SaveBody(Guid.NewGuid(), 0, "Another")[..^1] + ",\"reference\":\"other\"}";
            Assert.Equal((HttpStatusCode.ServiceUnavailable, """{"code":"model_provider_draft_unavailable"}"""),
                await Send(blocked));
            await authority.RevokeRoleAssignmentAsync(assignment.AssignmentId, assignment.Revision, "fixture");
            Assert.Equal((HttpStatusCode.Forbidden, """{"code":"forbidden"}"""), await Send(unpinned));
            await using var verify = await admin.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM projects_config.model_provider_reference_reservations", verify);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            foreach (var (name, value) in previous) System.Environment.SetEnvironmentVariable(name, value);
            await runtime.DisposeAsync();
            await using var connection = await admin.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
