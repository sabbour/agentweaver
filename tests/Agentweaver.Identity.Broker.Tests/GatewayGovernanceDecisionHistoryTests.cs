extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class GatewayModelProviderDraftPostgresTests
{
    [Fact]
    public async Task GatewayGovernanceDecisionHistoryEnforcesScopeRolesValidationAndPaging()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, key, grantRoleAssignmentWrites: false);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, projects.CreateHandler, (_, _, _) => throw new InvalidOperationException());

        var tenant = "gov-tenant-" + Guid.NewGuid().ToString("N");
        var otherTenant = "gov-other-" + Guid.NewGuid().ToString("N");
        var platformAdmin = "gov-plat-" + Guid.NewGuid().ToString("N");
        var viewer = "gov-viewer-" + Guid.NewGuid().ToString("N");
        var outsider = "gov-outsider-" + Guid.NewGuid().ToString("N");

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;

        var adminMembership = await authority.GrantMembershipAsync(issuer, platformAdmin, tenant, "fixture");
        var adminRole = await authority.AssignRoleAsync(
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin,
            "fixture");

        var viewerMembership = await authority.GrantMembershipAsync(issuer, viewer, tenant, "fixture");
        var outsiderMembership = await authority.GrantMembershipAsync(issuer, outsider, tenant, "fixture");
        var otherMembership = await authority.GrantMembershipAsync(issuer, viewer, otherTenant, "fixture");

        var projectId = Guid.NewGuid().ToString("N");
        var otherProjectId = Guid.NewGuid().ToString("N");
        await using (var db = new ProjectsConfigDbContext(options))
        {
            var now = DateTimeOffset.UtcNow;
            db.Projects.AddRange(
                new ProjectRecord
                {
                    ProjectId = projectId,
                    TenantId = tenant,
                    Name = "Gov Project",
                    State = ProjectLifecycleState.Active,
                    Revision = 1,
                    ConfigurationRevision = 1,
                    CreatedByActorId = platformAdmin,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new ProjectRecord
                {
                    ProjectId = otherProjectId,
                    TenantId = otherTenant,
                    Name = "Foreign Project",
                    State = ProjectLifecycleState.Active,
                    Revision = 1,
                    ConfigurationRevision = 1,
                    CreatedByActorId = platformAdmin,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var viewerRole = await authority.AssignRoleAsync(
            viewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Viewer,
            "fixture");
        await authority.AssignRoleAsync(
            otherMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            otherProjectId,
            ProjectAuthorityRole.Viewer,
            "fixture");

        var decisionStore = new ProjectGovernanceDecisionStore(
            new PooledDbContextFactory<ProjectsConfigDbContext>(options),
            TimeProvider.System);
        await decisionStore.AppendWithCurrentHeadAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            "policy",
            "canonical",
            "plat-1",
            SampleDecisionInput("platform-rule-1"),
            CancellationToken.None);
        await decisionStore.AppendWithCurrentHeadAsync(
            NativeAcsPolicyScopeType.Project,
            projectId,
            "skill",
            "skill-1",
            "proj-1",
            SampleDecisionInput("project-rule-1"),
            CancellationToken.None);
        await decisionStore.AppendWithCurrentHeadAsync(
            NativeAcsPolicyScopeType.Project,
            projectId,
            "skill",
            "skill-1",
            "proj-2",
            SampleDecisionInput("project-rule-2"),
            CancellationToken.None);

        var adminToken = CreateToken(key, platformAdmin, "api.read");
        var viewerToken = CreateToken(key, viewer, "api.read");
        var viewerProjectBound = CreateToken(key, viewer, "api.read", projectId: projectId);
        var viewerWrongProjectBound = CreateToken(key, viewer, "api.read", projectId: otherProjectId);
        var viewerRunBound = CreateToken(key, viewer, "api.read", projectId: projectId, runId: "run-1");
        var outsiderToken = CreateToken(key, outsider, "api.read");

        async Task<HttpResponseMessage> SendAsync(
            string path,
            string? token,
            string? tenantHeader = null,
            string? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (tenantHeader is not null)
                request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenantHeader);
            if (body is not null)
                request.Content = new StringContent(body);
            return await gateway.Client.SendAsync(request);
        }

        using (var platOk = await SendAsync(
            "/api/v1/policies/platform/decisions?resourceType=policy&resourceId=canonical",
            adminToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.OK, platOk.StatusCode);
            Assert.True(platOk.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await platOk.Content.ReadAsStringAsync());
            Assert.Equal("platform", json.RootElement.GetProperty("scopeType").GetString());
            Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        }

        using (var platDenied = await SendAsync(
            "/api/v1/policies/platform/decisions?resourceType=policy&resourceId=canonical",
            viewerToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, platDenied.StatusCode);
        }

        using (var projPage1 = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1&pageSize=1",
            viewerProjectBound,
            tenant))
        {
            Assert.Equal(HttpStatusCode.OK, projPage1.StatusCode);
            Assert.True(projPage1.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await projPage1.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal(2, items[0].GetProperty("sequence").GetInt64());
            Assert.Equal(2, json.RootElement.GetProperty("nextBeforeSequence").GetInt64());
        }

        using (var projPage2 = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1&pageSize=1&beforeSequence=2",
            viewerToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.OK, projPage2.StatusCode);
            using var json = JsonDocument.Parse(await projPage2.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal(1, items[0].GetProperty("sequence").GetInt64());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextBeforeSequence").ValueKind);
        }

        foreach (var badPath in new[]
        {
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1&unexpected=1",
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceType=other&resourceId=skill-1",
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill",
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1&beforeSequence=0",
        })
        {
            using var bad = await SendAsync(badPath, viewerToken, tenant);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        using (var unauthenticated = await SendAsync(
            "/api/v1/policies/platform/decisions?resourceType=policy&resourceId=canonical",
            token: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            Assert.True(unauthenticated.Headers.CacheControl?.NoStore);
        }

        using (var withBody = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerToken,
            tenant,
            body: "{}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, withBody.StatusCode);
            Assert.True(withBody.Headers.CacheControl?.NoStore);
        }

        using (var wrongBinding = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerWrongProjectBound,
            tenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongBinding.StatusCode);
        }

        using (var runBinding = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerRunBound,
            tenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, runBinding.StatusCode);
        }

        using (var outsiderResp = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            outsiderToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.NotFound, outsiderResp.StatusCode);
        }

        using (var crossTenant = await SendAsync(
            $"/api/v1/policies/projects/{otherProjectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        }

        await authority.RevokeRoleAssignmentAsync(viewerRole.AssignmentId, viewerRole.Revision, "revoked");
        using (var revokedViewer = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.NotFound, revokedViewer.StatusCode);
        }

        await authority.RevokeRoleAssignmentAsync(adminRole.AssignmentId, adminRole.Revision, "revoked");
        using (var revokedAdmin = await SendAsync(
            "/api/v1/policies/platform/decisions?resourceType=policy&resourceId=canonical",
            adminToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedAdmin.StatusCode);
            Assert.True(revokedAdmin.Headers.CacheControl?.NoStore);
        }

        await using (var conn = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync())
        await using (var revokeMembership = new Npgsql.NpgsqlCommand(
            "UPDATE projects_config.tenant_memberships SET state = 'Revoked', revision = revision + 1 WHERE membership_id = @id;",
            conn))
        {
            revokeMembership.Parameters.AddWithValue("id", viewerMembership.MembershipId);
            await revokeMembership.ExecuteNonQueryAsync();
        }
        using (var revokedTenant = await SendAsync(
            $"/api/v1/policies/projects/{projectId}/decisions?resourceType=skill&resourceId=skill-1",
            viewerToken,
            tenant))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedTenant.StatusCode);
            Assert.True(revokedTenant.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task GatewayGovernanceDecisionHistoryMapsTimeoutExceptionTo503NoStore()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(connectionString, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var realFactory = new PooledDbContextFactory<ProjectsConfigDbContext>(options);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString,
            key,
            grantRoleAssignmentWrites: false,
            configureServices: services =>
            {
                services.AddSingleton<IDbContextFactory<ProjectsConfigDbContext>>(new TimeoutDbContextFactory(realFactory));
            });
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, projects.CreateHandler, (_, _, _) => throw new InvalidOperationException());

        var tenant = "gov-timeout-" + Guid.NewGuid().ToString("N");
        var platformAdmin = "gov-timeout-admin-" + Guid.NewGuid().ToString("N");
        var privilegedOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config =>
                config.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(privilegedOptions, TimeProvider.System);
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var membership = await authority.GrantMembershipAsync(issuer, platformAdmin, tenant, "fixture");
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin,
            "fixture");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/policies/platform/decisions?resourceType=policy&resourceId=canonical");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key, platformAdmin, "api.read"));
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("governance_decision_source_unavailable", json.RootElement.GetProperty("code").GetString());
    }

    private sealed class TimeoutDbContextFactory(
        IDbContextFactory<ProjectsConfigDbContext> inner) : IDbContextFactory<ProjectsConfigDbContext>
    {
        public ProjectsConfigDbContext CreateDbContext() => inner.CreateDbContext();

        public Task<ProjectsConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Simulated governance store timeout.");
    }

    private static ProjectGovernanceDecisionInput SampleDecisionInput(string selectedRule) =>
        new(
            "policy",
            ProjectGovernanceOutcome.Allow,
            ProjectGovernanceApprovalState.NotRequired,
            PolicyRevision: 1,
            PolicyArtifactSha256: new string('a', 64),
            SelectedRule: selectedRule,
            InterventionPoint: "beforeTool",
            RequestId: "req-1",
            ActionId: "act-1",
            RunId: null,
            AgentId: null,
            RoleId: null,
            HumanEntraOid: null,
            TenantId: null,
            BrokerIssuer: "https://broker.example/",
            BrokerSubject: "actor-1",
            EffectiveRoleRevisions: [],
            OwnerReceiptId: null,
            OwnerReceiptPosition: null,
            CompilerGeneration: null,
            ApplicationGeneration: null,
            CorrelationId: "corr-" + Guid.NewGuid().ToString("N"),
            DecidedAt: new DateTimeOffset(2026, 11, 25, 19, 0, 0, TimeSpan.Zero));
}
