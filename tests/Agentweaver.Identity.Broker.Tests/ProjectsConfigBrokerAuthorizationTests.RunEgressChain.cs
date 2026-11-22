extern alias EnvironmentService;
extern alias GatewayHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using EnvironmentService::Agentweaver.Environment;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task SameSignedBearerValidatesThroughGatewayOrchestratorAndEnvironmentAndEnforcesBetweenHopRevocation()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        var issuerUri = new Uri(IdentityBrokerWebApplicationFactory.Issuer);
        const string audience = "https://api.test";

        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var adminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "run-egress-chain-admin", null, null, ["platform_admin"]);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, adminToken, TenantId, "Run egress chain");

        var viewerToken = await IssueTokenAsync("api.read", [TenantId], "run-egress-chain-viewer");
        var viewerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(viewerToken).Claims, "sub");
        var viewerMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, viewerSubject, TenantId);
        var viewerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            viewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);

        var runId = "run-chain-" + Guid.NewGuid().ToString("N");
        var orchestratorSchema = "orch_egress_" + Guid.NewGuid().ToString("N");

        await using var envDataSource = NpgsqlDataSource.Create(_connectionString);
        var envDbOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(envDataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", EnvironmentDbContext.Schema))
            .Options;
        await EnvironmentMigrator.MigrateAsync(envDataSource, envDbOptions);
        var auditStore = new EnvironmentEgressAuditStore(envDataSource);
        var fp = await auditStore.RegisterOwnerAsync(
            new EnvironmentOwnerIdentity(TenantId, project.ProjectId, runId, "env-chain-" + Guid.NewGuid().ToString("N")),
            CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await auditStore.AppendFlowAsync(
            new("chain.example", null, 443, EgressFlowVerdict.Allowed, now, now, 3, fp),
            CancellationToken.None);

        await using var orchDataSource = NpgsqlDataSource.Create(_connectionString);
        await CoordinationOwnerMigrator.MigrateAsync(orchDataSource, orchestratorSchema);
        await using (var conn = await orchDataSource.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO "{orchestratorSchema}".accepted_runs
                (tenant_id, project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject)
            VALUES
                (@tenant, @project, @run, @selection::jsonb, @hash, @issuer, @subject)
            """, conn))
        {
            cmd.Parameters.AddWithValue("tenant", TenantId);
            cmd.Parameters.AddWithValue("project", project.ProjectId);
            cmd.Parameters.AddWithValue("run", runId);
            cmd.Parameters.AddWithValue("selection", "{}");
            cmd.Parameters.AddWithValue("hash", new string('b', 64));
            cmd.Parameters.AddWithValue("issuer", issuerUri.AbsoluteUri);
            cmd.Parameters.AddWithValue("subject", viewerSubject);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            // Real Environment host with actual JwtBearer validator.
            var envBuilder = WebApplication.CreateBuilder();
            envBuilder.WebHost.UseTestServer();
            envBuilder.Services.AddSingleton<IEnvironmentEgressAuditStore>(auditStore);
            envBuilder.Services.AddHttpClient<IProjectsConfigClient, EnvironmentService::Agentweaver.Environment.ProjectsConfigHttpClient>(
                    c => c.BaseAddress = projects.Client.BaseAddress)
                .ConfigurePrimaryHttpMessageHandler(() => projects.CreateHandler());
            ConfigureJwtBearer(envBuilder.Services, issuerUri.AbsoluteUri, audience, signingKey);
            envBuilder.Services.AddAuthorization();
            envBuilder.Services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            await using var envApp = envBuilder.Build();
            EnvironmentRunEgressEndpoints.UseRunEgressNoStore(envApp);
            envApp.UseAuthentication();
            envApp.UseAuthorization();
            EnvironmentRunEgressEndpoints.MapRunEgressEndpoints(envApp);
            await envApp.StartAsync();
            var envTestServer = envApp.GetTestServer();

            Func<Task>? beforeEnvironmentCall = null;

            // Real Orchestrator host with actual JwtBearer validator.
            var orchBuilder = WebApplication.CreateBuilder();
            orchBuilder.WebHost.UseTestServer();
            var orchOptions = new OrchestratorOptions(
                issuerUri.AbsoluteUri, audience, projects.Client.BaseAddress!.AbsoluteUri, audience,
                "https://events.test/", audience, orchestratorSchema);
            orchBuilder.Services.AddSingleton(orchOptions);
            orchBuilder.Services.AddSingleton(TimeProvider.System);
            orchBuilder.Services.AddSingleton(new ProjectRunReadStore(orchDataSource, orchestratorSchema));
            orchBuilder.Services.AddSingleton(new CoordinationOwnerStore(orchDataSource, orchestratorSchema));
            orchBuilder.Services.AddSingleton<IReviewedRemoteToolSnapshotResolver>(
                new ReviewedRemoteToolSnapshotStore(orchDataSource, orchestratorSchema));
            orchBuilder.Services.AddHttpClient<ProjectsRunSelectionClient>()
                .ConfigurePrimaryHttpMessageHandler(() => projects.CreateHandler());
            orchBuilder.Services.AddSingleton(new RuntimeRegistrationOwnerOptions(new Uri("https://environment.test/")));
            orchBuilder.Services.AddHttpClient("run-egress-chain-env")
                .ConfigurePrimaryHttpMessageHandler(() => new BeforeCallHandler(
                    envTestServer.CreateHandler(),
                    () => beforeEnvironmentCall is null ? Task.CompletedTask : beforeEnvironmentCall()));
            orchBuilder.Services.AddScoped(sp => new RunEgressEnvironmentClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("run-egress-chain-env"),
                sp.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress));
            ConfigureJwtBearer(orchBuilder.Services, issuerUri.AbsoluteUri, audience, signingKey);
            orchBuilder.Services.AddAuthorization();
            orchBuilder.Services.ConfigureHttpJsonOptions(json =>
                json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            await using var orchApp = orchBuilder.Build();
            ProjectRunEndpoints.UseRunEgressNoStore(orchApp);
            orchApp.UseAuthentication();
            orchApp.UseAuthorization();
            orchApp.MapProjectRunEndpoints();
            await orchApp.StartAsync();
            var orchTestClient = orchApp.GetTestClient();

            await using var gateway = await GatewayResourceServer.StartAsync(
                signingKey,
                () => projects.CreateHandler(),
                (owner, request, ct) =>
                {
                    Assert.Equal(GatewayOwner.Orchestrator, owner);
                    return orchTestClient.SendAsync(CloneRequest(request), ct);
                });

            // 1. Happy path: same signed bearer validates at Gateway, Orchestrator, and Environment.
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/egress"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
                req.Headers.Add("X-Agentweaver-Tenant", TenantId);
                using var resp = await gateway.Client.SendAsync(req);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
                var body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("chain.example", body, StringComparison.Ordinal);
            }

            // 2. Unauthenticated requests receive Cache-Control: no-store + 401 at every hop.
            foreach (var (client, path) in new[]
            {
                (gateway.Client, $"/api/v1/runs/{runId}/egress"),
                (orchTestClient, $"/api/runs/{runId}/egress"),
                (envApp.GetTestClient(), $"/api/projects/{project.ProjectId}/runs/{runId}/egress")
            })
            {
                using var resp = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }

            // 3. Tenant-bound platform-admin token (with tenant_id claim) is denied (403) when relying on platform-admin fallback,
            //    while an unbound platform-admin token succeeds (200).
            var platformOnlySubject = Guid.NewGuid().ToString("D");
            var platformOnlyMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, platformOnlySubject, TenantId);
            await AssignRoleAsync(
                projects.PrivilegedFixtureDataSource,
                platformOnlyMembership.MembershipId,
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin);
            var tenantBoundAdminToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuerUri.AbsoluteUri,
                audience,
                [
                    new("sub", platformOnlySubject),
                    new("scope", "api.read projects.admin"),
                    new("tenant_id", TenantId)
                ],
                DateTime.UtcNow.AddMinutes(-1),
                DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/egress"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantBoundAdminToken);
                req.Headers.Add("X-Agentweaver-Tenant", TenantId);
                using var resp = await gateway.Client.SendAsync(req);
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }

            var unboundAdminToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuerUri.AbsoluteUri,
                audience,
                [
                    new("sub", platformOnlySubject),
                    new("scope", "api.read projects.admin")
                ],
                DateTime.UtcNow.AddMinutes(-1),
                DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/egress"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unboundAdminToken);
                req.Headers.Add("X-Agentweaver-Tenant", TenantId);
                using var resp = await gateway.Client.SendAsync(req);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }

            // 4. Current Projects authority revocation between Orchestrator and Environment hops -> 403 at Environment.
            beforeEnvironmentCall = async () =>
            {
                beforeEnvironmentCall = null;
                await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, viewerAssignment.AssignmentId, 1);
            };

            using (var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{runId}/egress"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
                req.Headers.Add("X-Agentweaver-Tenant", TenantId);
                using var resp = await gateway.Client.SendAsync(req);
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }
        }
        finally
        {
            await using var conn = await orchDataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{orchestratorSchema}\" CASCADE", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static void ConfigureJwtBearer(
        IServiceCollection services,
        string issuer,
        string audience,
        SecurityKey signingKey)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri!.PathAndQuery);
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class BeforeCallHandler(HttpMessageHandler inner, Func<Task> onSend) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await onSend().ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
