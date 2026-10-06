extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class ProjectsConfigBrokerAuthorizationTests(PostgresContainerFixture postgres)
    : IAsyncLifetime
{
    private static readonly string[] ProjectScopes =
    [
        "projects.bootstrap",
        "projects.admin",
        "projects.owner",
        "projects.orchestrator",
        "projects.foreign-tenant",
        "projects.no-membership",
        "projects.multiple-memberships",
        "projects.unassigned-admin",
        "projects.run-binding",
    ];

    private const string TenantId = "tenant-1";
    private const string OtherTenantId = "tenant-2";
    private const string TestIssuer = IdentityBrokerWebApplicationFactory.Issuer;
    private const string RunId = "run-authorized-1";

    private FakeIdentityProvider _fakeIdp = null!;
    private IdentityBrokerWebApplicationFactory _brokerFactory = null!;
    private HttpClient _fakeIdpClient = null!;
    private string _connectionString = string.Empty;
    private (string PfxPath, string Password) _signingCertificate;
    private int _subjectSequence;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync();
        _fakeIdp = await FakeIdentityProvider.StartAsync();
        _signingCertificate = TestSigningCertificate.Create();
        _brokerFactory = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            });
        _fakeIdpClient = new HttpClient(_fakeIdp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority),
        };
    }

    public async Task DisposeAsync()
    {
        _fakeIdpClient.Dispose();
        await _brokerFactory.DisposeAsync();
        await _fakeIdp.DisposeAsync();
        if (File.Exists(_signingCertificate.PfxPath))
            File.Delete(_signingCertificate.PfxPath);
        if (Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
    }

    [Fact]
    public async Task ProjectsApiUsesOnlyLiveSourceAuthorityAndIntersectsBrokerTokenConstraints()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        await AssertRuntimeCannotWriteAuthorityAsync(projects.RuntimeDataSource);

        var ownerToken = await IssueTokenAsync(
            "projects.admin", ["upstream-tenant"], "platform_admin");
        var ownerClaims = new JwtSecurityTokenHandler().ReadJwtToken(ownerToken).Claims.ToArray();
        var ownerSubject = SingleClaim(ownerClaims, "sub");
        Assert.DoesNotContain(ownerClaims,
            claim => claim.Type is "tenant_id" or "tid" or "role" or "roles" or ClaimTypes.Role);
        var ownerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, ownerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Tenant one project" }),
        };
        AddBearerAndTenant(createRequest, ownerToken, TenantId);
        using var created = await projects.Client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<ProjectSummary>();
        Assert.NotNull(project);

        var ownerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        using var ownerRead = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);

        var unassignedAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "platform_admin");
        var unassignedAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(unassignedAdminToken).Claims, "sub");
        await AddMembershipAsync(projects.PrivilegedFixtureDataSource, unassignedAdminSubject, TenantId);
        using var scopeCannotGrantRole = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", unassignedAdminToken, [TenantId],
            roleHeader: "platform_admin");
        Assert.Equal(HttpStatusCode.Forbidden, scopeCannotGrantRole.StatusCode);
        using var scopeCannotCreateProject = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Unassigned project" }),
        };
        AddBearerAndTenant(scopeCannotCreateProject, unassignedAdminToken, TenantId);
        using var rejectedCreate = await projects.Client.SendAsync(scopeCannotCreateProject);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedCreate.StatusCode);

        var forgedClaimsToken = CreateSignedAccessToken(
            unassignedAdminSubject,
            new Claim("tenant_id", TenantId),
            new Claim("role", "platform_admin"));
        using var forgedClaimsDenied = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", forgedClaimsToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, forgedClaimsDenied.StatusCode);

        var duplicateTenantClaimsToken = CreateSignedAccessToken(
            unassignedAdminSubject,
            new Claim("tenant_id", TenantId),
            new Claim("tenant_id", OtherTenantId));
        using var duplicateTenantClaimsDenied = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}",
            duplicateTenantClaimsToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, duplicateTenantClaimsDenied.StatusCode);

        var noMembershipToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "platform_admin");
        using var missingMembership = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", noMembershipToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, missingMembership.StatusCode);

        var otherTenantToken = await IssueTokenAsync(
            "projects.foreign-tenant", [OtherTenantId]);
        var otherTenantSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(otherTenantToken).Claims, "sub");
        var otherTenantMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, otherTenantSubject, OtherTenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            otherTenantMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            OtherTenantId,
            ProjectAuthorityRole.TenantAdmin);
        using var crossTenantRead = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}",
            otherTenantToken, [OtherTenantId]);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantRead.StatusCode);

        var multiTenantToken = await IssueTokenAsync(
            "projects.multiple-memberships", [TenantId]);
        var multiTenantSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(multiTenantToken).Claims, "sub");
        var firstMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, multiTenantSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            firstMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
        await AddMembershipAsync(projects.PrivilegedFixtureDataSource, multiTenantSubject, OtherTenantId);
        using var ambiguousTenant = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", multiTenantToken, null);
        Assert.Equal(HttpStatusCode.Forbidden, ambiguousTenant.StatusCode);
        using var duplicateSelector = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", multiTenantToken,
            [TenantId, TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, duplicateSelector.StatusCode);
        using var foreignSelector = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", multiTenantToken,
            ["tenant-foreign"]);
        Assert.Equal(HttpStatusCode.Forbidden, foreignSelector.StatusCode);
        using var selectedTenant = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", multiTenantToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, selectedTenant.StatusCode);

        var platformAdminToken = await IssueTokenAsync("projects.admin", [TenantId]);
        var platformAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformAdminToken).Claims, "sub");
        var platformMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        using var defaultsResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", platformAdminToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);

        var orchestratorBootstrap = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "run-actor", null, null, ["orchestrator"]);
        var orchestratorSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(orchestratorBootstrap).Claims, "sub");
        var orchestratorMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, orchestratorSubject, TenantId);
        var orchestratorAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await CreateRunBindingGrantAsync(orchestratorSubject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator", [TenantId],
            "run-actor", project.ProjectId, RunId, ["platform_admin"]);
        var runClaims = new JwtSecurityTokenHandler().ReadJwtToken(runToken).Claims;
        Assert.Contains(runClaims, claim => claim.Type == "project_id" && claim.Value == project.ProjectId);
        Assert.Contains(runClaims, claim => claim.Type == "run_id" && claim.Value == RunId);

        var defaults = new PlatformRuntimeDefaults
        {
            ModelSelection = new ModelSelectionSettings("platform-model"),
            EgressBaseline = [],
            RunLimits = new CopilotRunLimits
            {
                MaxModelTurns = 12,
                MaxToolCalls = 100,
                MaxChildren = 4,
                MaxConcurrentChildren = 2,
                MaxWallTimeSeconds = 3600,
                MaxPromptTokens = 20000,
            },
        };
        using var updateDefaults = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = defaults,
            }),
        };
        AddBearerAndTenant(updateDefaults, platformAdminToken, TenantId);
        using var updatedDefaults = await projects.Client.SendAsync(updateDefaults);
        Assert.Equal(HttpStatusCode.OK, updatedDefaults.StatusCode);

        var selectionRequest = new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = project.ConfigurationRevision,
            ExpectedPlatformRuntimeRevision = 1,
            Context = new RunSelectionContext
            {
                Revision = "provider-catalog-v1",
                AvailableModelSelectionReferences = ImmutableHashSet.Create(
                    StringComparer.Ordinal, "platform-model"),
            },
        };
        using var acceptSelection = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(selectionRequest),
        };
        AddBearerAndTenant(acceptSelection, runToken, TenantId);
        using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
        Assert.Equal(HttpStatusCode.OK, acceptedSelection.StatusCode);

        using var runBoundPlatformAdmin = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, runBoundPlatformAdmin.StatusCode);
        using var runMismatch = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/other-run/selection", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, runMismatch.StatusCode);
        using var wrongProject = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{Guid.NewGuid():N}/runs/{RunId}/selection", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, wrongProject.StatusCode);

        using var beforeRevocation = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/selection", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, beforeRevocation.StatusCode);
        await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, orchestratorAssignment.AssignmentId, 1);
        using var afterRevocation = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/selection", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, afterRevocation.StatusCode);
        using var afterRevocationRequest = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(selectionRequest),
        };
        AddBearerAndTenant(afterRevocationRequest, runToken, TenantId);
        using var afterRevocationCannotStartRun = await projects.Client.SendAsync(afterRevocationRequest);
        Assert.Equal(HttpStatusCode.Forbidden, afterRevocationCannotStartRun.StatusCode);

        await using (var verifyDb = CreateDbContext(projects.PrivilegedFixtureDataSource))
        {
            var revoked = await verifyDb.RoleAssignments.AsNoTracking()
                .SingleAsync(item => item.AssignmentId == orchestratorAssignment.AssignmentId);
            Assert.Equal(ProjectAuthorityRecordState.Revoked, revoked.State);
            Assert.Equal(2, revoked.Revision);
            Assert.Contains(await verifyDb.AuthorityAudit.AsNoTracking()
                .Where(item => item.AssignmentId == orchestratorAssignment.AssignmentId)
                .ToArrayAsync(), item => item.EventType == "role_revoked" && item.Revision == 2);
        }
        await using (var staleDb = CreateDbContext(projects.PrivilegedFixtureDataSource))
        {
            var staleStore = new ProjectsConfigPrivilegedAuthorityStore(staleDb, TimeProvider.System);
            await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
                staleStore.RevokeRoleAssignmentAsync(
                    orchestratorAssignment.AssignmentId, 1, "local-test-fixture"));
        }

        var purposeToken = CreateSignedAccessToken(
            platformAdminSubject,
            new Claim("purpose", "secret-redemption"));
        using var purposeDenied = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", purposeToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, purposeDenied.StatusCode);

        await using var wrongAudience = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), "https://other-api.test");
        using var wrongResource = await SendAsync(
            wrongAudience.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResource.StatusCode);
        Assert.NotEqual(Guid.Empty, ownerAssignment.AssignmentId);
    }

    private async Task<string> IssueTokenAsync(
        string additionalScope,
        IReadOnlyList<string> tenantIds,
        params string[] roles) =>
        await IssueTokenAsync(additionalScope, tenantIds, null, null, null, roles);

    private async Task<string> IssueTokenAsync(
        string additionalScope,
        IReadOnlyList<string> tenantIds,
        string? upstreamSubject,
        string? projectId,
        string? runId,
        string[] roles)
    {
        _fakeIdp.Subject = upstreamSubject ?? $"external-subject-{Interlocked.Increment(ref _subjectSequence)}";
        _fakeIdp.TenantIds = tenantIds;
        _fakeIdp.Roles = roles;
        using var broker = _brokerFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        var (verifier, challenge) = Pkce.Create();
        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            $"openid profile email api.read offline_access {additionalScope}",
            challenge,
            projectId: projectId,
            runId: runId);
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        return tokens.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The Identity broker did not return an access token.");
    }

    private string CreateSignedAccessToken(string subject, params Claim[] additionalClaims)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("scope", "api.read projects.admin"),
        };
        claims.AddRange(additionalClaims);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            claims,
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(new X509SecurityKey(certificate), SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task CreateRunBindingGrantAsync(string subject, string projectId, string runId)
    {
        using var scope = _brokerFactory.Services.CreateScope();
        var authority = scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>();
        var grant = new SecretRedemptionGrant(
            $"projects-config:{runId}",
            subject,
            projectId,
            runId,
            "projects.selection",
            new SecretRef("projects-selection", "v1"),
            GrantState.Active,
            DateTimeOffset.UtcNow.AddMinutes(30));
        await authority.ReplaceAsync(grant, 0, $"local-run-{Guid.NewGuid():N}");
    }

    private static async Task<ProjectTenantMembershipRecord> AddMembershipAsync(
        NpgsqlDataSource privilegedDataSource,
        string subject,
        string tenantId)
    {
        await using var db = CreateDbContext(privilegedDataSource);
        return await new ProjectsConfigPrivilegedAuthorityStore(db, TimeProvider.System)
            .GrantMembershipAsync(new Uri(TestIssuer).AbsoluteUri, subject, tenantId, "local-test-fixture");
    }

    private static async Task<ProjectRoleAssignmentRecord> AssignRoleAsync(
        NpgsqlDataSource privilegedDataSource,
        Guid membershipId,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role)
    {
        await using var db = CreateDbContext(privilegedDataSource);
        return await new ProjectsConfigPrivilegedAuthorityStore(db, TimeProvider.System)
            .AssignRoleAsync(membershipId, resourceType, resourceId, role, "local-test-fixture");
    }

    private static async Task RevokeRoleAsync(
        NpgsqlDataSource privilegedDataSource,
        Guid assignmentId,
        long expectedRevision)
    {
        await using var db = CreateDbContext(privilegedDataSource);
        await new ProjectsConfigPrivilegedAuthorityStore(db, TimeProvider.System)
            .RevokeRoleAssignmentAsync(assignmentId, expectedRevision, "local-test-fixture");
    }

    private static ProjectsConfigDbContext CreateDbContext(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        return new ProjectsConfigDbContext(options);
    }

    private static async Task AssertRuntimeCannotWriteAuthorityAsync(NpgsqlDataSource runtimeDataSource)
    {
        await using var connection = await runtimeDataSource.OpenConnectionAsync();
        string[] prohibitedWrites =
        [
            """
            INSERT INTO projects_config.tenant_memberships
                (membership_id, issuer, subject, tenant_id, state, revision, granted_by, granted_at)
            VALUES ('00000000-0000-0000-0000-000000000001', 'https://issuer.test/', 'forged', 'tenant-1',
                    'Active', 1, 'runtime', now())
            """,
            "UPDATE projects_config.tenant_memberships SET revision = 2 WHERE FALSE",
            """
            INSERT INTO projects_config.project_role_assignments
                (assignment_id, membership_id, resource_type, resource_id, role, state, revision, granted_by, granted_at)
            VALUES ('00000000-0000-0000-0000-000000000002',
                    '00000000-0000-0000-0000-000000000001', 'Project', 'project', 'Owner', 'Active', 1, 'runtime', now())
            """,
            "UPDATE projects_config.project_role_assignments SET revision = 2 WHERE FALSE",
            """
            INSERT INTO projects_config.authority_audit
                (event_id, event_type, membership_id, issuer, subject, tenant_id, revision, actor, created_at)
            VALUES ('00000000-0000-0000-0000-000000000003', 'membership_granted',
                    '00000000-0000-0000-0000-000000000001', 'https://issuer.test/', 'forged',
                    'tenant-1', 1, 'runtime', now())
            """,
            "UPDATE projects_config.authority_audit SET actor = 'runtime' WHERE FALSE",
        ];
        foreach (var sql in prohibitedWrites)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        IReadOnlyList<string>? tenantSelectors,
        string? roleHeader = null)
    {
        using var request = new HttpRequestMessage(method, path);
        AddBearerAndTenant(request, token, tenantSelectors, roleHeader);
        return await client.SendAsync(request);
    }

    private static void AddBearerAndTenant(
        HttpRequestMessage request,
        string token,
        string tenantId,
        string? roleHeader = null) =>
        AddBearerAndTenant(request, token, [tenantId], roleHeader);

    private static void AddBearerAndTenant(
        HttpRequestMessage request,
        string token,
        IReadOnlyList<string>? tenantSelectors,
        string? roleHeader = null)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantSelectors is not null)
            request.Headers.TryAddWithoutValidation(ProjectAuthorizationOwner.TenantSelectorHeader, tenantSelectors);
        if (roleHeader is not null)
            request.Headers.TryAddWithoutValidation("X-Role", roleHeader);
    }

    private static string SingleClaim(IEnumerable<Claim> claims, string claimType) =>
        Assert.Single(claims, claim => claim.Type == claimType).Value;
}
