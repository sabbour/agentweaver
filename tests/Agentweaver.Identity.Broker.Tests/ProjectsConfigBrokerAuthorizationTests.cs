extern alias ProjectsConfig;
extern alias EnvironmentService;
extern alias KnowledgeService;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Providers;
using KnowledgeService::Agentweaver.Knowledge;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using EnvironmentCaller = EnvironmentService::Agentweaver.Environment.CurrentCallerRequest;
using EnvironmentProjectsConfigHttpClient = EnvironmentService::Agentweaver.Environment.ProjectsConfigHttpClient;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed partial class ProjectsConfigBrokerAuthorizationTests(
    PostgresContainerFixture postgres, Xunit.Abstractions.ITestOutputHelper output)
    : IAsyncLifetime
{
    private static readonly JsonSerializerOptions AuthorizationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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
    private NpgsqlDataSource _nativeDataSource = null!;
    private (string PfxPath, string Password) _signingCertificate;
    private int _subjectSequence;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync();
        _nativeDataSource = NpgsqlDataSource.Create(_connectionString);
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
        var before = await PostgresContainerFixture.CountConnectionsAsync(_connectionString);
        _fakeIdpClient.Dispose();
        await _brokerFactory.DisposeAsync();
        await _nativeDataSource.DisposeAsync();
        await _fakeIdp.DisposeAsync();
        if (File.Exists(_signingCertificate.PfxPath))
            File.Delete(_signingCertificate.PfxPath);
        if (Directory.Exists(_signingCertificate.PfxPath + ".keys"))
            Directory.Delete(_signingCertificate.PfxPath + ".keys", recursive: true);
        var after = await PostgresContainerFixture.CountConnectionsAsync(_connectionString);
        Console.WriteLine($"Native fixture owned connections: before cleanup={before}, after cleanup={after}.");
        Assert.Equal(0, after);
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
            "projects.admin",
            ["upstream-tenant"],
            "projects-owner",
            null,
            null,
            ["platform_admin"]);
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
        var project = await created.Content.ReadFromJsonAsync<ProjectSummary>(AuthorizationJsonOptions);
        Assert.NotNull(project);

        var ownerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        var ownerConsumerToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "projects-owner",
            null,
            null,
            ["platform_admin"]);
        var ownerConsumerClaims = new JwtSecurityTokenHandler().ReadJwtToken(ownerConsumerToken).Claims;
        Assert.DoesNotContain(ownerConsumerClaims, claim => claim.Type is "project_id" or "run_id");

        var environmentProjects = new EnvironmentProjectsConfigHttpClient(projects.Client);
        var ownerConsumer = new EnvironmentCaller(ownerConsumerToken, TenantId);
        var ownerConsumerContext = await environmentProjects.GetAuthorizationContextAsync(
            ownerConsumer, CancellationToken.None);
        Assert.Null(ownerConsumerContext.BoundProjectId);
        Assert.Null(ownerConsumerContext.BoundRunId);
        Assert.True(HasPermission(
            ownerConsumerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadRunSelection));
        Assert.True(HasPermission(
            ownerConsumerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));

        using var updateProjectConfiguration = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/configuration")
        {
            Content = JsonContent.Create(new UpdateProjectConfigurationRequest
            {
                ExpectedRevision = project.ConfigurationRevision,
                Configuration = new ProjectConfiguration
                {
                    ModelSelection = new ModelSelectionSettings("owner-consumer-model"),
                },
            }),
        };
        AddBearerAndTenant(updateProjectConfiguration, ownerConsumerToken, TenantId);
        using var updatedProjectConfiguration = await projects.Client.SendAsync(updateProjectConfiguration);
        Assert.Equal(HttpStatusCode.OK, updatedProjectConfiguration.StatusCode);
        var updatedConfiguration = await updatedProjectConfiguration.Content
            .ReadFromJsonAsync<VersionedProjectConfiguration>();
        Assert.NotNull(updatedConfiguration);
        Assert.Equal(project.ConfigurationRevision + 1, updatedConfiguration.Revision);

        using var ownerRead = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        using var ownerContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, ownerContextResponse.StatusCode);
        AssertNoStore(ownerContextResponse);
        var ownerContextJsonText = await ownerContextResponse.Content.ReadAsStringAsync();
        var ownerContext = JsonSerializer.Deserialize<ProjectAuthorizationContextResponse>(
            ownerContextJsonText, AuthorizationJsonOptions)
            ?? throw new InvalidOperationException("Projects & Config returned an empty authorization context.");
        Assert.Equal(ProjectAuthorizationContext.CurrentContractVersion, ownerContext.ContractVersion);
        Assert.Equal(new Uri(TestIssuer).AbsoluteUri, ownerContext.Issuer);
        Assert.Equal(ownerSubject, ownerContext.ActorId);
        Assert.Equal(TenantId, ownerContext.TenantId);
        Assert.Equal(ownerMembership.Revision, ownerContext.MembershipRevision);
        Assert.True(HasPermission(
            ownerContext, ProjectAuthorityResourceType.Tenant, TenantId,
            ProjectAuthorizationPermission.CreateProjects));
        Assert.True(HasPermission(
            ownerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadProjects));
        Assert.True(HasPermission(
            ownerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));
        var ownerProjectAuthority = Assert.Single(
            ownerContext.EffectiveAuthority,
            item => item.ResourceType == ProjectAuthorityResourceType.Project &&
                item.ResourceId == project.ProjectId);
        Assert.Contains(ownerProjectAuthority.Permissions, permission =>
            permission.Permission == ProjectAuthorizationPermission.ReadProjects &&
            permission.RoleRevision == ownerAssignment.Revision);
        using (var ownerContextJson = JsonDocument.Parse(ownerContextJsonText))
        {
            Assert.False(ownerContextJson.RootElement.TryGetProperty("grants", out _));
            foreach (var resource in ownerContextJson.RootElement
                .GetProperty("effectiveAuthority").EnumerateArray())
            foreach (var permission in resource.GetProperty("permissions").EnumerateArray())
            {
                Assert.False(permission.TryGetProperty("assignmentId", out _));
                Assert.False(permission.TryGetProperty("role", out _));
            }
        }
        using var subjectQuerySpoof = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/authorization/context?subject={Uri.EscapeDataString(ownerSubject)}",
            ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, subjectQuerySpoof.StatusCode);
        using var roleQuerySpoof = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context?role=platformAdmin",
            ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, roleQuerySpoof.StatusCode);
        var duplicateSubjectToken = CreateSignedAccessToken(
            ownerSubject, new Claim("sub", "spoofed-subject"));
        using var duplicateSubjectDenied = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context",
            duplicateSubjectToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Unauthorized, duplicateSubjectDenied.StatusCode);

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
        using var ambiguousContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", multiTenantToken, null);
        Assert.Equal(HttpStatusCode.Forbidden, ambiguousContext.StatusCode);
        using var duplicateSelectorContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", multiTenantToken,
            [TenantId, TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, duplicateSelectorContext.StatusCode);
        using var foreignSelectorContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", multiTenantToken,
            ["tenant-foreign"]);
        Assert.Equal(HttpStatusCode.Forbidden, foreignSelectorContext.StatusCode);
        using var selectedTenantContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", multiTenantToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, selectedTenantContext.StatusCode);
        Assert.Equal(
            multiTenantSubject,
            (await ReadAuthorizationContextAsync(selectedTenantContext)).ActorId);

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
        var platformReadOnlyToken = await IssueTokenAsync("api.read", [TenantId]);
        var platformReadOnlySubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformReadOnlyToken).Claims, "sub");
        var platformReadOnlyMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformReadOnlySubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformReadOnlyMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        using var roleCannotReplaceAdminScope = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults",
            platformReadOnlyToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, roleCannotReplaceAdminScope.StatusCode);
        using var readOnlyContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", platformReadOnlyToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, readOnlyContext.StatusCode);
        Assert.Empty((await ReadAuthorizationContextAsync(readOnlyContext)).EffectiveAuthority);

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
        using var runContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, runContextResponse.StatusCode);
        AssertNoStore(runContextResponse);
        var runContext = await ReadAuthorizationContextAsync(runContextResponse);
        Assert.Equal(project.ProjectId, runContext.BoundProjectId);
        Assert.Equal(RunId, runContext.BoundRunId);
        var scopedRunAuthority = Assert.Single(runContext.EffectiveAuthority);
        Assert.Equal(ProjectAuthorityResourceType.Project, scopedRunAuthority.ResourceType);
        Assert.Equal(project.ProjectId, scopedRunAuthority.ResourceId);
        Assert.True(HasPermission(
            runContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadRunSelection));
        Assert.True(HasPermission(
            runContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.AcceptRunSelection));
        Assert.False(HasPermission(
            runContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadProjects));

        var viewerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
        var unboundViewerToken = CreateSignedAccessToken(orchestratorSubject);
        var purposeBoundViewerToken = CreateSignedAccessToken(
            orchestratorSubject,
            new Claim("project_id", project.ProjectId),
            new Claim("run_id", RunId),
            new Claim("purpose", "secret-redemption"));
        using var runBoundViewerRead = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId={RunId}", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, runBoundViewerRead.StatusCode);
        AssertNoStore(runBoundViewerRead);
        using var mismatchedRunViewerRead = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId=other-run", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, mismatchedRunViewerRead.StatusCode);
        using var unboundViewerRead = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId={RunId}", unboundViewerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, unboundViewerRead.StatusCode);
        using var purposeBoundViewerRead = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId={RunId}", purposeBoundViewerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, purposeBoundViewerRead.StatusCode);
        using var duplicateRunViewerRead = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId={RunId}&runId={RunId}", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, duplicateRunViewerRead.StatusCode);

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
            ExpectedProjectConfigRevision = updatedConfiguration.Revision,
            ExpectedPlatformRuntimeRevision = 1,
            Context = new RunSelectionContext
            {
                Revision = "provider-catalog-v1",
                AvailableModelSelectionReferences = ImmutableHashSet.Create(
                    StringComparer.Ordinal, "owner-consumer-model"),
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

        var ownerConsumerSelection = await environmentProjects.GetRunSelectionAsync(
            ownerConsumer, project.ProjectId, RunId, CancellationToken.None);
        Assert.Equal(project.ProjectId, ownerConsumerSelection.ProjectId);
        Assert.Equal(RunId, ownerConsumerSelection.RunId);

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
        var orchestratorReadOnlyToken = await IssueTokenAsync("api.read", [TenantId]);
        var orchestratorReadOnlySubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(orchestratorReadOnlyToken).Claims, "sub");
        var orchestratorReadOnlyMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, orchestratorReadOnlySubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorReadOnlyMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        using var roleCannotReplaceOrchestratorScope = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/selection",
            orchestratorReadOnlyToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, roleCannotReplaceOrchestratorScope.StatusCode);
        await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, orchestratorAssignment.AssignmentId, 1);
        using var refreshedRunContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, refreshedRunContext.StatusCode);
        Assert.Empty((await ReadAuthorizationContextAsync(refreshedRunContext)).EffectiveAuthority);
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
        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource, viewerAssignment.AssignmentId, 1);
        using var afterViewerRevocation = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}?runId={RunId}", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.NotFound, afterViewerRevocation.StatusCode);

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
        var staleStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(projects.PrivilegedFixtureDataSource), TimeProvider.System);
        await Assert.ThrowsAsync<ProjectAuthorityConcurrencyException>(() =>
            staleStore.RevokeRoleAssignmentAsync(
                orchestratorAssignment.AssignmentId, 1, "local-test-fixture"));

        var purposeToken = CreateSignedAccessToken(
            platformAdminSubject,
            new Claim("purpose", "secret-redemption"));
        using var purposeDenied = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", purposeToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, purposeDenied.StatusCode);
        using var purposeContextDenied = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", purposeToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, purposeContextDenied.StatusCode);

        await using var wrongAudience = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), "https://other-api.test");
        using var wrongResource = await SendAsync(
            wrongAudience.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResource.StatusCode);
        using var wrongResourceContext = await SendAsync(
            wrongAudience.Client, HttpMethod.Get, "/api/authorization/context", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResourceContext.StatusCode);

        await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(projects.PrivilegedFixtureDataSource), TimeProvider.System)
            .RevokeMembershipAsync(orchestratorMembership.MembershipId, 1, "local-test-fixture");
        using var revokedMembershipContext = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, revokedMembershipContext.StatusCode);
        Assert.NotEqual(Guid.Empty, ownerAssignment.AssignmentId);
    }

    [Fact]
    public async Task KnowledgePrivateReadsRequireLiveProjectAdminAndStayWithinProjectScope()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var database = await KnowledgeTestDatabase.CreateAsync(_connectionString);
        var catalog = CreateKnowledgeMemoryCatalog(database);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            new X509SecurityKey(certificate),
            providerCatalog: catalog);

        const string tenantAdminUpstreamSubject = "knowledge-tenant-admin";
        var tenantAdminToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            tenantAdminUpstreamSubject,
            null,
            null,
            []);
        var tenantAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(tenantAdminToken).Claims, "sub");
        var tenantAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, tenantAdminToken, TenantId, "Knowledge visibility project");
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);

        var ownerToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "knowledge-project-owner",
            null,
            null,
            []);
        var ownerClaims = new JwtSecurityTokenHandler().ReadJwtToken(ownerToken).Claims;
        Assert.DoesNotContain(ownerClaims,
            claim => claim.Type is "tenant_id" or "tid" or "role" or "roles" or
                ClaimTypes.Role or "project_id" or "run_id");
        var ownerSubject = SingleClaim(ownerClaims, "sub");
        var ownerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, ownerSubject, TenantId);
        var ownerAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        var backupOwnerToken = await IssueTokenAsync(
            "projects.admin",
            [TenantId],
            "knowledge-backup-owner",
            null,
            null,
            []);
        var backupOwnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(backupOwnerToken).Claims, "sub");
        var backupOwnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, backupOwnerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            backupOwnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            ownerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);

        var metadataViewerToken = await IssueTokenAsync(
            "projects.orchestrator",
            [TenantId],
            "knowledge-metadata-viewer",
            null,
            null,
            []);
        var metadataViewerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(metadataViewerToken).Claims, "sub");
        var metadataViewerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, metadataViewerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            metadataViewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            metadataViewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);

        var foreignOwnerToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [OtherTenantId],
            "knowledge-foreign-owner",
            null,
            null,
            []);
        var foreignOwnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(foreignOwnerToken).Claims, "sub");
        var foreignOwnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, foreignOwnerSubject, OtherTenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            foreignOwnerMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            OtherTenantId,
            ProjectAuthorityRole.TenantAdmin);
        var foreignProject = await CreateProjectsTestProjectAsync(
            projects.Client, foreignOwnerToken, OtherTenantId, "Foreign Knowledge project");
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            foreignOwnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            foreignProject.ProjectId,
            ProjectAuthorityRole.Owner);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            foreignOwnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            foreignProject.ProjectId,
            ProjectAuthorityRole.Orchestrator);

        using var viewerContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", metadataViewerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, viewerContextResponse.StatusCode);
        var viewerContext = await ReadAuthorizationContextAsync(viewerContextResponse);
        Assert.True(HasPermission(
            viewerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadProjects));
        Assert.True(HasPermission(
            viewerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadRunSelection));
        Assert.False(HasPermission(
            viewerContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));

        using var ownerContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, ownerContextResponse.StatusCode);
        Assert.True(HasPermission(
            await ReadAuthorizationContextAsync(ownerContextResponse),
            ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));

        using var tenantAdminContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", tenantAdminToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, tenantAdminContextResponse.StatusCode);
        var tenantAdminContext = await ReadAuthorizationContextAsync(tenantAdminContextResponse);
        Assert.True(HasPermission(
            tenantAdminContext, ProjectAuthorityResourceType.Tenant, TenantId,
            ProjectAuthorizationPermission.WriteProjects));
        Assert.False(HasPermission(
            tenantAdminContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));
        Assert.True(HasPermission(
            tenantAdminContext, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadRunSelection));

        var acceptedRun = await AcceptKnowledgeRunSelectionAsync(
            projects, database, project, tenantAdminToken);
        var ownerRunId = acceptedRun.RunId;
        await using var app = await CreateKnowledgeTestAppAsync(
            database, catalog, projects.Client, new X509SecurityKey(certificate));
        using var knowledge = app.GetTestClient();

        const string ownerSecret = "private-owner-session-context";
        const string ownerAgentId = "agent-a";
        var ownerBasePath =
            $"/api/projects/{project.ProjectId}/runs/{ownerRunId}/agents/{ownerAgentId}";
        using var ownerCreate = new HttpRequestMessage(HttpMethod.Post, $"{ownerBasePath}/records")
        {
            Content = JsonContent.Create(new
            {
                kind = "sessionContext",
                type = "session",
                content = ownerSecret,
                importance = "medium",
                tags = new[] { "authorization-test" },
            }),
        };
        AddBearerAndTenant(ownerCreate, ownerToken, [TenantId]);
        ownerCreate.Headers.TryAddWithoutValidation("Idempotency-Key", $"knowledge-owner-{Guid.NewGuid():N}");
        using var ownerCreated = await knowledge.SendAsync(ownerCreate);
        Assert.True(
            ownerCreated.StatusCode == HttpStatusCode.Created,
            await ownerCreated.Content.ReadAsStringAsync());
        using var ownerCreatedJson = JsonDocument.Parse(await ownerCreated.Content.ReadAsStringAsync());
        var ownerRecordId = ownerCreatedJson.RootElement
            .GetProperty("record").GetProperty("recordId").GetGuid();
        var ownerReadPaths = KnowledgeReadPaths(
            project.ProjectId, ownerRunId, ownerAgentId, ownerRecordId);

        const string privateAgentId = "agent-b";
        const string privateAgentSecret = "private-agent-b-memory";
        var privateAgentBasePath =
            $"/api/projects/{project.ProjectId}/runs/{ownerRunId}/agents/{privateAgentId}";
        using var privateAgentCreate = new HttpRequestMessage(
            HttpMethod.Post, $"{privateAgentBasePath}/records")
        {
            Content = JsonContent.Create(new
            {
                kind = "memory",
                type = "note",
                content = privateAgentSecret,
                importance = "medium",
                tags = new[] { "authorization-test" },
            }),
        };
        AddBearerAndTenant(privateAgentCreate, ownerToken, [TenantId]);
        privateAgentCreate.Headers.TryAddWithoutValidation(
            "Idempotency-Key", $"knowledge-agent-b-{Guid.NewGuid():N}");
        using var privateAgentCreated = await knowledge.SendAsync(privateAgentCreate);
        Assert.True(
            privateAgentCreated.StatusCode == HttpStatusCode.Created,
            await privateAgentCreated.Content.ReadAsStringAsync());
        using var privateAgentCreatedJson =
            JsonDocument.Parse(await privateAgentCreated.Content.ReadAsStringAsync());
        var privateAgentRecord = privateAgentCreatedJson.RootElement.GetProperty("record");
        var privateAgentRecordId = privateAgentRecord.GetProperty("recordId").GetGuid();
        var privateAgentRevisionId = privateAgentRecord.GetProperty("revisionId").GetGuid();
        var privateAgentReadPaths = KnowledgeReadPaths(
            project.ProjectId, ownerRunId, privateAgentId, privateAgentRecordId);

        foreach (var path in ownerReadPaths)
        {
            using var response = await SendAsync(knowledge, HttpMethod.Get, path, ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(ownerSecret, body, StringComparison.Ordinal);
        }

        foreach (var path in ownerReadPaths)
        {
            using var response = await SendAsync(
                knowledge, HttpMethod.Get, path, tenantAdminToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(ownerSecret, body, StringComparison.Ordinal);
        }

        foreach (var path in ownerReadPaths)
        {
            using var response = await SendAsync(
                knowledge, HttpMethod.Get, path, metadataViewerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(ownerSecret, body, StringComparison.Ordinal);
        }

        foreach (var path in privateAgentReadPaths)
        {
            using var response = await SendAsync(knowledge, HttpMethod.Get, path, ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(privateAgentSecret, body, StringComparison.Ordinal);
        }

        var ownerAgentPrivatePaths = KnowledgeReadPaths(
            project.ProjectId, ownerRunId, ownerAgentId, privateAgentRecordId);
        foreach (var index in new[] { 0, 3 })
        {
            using var response = await SendAsync(
                knowledge, HttpMethod.Get, ownerAgentPrivatePaths[index], ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(ownerSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(privateAgentSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(privateAgentRecordId.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain(privateAgentRevisionId.ToString(), body, StringComparison.Ordinal);
        }

        foreach (var index in new[] { 1, 2 })
        {
            using var response = await SendAsync(
                knowledge, HttpMethod.Get, ownerAgentPrivatePaths[index], ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(privateAgentSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(privateAgentRecordId.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain(privateAgentRevisionId.ToString(), body, StringComparison.Ordinal);
        }

        const string foreignSecret = "private-foreign-session-context";
        var foreignRunId = "knowledge-run-foreign";
        var foreignCreated = await database.Provider.CreateAsync(
            new KnowledgeRecordCreate(
                foreignProject.ProjectId,
                "agent-foreign",
                KnowledgeRecordKind.Memory,
                "note",
                null,
                foreignSecret,
                null,
                "medium",
                ["authorization-test"],
                foreignRunId,
                null,
                new string('a', 64),
                "authorization-test"),
            $"knowledge-foreign-{Guid.NewGuid():N}");
        Assert.Equal(KnowledgeWriteStatus.Created, foreignCreated.Status);
        var foreignRecordId = foreignCreated.Record!.RecordId;
        var foreignReadPaths = KnowledgeReadPaths(
            foreignProject.ProjectId, foreignRunId, "agent-foreign", foreignRecordId);

        foreach (var path in foreignReadPaths)
        {
            using var response = await SendAsync(knowledge, HttpMethod.Get, path, ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(foreignSecret, body, StringComparison.Ordinal);
        }

        foreach (var path in foreignReadPaths)
        {
            using var response = await SendAsync(
                knowledge, HttpMethod.Get, path, tenantAdminToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(foreignSecret, body, StringComparison.Ordinal);
        }

        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource, ownerAssignment.AssignmentId, 1);
        using var revokedContextResponse = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/authorization/context", ownerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, revokedContextResponse.StatusCode);
        Assert.False(HasPermission(
            await ReadAuthorizationContextAsync(revokedContextResponse),
            ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));

        foreach (var path in ownerReadPaths)
        {
            using var response = await SendAsync(knowledge, HttpMethod.Get, path, ownerToken, [TenantId]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(ownerSecret, body, StringComparison.Ordinal);
        }
    }

    private static async Task<ProjectSummary> CreateProjectsTestProjectAsync(
        HttpClient client,
        string token,
        string tenantId,
        string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = name }),
        };
        AddBearerAndTenant(request, token, [tenantId]);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ProjectSummary>(AuthorizationJsonOptions)
            ?? throw new InvalidOperationException("Projects & Config returned no project.");
    }

    private static string[] KnowledgeReadPaths(
        string projectId,
        string runId,
        string agentId,
        Guid recordId)
    {
        var prefix = $"/api/projects/{projectId}/runs/{runId}/agents/{agentId}";
        return
        [
            $"{prefix}/records",
            $"{prefix}/records/{recordId:D}",
            $"{prefix}/records/{recordId:D}/revisions",
            $"{prefix}/context",
        ];
    }

    private static ProviderCatalog CreateKnowledgeMemoryCatalog(KnowledgeTestDatabase database)
    {
        var provider = database.Provider;
        var registration = new ProviderRegistration(
            provider.Descriptor,
            Enabled: true,
            database.Options.OptionsRevision,
            database.Options.OptionsSchemaVersion);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)],
            [new ProviderOverridePermission(
                ProviderSeam.Memory, NativePostgresMemoryProvider.ProviderId)]).Value);
    }

    private async Task<EffectiveRunSelection> AcceptKnowledgeRunSelectionAsync(
        ProjectsConfigResourceServer projects,
        KnowledgeTestDatabase database,
        ProjectSummary project,
        string tenantAdminToken)
    {
        const string modelReference = "knowledge-auth-model";
        using var updateProject = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/configuration")
        {
            Content = JsonContent.Create(new UpdateProjectConfigurationRequest
            {
                ExpectedRevision = project.ConfigurationRevision,
                Configuration = new ProjectConfiguration
                {
                    ModelSelection = new ModelSelectionSettings(modelReference),
                },
            }),
        };
        AddBearerAndTenant(updateProject, tenantAdminToken, [TenantId]);
        using var updatedProject = await projects.Client.SendAsync(updateProject);
        Assert.Equal(HttpStatusCode.OK, updatedProject.StatusCode);
        var projectConfiguration = await updatedProject.Content
            .ReadFromJsonAsync<VersionedProjectConfiguration>(AuthorizationJsonOptions);
        Assert.NotNull(projectConfiguration);

        var platformAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "knowledge-platform-admin", null, null, []);
        var platformAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformAdminToken).Claims, "sub");
        var platformAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var defaults = new PlatformRuntimeDefaults
        {
            ModelSelection = new ModelSelectionSettings(modelReference),
            EgressBaseline = [],
            RunLimits = new CopilotRunLimits
            {
                MaxModelTurns = 12,
                MaxToolCalls = 100,
                MaxChildren = 4,
                MaxConcurrentChildren = 2,
                MaxWallTimeSeconds = 3600,
                MaxPromptTokens = 20_000,
            },
        };
        using var updateDefaults = new HttpRequestMessage(
            HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = defaults,
            }),
        };
        AddBearerAndTenant(updateDefaults, platformAdminToken, [TenantId]);
        using var updatedDefaults = await projects.Client.SendAsync(updateDefaults);
        Assert.Equal(HttpStatusCode.OK, updatedDefaults.StatusCode);
        var platformDefaults = await updatedDefaults.Content
            .ReadFromJsonAsync<VersionedPlatformRuntimeDefaults>(AuthorizationJsonOptions);
        Assert.NotNull(platformDefaults);
        Assert.Equal(1, platformDefaults.Revision);

        const string runId = "knowledge-run-owner";
        const string orchestratorUpstreamSubject = "knowledge-run-selection-actor";
        var orchestratorBootstrap = await IssueTokenAsync(
            "projects.bootstrap",
            [TenantId],
            orchestratorUpstreamSubject,
            null,
            null,
            ["orchestrator"]);
        var orchestratorSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(orchestratorBootstrap).Claims, "sub");
        var orchestratorMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, orchestratorSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        await CreateRunBindingGrantAsync(orchestratorSubject, project.ProjectId, runId);
        var orchestratorToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            orchestratorUpstreamSubject,
            project.ProjectId,
            runId,
            ["platform_admin"]);
        var provider = database.Provider;
        var request = new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = projectConfiguration.Revision,
            ExpectedPlatformRuntimeRevision = platformDefaults.Revision,
            Context = new RunSelectionContext
            {
                Revision = "knowledge-auth-context-v1",
                AvailableModelSelectionReferences =
                    ImmutableHashSet.Create(StringComparer.Ordinal, modelReference),
                ProviderRequirements =
                [
                    new ProviderRequirement
                    {
                        Seam = ProviderSeam.Memory,
                        RequiredAdapterVersion = provider.Descriptor.AdapterVersion.ToString(),
                        RequiredOptionsSchemaVersion = database.Options.OptionsSchemaVersion,
                        RequiredCapabilities = MemoryProviderCapabilities.All,
                    },
                ],
            },
        };
        using var acceptRun = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{runId}/selection")
        {
            Content = JsonContent.Create(request),
        };
        AddBearerAndTenant(acceptRun, orchestratorToken, [TenantId]);
        using var accepted = await projects.Client.SendAsync(acceptRun);
        Assert.True(
            accepted.StatusCode == HttpStatusCode.OK,
            $"{accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}");
        var selection = await accepted.Content.ReadFromJsonAsync<EffectiveRunSelection>(AuthorizationJsonOptions);
        Assert.NotNull(selection);
        Assert.Equal(project.ProjectId, selection.ProjectId);
        Assert.Equal(runId, selection.RunId);
        var memory = Assert.Single(selection.Providers, item => item.Seam == ProviderSeam.Memory);
        var candidate = Assert.Single(memory.Candidates);
        Assert.Equal(provider.Descriptor.Id, candidate.ProviderId);
        Assert.Equal(database.Options.OptionsRevision, candidate.OptionsRevision);
        Assert.Equal(database.Options.OptionsSchemaVersion, candidate.OptionsSchemaVersion);
        return selection;
    }

    private static async Task<WebApplication> CreateKnowledgeTestAppAsync(
        KnowledgeTestDatabase database,
        ProviderCatalog catalog,
        HttpClient projectsClient,
        SecurityKey signingKey,
        IReadOnlyList<string>? additionalAudiences = null)
    {
        var provider = database.Provider;
        var runtimeOptions = new KnowledgeRuntimeOptions(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer),
            "https://api.test",
            new Uri("https://projects.test/"),
            new Uri("https://events.test/"),
            "events-tests",
            database.Options,
            MaximumContextCandidates: 100,
            DefaultContextItems: 20,
            DefaultContextTokens: 1000);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication =>
            {
                authentication.MapInboundClaims = false;
                authentication.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudiences = new[] { runtimeOptions.Audience }
                        .Concat(additionalAudiences ?? Array.Empty<string>())
                        .ToArray(),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton(database.Options);
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddSingleton(new PostgresOutbox(database.DataSource, database.Options.Schema));
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(new ProviderResolver(catalog));
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<IMemoryProvider>(provider);
        builder.Services.AddSingleton<IReadOnlyDictionary<string, IMemoryProvider>>(
            new Dictionary<string, IMemoryProvider>(StringComparer.Ordinal)
            {
                [NativePostgresMemoryProvider.ProviderId] = provider
            });
        builder.Services.AddSingleton<ProjectsConfigClient>(services => new ProjectsConfigClient(
            projectsClient,
            services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            runtimeOptions));
        builder.Services.AddSingleton(new HttpClient
        {
            BaseAddress = runtimeOptions.EventsBaseAddress,
        });
        builder.Services.AddSingleton<AcceptedEffectRelay>();
        builder.Services.AddScoped<KnowledgeProviderBindingService>();
        builder.Services.AddSingleton<MemoryContextCompiler>();
        builder.Services.AddScoped<KnowledgeApplicationService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKnowledgeEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class KnowledgeTestDatabase(
        NativePostgresMemoryOptions options,
        NpgsqlDataSource adminDataSource,
        NpgsqlDataSource dataSource,
        string runtimeRole,
        NativePostgresMemoryProvider provider) : IAsyncDisposable
    {
        public NativePostgresMemoryOptions Options { get; } = options;
        private NpgsqlDataSource AdminDataSource { get; } = adminDataSource;
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public NativePostgresMemoryProvider Provider { get; } = provider;

        public static async Task<KnowledgeTestDatabase> CreateAsync(string connectionString)
        {
            var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString);
            var databaseName = adminConnectionString.Database
                ?? throw new InvalidOperationException("The PostgreSQL test connection has no database name.");
            var adminDataSource = NpgsqlDataSource.Create(connectionString);
            var options = new NativePostgresMemoryOptions(
                "knowledge-auth-test",
                databaseName,
                1,
                $"knowledge_auth_{Guid.NewGuid():N}",
                "knowledge-auth-v1",
                NativePostgresMemoryOptions.CurrentOptionsSchemaVersion);
            var runtimeRole = $"knowledge_auth_runtime_{Guid.NewGuid():N}";
            const string runtimePassword = "knowledge-auth-test-password";
            NpgsqlDataSource? dataSource = null;
            var ownsResources = true;
            try
            {
                await KnowledgeMigrator.MigrateAsync(adminDataSource, options.Schema);
                await using (var connection = await adminDataSource.OpenConnectionAsync())
                await using (var grants = new NpgsqlCommand($"""
                    CREATE ROLE "{runtimeRole}" LOGIN PASSWORD '{runtimePassword}';
                    GRANT USAGE ON SCHEMA "{options.Schema}" TO "{runtimeRole}";
                    GRANT SELECT, INSERT, UPDATE ON "{options.Schema}".knowledge_records TO "{runtimeRole}";
                    GRANT SELECT, INSERT ON "{options.Schema}".knowledge_revisions TO "{runtimeRole}";
                    GRANT SELECT, INSERT ON "{options.Schema}".memory_provider_bindings TO "{runtimeRole}";
                    GRANT SELECT, INSERT, UPDATE ON "{options.Schema}".knowledge_write_idempotency TO "{runtimeRole}";
                    GRANT SELECT, INSERT, UPDATE ON "{options.Schema}".outbox_streams TO "{runtimeRole}";
                    GRANT SELECT, INSERT, UPDATE ON "{options.Schema}".outbox_events TO "{runtimeRole}";
                    GRANT SELECT ON "{options.Schema}".outbox_schema_migrations TO "{runtimeRole}";
                    GRANT SELECT ON "{options.Schema}".knowledge_schema_migrations TO "{runtimeRole}";
                    """, connection))
                    await grants.ExecuteNonQueryAsync();

                adminConnectionString.Username = runtimeRole;
                adminConnectionString.Password = runtimePassword;
                dataSource = NpgsqlDataSource.Create(adminConnectionString.ConnectionString);
                var database = new KnowledgeTestDatabase(
                    options, adminDataSource, dataSource, runtimeRole,
                    new NativePostgresMemoryProvider(dataSource, options));
                ownsResources = false;
                return database;
            }
            finally
            {
                if (ownsResources)
                {
                    try
                    {
                        if (dataSource is not null)
                            await dataSource.DisposeAsync();
                        await CleanupAsync(adminDataSource, options.Schema, runtimeRole);
                    }
                    finally
                    {
                        await adminDataSource.DisposeAsync();
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DataSource.DisposeAsync();
                await CleanupAsync(AdminDataSource, Options.Schema, runtimeRole);
            }
            finally
            {
                await AdminDataSource.DisposeAsync();
            }
        }

        private static async Task CleanupAsync(
            NpgsqlDataSource adminDataSource,
            string schema,
            string runtimeRole)
        {
            await using var connection = await adminDataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand(
                $"""DROP SCHEMA IF EXISTS "{schema}" CASCADE; DROP ROLE IF EXISTS "{runtimeRole}";""",
                connection);
            await drop.ExecuteNonQueryAsync();
        }
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

    private async Task CreateRunBindingGrantAsync(
        string subject,
        string projectId,
        string runId,
        string? grantId = null)
    {
        using var scope = _brokerFactory.Services.CreateScope();
        var authority = scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>();
        var grant = new SecretRedemptionGrant(
            grantId ?? $"projects-config:{runId}",
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
        return await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(privilegedDataSource), TimeProvider.System)
            .GrantMembershipAsync(new Uri(TestIssuer).AbsoluteUri, subject, tenantId, "local-test-fixture");
    }

    private static async Task<ProjectRoleAssignmentRecord> AssignRoleAsync(
        NpgsqlDataSource privilegedDataSource,
        Guid membershipId,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role)
    {
        return await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(privilegedDataSource), TimeProvider.System)
            .AssignRoleAsync(membershipId, resourceType, resourceId, role, "local-test-fixture");
    }

    private static async Task RevokeRoleAsync(
        NpgsqlDataSource privilegedDataSource,
        Guid assignmentId,
        long expectedRevision)
    {
        await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(privilegedDataSource), TimeProvider.System)
            .RevokeRoleAssignmentAsync(assignmentId, expectedRevision, "local-test-fixture");
    }

    private static async Task<ProjectAuthorizationContextResponse> ReadAuthorizationContextAsync(
        HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<ProjectAuthorizationContextResponse>(
            AuthorizationJsonOptions)
        ?? throw new InvalidOperationException("Projects & Config returned an empty authorization context.");

    private static bool HasPermission(
        ProjectAuthorizationContextResponse context,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorizationPermission permission) =>
        context.EffectiveAuthority.Any(resource =>
            resource.ResourceType == resourceType &&
            resource.ResourceId == resourceId &&
            resource.Permissions.Any(grant => grant.Permission == permission));

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.True(response.Headers.CacheControl?.NoStore ?? false);

    private static ProjectsConfigDbContext CreateDbContext(NpgsqlDataSource dataSource)
    {
        return new ProjectsConfigDbContext(CreateDbContextOptions(dataSource));
    }

    private static DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions(
        NpgsqlDataSource dataSource) =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

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
        string? roleHeader = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);
        AddBearerAndTenant(request, token, tenantSelectors, roleHeader);
        return await client.SendAsync(request, cancellationToken);
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
