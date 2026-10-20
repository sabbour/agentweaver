extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>Exercises reviewed casting through the Gateway with real Broker tokens and Projects authority.</summary>
public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    /// <summary>Confirmation advances configuration once and stale draft or base revisions remain conflicts.</summary>
    [Fact]
    public async Task GatewayCastingConfirmationAdvancesConfigurationAndRejectsStaleRevisions()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var token = await IssueTokenAsync("projects.admin", [TenantId], "gateway-casting-admin", null, null, ["platform_admin"]);
        var subject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, token, TenantId, "Gateway casting");
        var forwardedTenants = new List<string?>();
        await using var gateway = await GatewayResourceServer.StartAsync(signingKey, projects.CreateHandler,
            (_, _, _) => throw new InvalidOperationException("Casting must call Projects only."),
            request =>
            {
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants);
                forwardedTenants.Add(tenants?.SingleOrDefault());
                Assert.Equal("Bearer " + token, request.Headers.Authorization?.ToString());
            });
        var prefix = $"/api/v1/projects/{project.ProjectId}";
        using var templatesResponse = await SendAsync(gateway.Client, HttpMethod.Get, "/api/v1/casting/templates", token, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, templatesResponse.StatusCode);
        var templates = await templatesResponse.Content.ReadFromJsonAsync<ProjectCastingTemplateDefinition[]>(AuthorizationJsonOptions)
            ?? throw new InvalidOperationException("Projects returned no casting templates.");
        Assert.NotEmpty(templates);
        using var rolesResponse = await SendAsync(gateway.Client, HttpMethod.Get, "/api/v1/catalog/roles", token, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
        Assert.NotEmpty((await rolesResponse.Content.ReadFromJsonAsync<ProjectCastingRoleDefinition[]>(AuthorizationJsonOptions))!);

        // Two drafts share a base revision so the second becomes stale after the first is confirmed.
        var create = new CreateScenarioProjectCastingProposalRequest
        {
            ExpectedConfigurationRevision = project.ConfigurationRevision,
            TemplateId = templates[0].Id,
        };
        using var createdResponse = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post, prefix + "/casting/proposals/scenario", token, create);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var proposal = (await createdResponse.Content.ReadFromJsonAsync<ProjectCastingProposal>(AuthorizationJsonOptions))!;
        Assert.Equal(ProjectCastingProposalState.Pending, proposal.State);
        using var secondResponse = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post, prefix + "/casting/proposals/scenario", token, create);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var second = (await secondResponse.Content.ReadFromJsonAsync<ProjectCastingProposal>(AuthorizationJsonOptions))!;
        var proposalPath = prefix + "/casting/proposals/" + proposal.ProposalId;
        using var staleDraft = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post, proposalPath + "/confirm", token,
            new TransitionProjectCastingProposalRequest { ExpectedDraftRevision = proposal.DraftRevision + 1 });
        Assert.Equal(HttpStatusCode.Conflict, staleDraft.StatusCode);
        using var confirmedResponse = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post, proposalPath + "/confirm", token,
            new TransitionProjectCastingProposalRequest { ExpectedDraftRevision = proposal.DraftRevision });
        Assert.Equal(HttpStatusCode.OK, confirmedResponse.StatusCode);
        var confirmed = (await confirmedResponse.Content.ReadFromJsonAsync<ProjectCastingProposal>(AuthorizationJsonOptions))!;
        Assert.Equal(ProjectCastingProposalState.Confirmed, confirmed.State);
        Assert.Equal(project.ConfigurationRevision + 1, confirmed.ConfirmedConfigurationRevision);
        using var configurationResponse = await SendAsync(gateway.Client, HttpMethod.Get, prefix + "/configuration", token, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        var configuration = (await configurationResponse.Content.ReadFromJsonAsync<VersionedProjectConfiguration>(AuthorizationJsonOptions))!;
        Assert.Equal(project.ConfigurationRevision + 1, configuration.Revision);
        // Records with nested immutable arrays compare by reference, so compare their wire form.
        Assert.Equal(
            JsonSerializer.Serialize(proposal.Draft.Casting, AuthorizationJsonOptions),
            JsonSerializer.Serialize(configuration.Configuration.Casting, AuthorizationJsonOptions));
        using var staleBase = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post,
            prefix + "/casting/proposals/" + second.ProposalId + "/confirm", token,
            new TransitionProjectCastingProposalRequest { ExpectedDraftRevision = second.DraftRevision });
        Assert.Equal(HttpStatusCode.Conflict, staleBase.StatusCode);
        using var staleCreate = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post, prefix + "/casting/proposals/scenario", token, create);
        Assert.Equal(HttpStatusCode.Conflict, staleCreate.StatusCode);
        using var finalConfiguration = await SendAsync(gateway.Client, HttpMethod.Get, prefix + "/configuration", token, [TenantId]);
        Assert.Equal(project.ConfigurationRevision + 1,
            (await finalConfiguration.Content.ReadFromJsonAsync<VersionedProjectConfiguration>(AuthorizationJsonOptions))!.Revision);
        Assert.All(forwardedTenants, tenant => Assert.Equal(TenantId, tenant));
    }

    /// <summary>Preserves real owner denials for a project Viewer and hides proposals in an ungranted project.</summary>
    [Fact]
    public async Task GatewayCastingPreservesOwnerAuthorizationForReadOnlyAndUngrantedProjects()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var adminToken = await IssueTokenAsync("projects.admin", [TenantId], "casting-authorization-admin", null, null, ["platform_admin"]);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Viewer casting");
        var otherProject = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Ungranted casting");
        using var created = await SendCastingJsonAsync(projects.Client, HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/casting/proposals/manual", adminToken,
            new CreateManualProjectCastingProposalRequest
            {
                ExpectedConfigurationRevision = project.ConfigurationRevision,
                RoleIds = ["lead-pm"],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var proposal = (await created.Content.ReadFromJsonAsync<ProjectCastingProposal>(AuthorizationJsonOptions))!;
        using var exported = await SendAsync(projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/casting/export", adminToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var transfer = (await exported.Content.ReadFromJsonAsync<ProjectCastingTransfer>(AuthorizationJsonOptions))!;

        // Retain the write scope so denial proves missing owner authority, not merely a missing token scope.
        var viewerToken = await IssueTokenAsync("projects.admin", [TenantId], "casting-authorization-viewer", null, null, []);
        var viewerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(viewerToken).Claims, "sub");
        var viewerMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, viewerSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, viewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Viewer);
        var ownerStatuses = new List<HttpStatusCode>();
        await using var gateway = await GatewayResourceServer.StartAsync(signingKey,
            () => new CastingOwnerStatusHandler(projects.CreateHandler(), ownerStatuses),
            (_, _, _) => throw new InvalidOperationException("Casting must call Projects only."));
        using var contextResponse = await SendAsync(gateway.Client, HttpMethod.Get,
            "/api/v1/authorization/context", viewerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        var context = await ReadAuthorizationContextAsync(contextResponse);
        Assert.True(HasPermission(context, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.ReadProjects));
        Assert.False(HasPermission(context, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorizationPermission.WriteProjects));
        Assert.False(HasPermission(context, ProjectAuthorityResourceType.Project, otherProject.ProjectId,
            ProjectAuthorizationPermission.ReadProjects));
        ownerStatuses.Clear();

        using var readable = await SendAsync(gateway.Client, HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/casting/proposals", viewerToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, readable.StatusCode);
        Assert.Contains((await readable.Content.ReadFromJsonAsync<ProjectCastingProposal[]>(AuthorizationJsonOptions))!,
            item => item.ProposalId == proposal.ProposalId);
        using var confirm = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/casting/proposals/{proposal.ProposalId}/confirm", viewerToken,
            new TransitionProjectCastingProposalRequest { ExpectedDraftRevision = proposal.DraftRevision });
        using var import = await SendCastingJsonAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/casting/import", viewerToken,
            new ImportProjectCastingTransferRequest
            {
                ExpectedConfigurationRevision = project.ConfigurationRevision,
                Transfer = transfer,
            });
        using var ungranted = await SendAsync(gateway.Client, HttpMethod.Get,
            $"/api/v1/projects/{otherProject.ProjectId}/casting/proposals", viewerToken, [TenantId]);

        // Observe responses inside the owner handler to distinguish owner denials from Gateway rejection.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.NotFound],
            ownerStatuses.ToArray());
        Assert.Equal(ownerStatuses[0], readable.StatusCode);
        Assert.Equal(ownerStatuses[1], confirm.StatusCode);
        Assert.Equal(ownerStatuses[2], import.StatusCode);
        Assert.Equal(ownerStatuses[3], ungranted.StatusCode);
    }

    /// <summary>The direct owner enforces its import limit during Kestrel request binding, before authority checks.</summary>
    [Fact]
    public async Task GatewayCastingImportOwnerEnforcesBodyLimitOnKestrel()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), useKestrel: true);
        var token = await IssueTokenAsync("projects.admin", [TenantId], "casting-import-limit", null, null, []);
        const string json = """
            {"expectedConfigurationRevision":1,"transfer":{"formatVersion":1,"sourceProjectId":"11111111111111111111111111111111","sourceConfigurationRevision":1,"agentCharters":[],"casting":[],"contentDigest":""}}
            """;
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var body = json + new string(' ', 1_048_576 + offset - Encoding.UTF8.GetByteCount(json));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/ungranted/casting/import")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            Assert.Equal(1_048_576 + offset, request.Content.Headers.ContentLength);
            AddBearerAndTenant(request, token, [TenantId]);
            using var response = await projects.Client.SendAsync(request);
            // In-bound bodies reach owner authorization; oversized bodies must fail before it.
            Assert.Equal(offset > 0 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>Records the real Projects response before the Gateway can transform it.</summary>
    private sealed class CastingOwnerStatusHandler(HttpMessageHandler innerHandler, List<HttpStatusCode> statuses)
        : DelegatingHandler(innerHandler)
    {
        /// <summary>Passes requests to Projects and retains its actual authorization result.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            statuses.Add(response.StatusCode);
            return response;
        }
    }

    /// <summary>Sends a typed casting request with the Broker bearer and explicit tenant selector.</summary>
    private static async Task<HttpResponseMessage> SendCastingJsonAsync<T>(
        HttpClient client, HttpMethod method, string path, string token, T body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: AuthorizationJsonOptions) };
        AddBearerAndTenant(request, token, [TenantId]);
        return await client.SendAsync(request);
    }
}
