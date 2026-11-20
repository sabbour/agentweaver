extern alias ProjectsConfig;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class GatewayModelProviderDraftPostgresTests
{
    [Fact]
    public async Task GatewayTargetContextProductionOwnerRejectsInvalidRequestShapes()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, new RsaSecurityKey(rsa), grantRoleAssignmentWrites: false);
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings__ProjectsConfig"] = "Host=localhost;Username=fixture",
            ["ProjectsConfig__Knowledge__BaseAddress"] = "https://knowledge.test",
            ["ProjectsConfig__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["ProjectsConfig__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["ProjectsConfig__Database__WorkloadIdentity__TokenFilePath"] = Path.GetFullPath("fixture-token"),
            ["ProjectsConfig__Authentication__Authority"] = IdentityBrokerWebApplicationFactory.Issuer,
            ["ProjectsConfig__Authentication__Audience"] = "https://api.test",
        };
        var previous = settings.ToDictionary(item => item.Key, item => System.Environment.GetEnvironmentVariable(item.Key));
        foreach (var (name, value) in settings) System.Environment.SetEnvironmentVariable(name, value);
        try
        {
            using var factory = new WebApplicationFactory<ProjectsConfig::Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton(projects.RuntimeDataSource);
                });
            });
            using var client = factory.CreateClient();
            foreach (var (method, path, body) in new[]
            {
                (HttpMethod.Post, "/api/authorization/target-context", (string?)null),
                (HttpMethod.Options, "/api/authorization/target-context/", null),
                (HttpMethod.Get, "/api/authorization/target-context?unexpected=1", null),
                (HttpMethod.Get, "/api/authorization/target-context", "{}"),
            })
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null) request.Content = new StringContent(body);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.Equal("""{"type":"about:blank","title":"request_shape_invalid","status":400,"code":"request_shape_invalid"}""",
                    await response.Content.ReadAsStringAsync());
            }
            using var missing = await client.GetAsync("/api/authorization/target-context");
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.True(missing.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", missing.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("Bearer", missing.Headers.WwwAuthenticate.Single().Scheme);
            Assert.Equal("""{"type":"about:blank","title":"authentication_required","status":401,"code":"authentication_required"}""",
                await missing.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (var (name, value) in previous) System.Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task GatewayTargetContextEnforcesTenantTokenAndSingletonContracts()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        using var logs = new DraftCapturedLogs();
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, key, grantRoleAssignmentWrites: false, loggerProvider: logs);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, projects.CreateHandler, (_, _, _) => throw new InvalidOperationException());
        using var owner = new HttpMessageInvoker(projects.CreateHandler());
        await using var productionGateway = GatewayProductionResourceServer.Start(
            key, (request, cancellationToken) => owner.SendAsync(request, cancellationToken),
            identityAudience: "https://api.test");
        var tenant = "matrix-tenant-" + Guid.NewGuid().ToString("N");
        var other = "matrix-other-" + Guid.NewGuid().ToString("N");
        var actor = "matrix-actor-" + Guid.NewGuid().ToString("N");
        var member = "matrix-member-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var membership = await authority.GrantMembershipAsync(issuer, actor, tenant, "fixture");
        await authority.AssignRoleAsync(membership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        await authority.GrantMembershipAsync(issuer, member, tenant, "fixture");
        var bearer = CreateToken(key, actor, "api.read");
        async Task Check(HttpClient client, string path, string? token, string[]? tenants, int status, string? code,
            string? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (tenants is not null)
                request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenants);
            if (body is not null) request.Content = new StringContent(body);
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (code is null)
            {
                Assert.Equal(tenant, json.RootElement.GetProperty("tenantId").GetString());
                Assert.Empty(json.RootElement.GetProperty("effectiveAuthority").EnumerateArray());
                Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("installationId").GetGuid());
            }
            else
            {
                Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
                Assert.False(json.RootElement.TryGetProperty("installationId", out _));
                Assert.Equal(4, json.RootElement.EnumerateObject().Count());
            }
        }
        const string ownerPath = "/api/authorization/target-context";
        const string path = "/api/v1/authorization/target-context";
        foreach (var (client, route) in new[] { (projects.Client, ownerPath), (gateway.Client, path),
                     (productionGateway.Client, path) })
        {
            await Check(client, route, bearer, null, 200, null);
            await Check(client, route, bearer, [tenant], 200, null);
            await Check(client, route, null, null, 401, "authentication_required");
            await Check(client, route, CreateToken(key, member, "api.read"), [tenant], 403, "target_context_forbidden");
            await Check(client, route, CreateToken(key, actor, "projects.admin"), [tenant], 403, "target_context_forbidden");
            await Check(client, route, CreateToken(key, actor, "api.read", projectId: "p"), [tenant], 403, "target_context_forbidden");
            await Check(client, route, CreateToken(key, actor, "api.read", projectId: "p", runId: "r"), [tenant], 403, "target_context_forbidden");
            await Check(client, route, CreateToken(key, actor, "api.read", purpose: "ticket"), [tenant], 403, "target_context_forbidden");
            await Check(client, route, CreateToken(key, actor, "api.read", tenantAssertion: other), [tenant], 403, "target_context_forbidden");
            await Check(client, route, bearer, [other], 403, "target_context_forbidden");
            await Check(client, route, bearer, [tenant, tenant], 403, "target_context_forbidden");
            await Check(client, route + "?unexpected=1", bearer, [tenant], 400, "request_shape_invalid");
            await Check(client, route, bearer, [tenant], 400, "request_shape_invalid", "{}");
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options })
            {
                using var request = new HttpRequestMessage(method, route);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.Equal("""{"type":"about:blank","title":"request_shape_invalid","status":400,"code":"request_shape_invalid"}""",
                    await response.Content.ReadAsStringAsync());
            }
        }
        var canary = Guid.NewGuid().ToString("D");
        await Check(projects.Client, ownerPath + "/?leak=" + canary, bearer, [tenant], 400, "request_shape_invalid");
        Assert.DoesNotContain(canary, logs.Text);
        Assert.DoesNotContain(bearer, logs.Text);
        await authority.GrantMembershipAsync(issuer, actor, other, "fixture");
        await Check(gateway.Client, path, bearer, null, 403, "target_context_forbidden");
        await Check(gateway.Client, path, bearer, [tenant], 200, null);
        Guid installation;
        await using (var connection = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
        {
            await using var read = new NpgsqlCommand("SELECT installation_id FROM projects_config.installation_identity", connection);
            installation = (Guid)(await read.ExecuteScalarAsync())!;
            await using var delete = new NpgsqlCommand("DELETE FROM projects_config.installation_identity", connection);
            await delete.ExecuteNonQueryAsync();
        }
        await Check(projects.Client, ownerPath, bearer, [tenant], 503, "target_context_unavailable");
        await Check(gateway.Client, path, bearer, [tenant], 503, "target_context_unavailable");
        var runtimeRole = new NpgsqlConnectionStringBuilder(projects.RuntimeDataSource.ConnectionString).Username!;
        await using (var connection = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
        {
            await using var restore = new NpgsqlCommand(
                "INSERT INTO projects_config.installation_identity (singleton, installation_id) VALUES (true, @id)", connection);
            restore.Parameters.AddWithValue("id", installation);
            await restore.ExecuteNonQueryAsync();
            using var identifier = new NpgsqlCommandBuilder();
            await using var revoke = new NpgsqlCommand(
                $"REVOKE SELECT ON projects_config.installation_identity FROM {identifier.QuoteIdentifier(runtimeRole)}", connection);
            await revoke.ExecuteNonQueryAsync();
        }
        await Check(projects.Client, ownerPath, bearer, [tenant], 503, "target_context_unavailable");
        await Check(gateway.Client, path, bearer, [tenant], 503, "target_context_unavailable");
        var admin = CreateToken(key, actor, "api.read projects.admin");
        using var draft = OwnerRequest(HttpMethod.Post, "/api/v1/platform/byok-providers", admin, tenant,
            SaveBody(Guid.NewGuid(), 0, "Ordinary draft"));
        using var created = await gateway.Client.SendAsync(draft);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GatewayTargetContextSerializesCurrentAuthorityRevocation(bool membershipRevocation, bool afterLock)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, key, grantRoleAssignmentWrites: false);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, projects.CreateHandler, (_, _, _) => throw new InvalidOperationException());
        var tenant = "lock-tenant-" + Guid.NewGuid().ToString("N");
        var subject = "lock-actor-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var membership = await authority.GrantMembershipAsync(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri, subject, tenant, "fixture");
        var platform = await authority.AssignRoleAsync(membership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var token = CreateToken(key, subject, "api.read");
        async Task<HttpResponseMessage> Read() => await gateway.Client.SendAsync(OwnerRequest(
            HttpMethod.Get, "/api/v1/authorization/target-context", token, tenant));
        Task Revoke() => membershipRevocation
            ? authority.RevokeMembershipAsync(membership.MembershipId, membership.Revision, "fixture")
            : authority.RevokeRoleAssignmentAsync(platform.AssignmentId, platform.Revision, "fixture");
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task Pause(int pid, CancellationToken ct)
        {
            ready.TrySetResult(pid);
            await release.Task.WaitAsync(ct);
        }
        if (afterLock) AuthorizationTargetContext.AfterAuthorityLock = Pause;
        else AuthorizationTargetContext.BeforeAuthorityLock = Pause;
        var pending = Read();
        Task? revoke = null;
        try
        {
            var pid = await ready.Task.WaitAsync(timeout.Token);
            revoke = Revoke();
            if (afterLock)
            {
                await using var observer = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync(timeout.Token);
                while (true)
                {
                    await using var query = new NpgsqlCommand(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))",
                        observer);
                    query.Parameters.AddWithValue("pid", pid);
                    if ((bool)(await query.ExecuteScalarAsync(timeout.Token) ?? false)) break;
                    Assert.False(revoke.IsCompleted);
                    await Task.Delay(20, timeout.Token);
                }
            }
            else
                await revoke.WaitAsync(timeout.Token);
            release.TrySetResult();
            using var response = await pending.WaitAsync(timeout.Token);
            Assert.Equal(afterLock ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (afterLock)
                Assert.NotEqual(Guid.Empty, body.RootElement.GetProperty("installationId").GetGuid());
            else
            {
                Assert.Equal("target_context_forbidden", body.RootElement.GetProperty("code").GetString());
                Assert.False(body.RootElement.TryGetProperty("installationId", out _));
            }
            await revoke.WaitAsync(timeout.Token);
        }
        finally
        {
            AuthorizationTargetContext.BeforeAuthorityLock = null;
            AuthorizationTargetContext.AfterAuthorityLock = null;
            release.TrySetResult();
            try { using var ignored = await pending; } catch { }
            if (revoke is not null) try { await revoke; } catch { }
        }
        using var denied = await Read();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task GatewayTargetContextReadsFreshGrantsAndEmittedSchema()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, key, grantRoleAssignmentWrites: false);
        await using var setupProjects = await ProjectsConfigResourceServer.StartAsync(connectionString, key);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, projects.CreateHandler, (_, _, _) => throw new InvalidOperationException());
        var tenant = "target-tenant-" + Guid.NewGuid().ToString("N");
        var subject = "target-actor-" + Guid.NewGuid().ToString("N");
        var creator = "target-creator-" + Guid.NewGuid().ToString("N");
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var membership = await authority.GrantMembershipAsync(issuer, subject, tenant, "fixture");
        var platform = await authority.AssignRoleAsync(membership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin, "fixture");
        var creatorMembership = await authority.GrantMembershipAsync(issuer, creator, tenant, "fixture");
        await authority.AssignRoleAsync(creatorMembership.MembershipId, ProjectAuthorityResourceType.Tenant,
            tenant, ProjectAuthorityRole.TenantAdmin, "fixture");
        var creatorToken = CreateToken(key, creator, "api.read projects.admin");
        async Task<string> CreateProject()
        {
            using var request = OwnerRequest(HttpMethod.Post, "/api/projects/", creatorToken, tenant,
                """{"name":"Target context project"}""");
            using var response = await setupProjects.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("projectId").GetString()!;
        }
        var firstProject = await CreateProject();
        var secondProject = await CreateProject();
        var contributor = await authority.AssignRoleAsync(membership.MembershipId,
            ProjectAuthorityResourceType.Project, firstProject, ProjectAuthorityRole.Contributor, "fixture");
        var token = CreateToken(key, subject, "api.read projects.orchestrator");
        const string path = "/api/v1/authorization/target-context";
        async Task<HttpResponseMessage> Read() =>
            await gateway.Client.SendAsync(OwnerRequest(HttpMethod.Get, path, token, tenant));

        using (var response = await Read())
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = result.RootElement;
            Assert.Equal(subject, root.GetProperty("actorId").GetString());
            Assert.Equal(tenant, root.GetProperty("tenantId").GetString());
            Assert.True(Guid.TryParseExact(root.GetProperty("installationId").GetString(), "D", out var id));
            Assert.NotEqual(Guid.Empty, id);
            await using (var connection = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
            await using (var read = new NpgsqlCommand("SELECT installation_id FROM projects_config.installation_identity", connection))
                Assert.Equal(id, (Guid)(await read.ExecuteScalarAsync())!);
            var project = Assert.Single(root.GetProperty("effectiveAuthority").EnumerateArray(),
                item => item.GetProperty("resourceId").GetString() == firstProject);
            Assert.Contains(project.GetProperty("permissions").EnumerateArray(),
                item => item.GetProperty("permission").GetString() == "cancelRun");
            Assert.DoesNotContain(root.GetProperty("effectiveAuthority").EnumerateArray(),
                item => item.GetProperty("resourceType").GetString() == "platform");
            using var schemaResponse = await gateway.Client.GetAsync("/openapi/v1.json");
            using var openApi = JsonDocument.Parse(await schemaResponse.Content.ReadAsStringAsync());
            var operation = openApi.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");
            Assert.Equal("getAuthorizationTargetContext", operation.GetProperty("operationId").GetString());
            var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            var schema = schemas.GetProperty("AuthorizationTargetContext");
            Assert.True(GatewayBacklogOpenApiTests.MatchesSchema(root, schema, schemas));
            var oldRoute = openApi.RootElement.GetProperty("paths").GetProperty("/api/v1/authorization/context")
                .GetProperty("get");
            Assert.Equal("#/components/schemas/ProjectAuthorizationContext", oldRoute.GetProperty("responses")
                .GetProperty("200").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
            var oldContext = schemas.GetProperty("ProjectAuthorizationContext");
            Assert.Equal(new[] { "contractVersion", "issuer", "actorId", "tenantId", "membershipRevision",
                "boundProjectId", "boundRunId", "effectiveAuthority" },
                oldContext.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(new[] { "readProjects", "writeProjects", "createProjects", "readRunSelection",
                "acceptRunSelection", "accessPrivateKnowledge", "readPlatformRuntimeDefaults",
                "writePlatformRuntimeDefaults" }, schemas.GetProperty("ProjectAuthorizationPermissionGrant")
                .GetProperty("properties").GetProperty("permission").GetProperty("enum").EnumerateArray()
                .Select(item => item.GetString()));
            foreach (var name in new[] { "AuthorizationTargetContext", "TargetContextEffectiveAuthority",
                         "TargetContextPermission", "AuthorizationTargetContextError" })
                Assert.False(schemas.GetProperty(name).GetProperty("additionalProperties").GetBoolean());
            var mutated = JsonNode.Parse(schemas.GetRawText())!;
            var permissions = mutated["TargetContextPermission"]!["properties"]!["permission"]!["enum"]!.AsArray();
            var cancel = permissions.Single(item => item!.GetValue<string>() == "cancelRun");
            permissions.Remove(cancel);
            using var invalidSchemas = JsonDocument.Parse(mutated.ToJsonString());
            Assert.False(GatewayBacklogOpenApiTests.MatchesSchema(root, schema, invalidSchemas.RootElement));
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        AuthorizationTargetContext.BeforeAuthorityLock = async (_, ct) =>
        {
            ready.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var pending = Read();
        try
        {
            await ready.Task.WaitAsync(timeout.Token);
            await authority.RevokeRoleAssignmentAsync(contributor.AssignmentId, contributor.Revision, "fixture");
            var added = await authority.AssignRoleAsync(membership.MembershipId,
                ProjectAuthorityResourceType.Project, secondProject, ProjectAuthorityRole.Contributor, "fixture");
            release.TrySetResult();
            using var response = await pending.WaitAsync(timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var grants = result.RootElement.GetProperty("effectiveAuthority").EnumerateArray().ToArray();
            Assert.DoesNotContain(grants, item => item.GetProperty("resourceId").GetString() == firstProject);
            var current = Assert.Single(grants, item => item.GetProperty("resourceId").GetString() == secondProject);
            Assert.Contains(current.GetProperty("permissions").EnumerateArray(), item =>
                item.GetProperty("permission").GetString() == "cancelRun" &&
                item.GetProperty("roleRevision").GetInt64() == added.Revision);
        }
        finally
        {
            AuthorizationTargetContext.BeforeAuthorityLock = null;
            release.TrySetResult();
            try { using var ignored = await pending; } catch { }
        }

        await authority.RevokeRoleAssignmentAsync(platform.AssignmentId, platform.Revision, "fixture");
        using var denied = await Read();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore);
        Assert.Equal("target_context_forbidden", JsonDocument.Parse(await denied.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());
    }
}
