extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
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

    /// <summary>Sends a typed casting request with the Broker bearer and explicit tenant selector.</summary>
    private static async Task<HttpResponseMessage> SendCastingJsonAsync<T>(
        HttpClient client, HttpMethod method, string path, string token, T body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: AuthorizationJsonOptions) };
        AddBearerAndTenant(request, token, [TenantId]);
        return await client.SendAsync(request);
    }
}
