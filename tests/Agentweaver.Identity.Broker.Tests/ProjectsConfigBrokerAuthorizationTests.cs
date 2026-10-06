extern alias ProjectsConfig;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.IdentityModel.Tokens.Jwt;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class ProjectsConfigBrokerAuthorizationTests(PostgresContainerFixture postgres)
    : IAsyncLifetime
{
    private static readonly string[] ProjectScopes =
    [
        "projects.owner",
        "projects.admin",
        "projects.orchestrator",
        "projects.other-tenant",
        "projects.no-tenant",
        "projects.ambiguous-tenant",
        "projects.malformed-tenant",
        "projects.untrusted-role",
    ];

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
    public async Task ProjectsApi_AuthorizesOnlyTrustedBrokerTenantRolesAndAudience()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));

        var ownerTokens = await IssueTokensAsync("projects.owner", ["tenant-1"]);
        var ownerToken = ownerTokens.AccessToken;
        var ownerProfile = new JwtSecurityTokenHandler().ReadJwtToken(ownerToken);
        Assert.Contains(ownerProfile.Claims,
            claim => claim.Type == IdentityAuthorizationContext.TenantIdClaimType && claim.Value == "tenant-1");
        var ownerIdToken = new JwtSecurityTokenHandler().ReadJwtToken(ownerTokens.IdToken);
        Assert.DoesNotContain(ownerIdToken.Claims,
            claim => claim.Type is IdentityAuthorizationContext.TenantIdClaimType or IdentityAuthorizationContext.RoleClaimType);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Tenant one project" }),
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        using var created = await projects.Client.SendAsync(createRequest);
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"Projects API rejected the broker token: {created.StatusCode}; " +
            $"challenge={string.Join(", ", created.Headers.WwwAuthenticate)}; " +
            $"body={await created.Content.ReadAsStringAsync()}");
        var project = await created.Content.ReadFromJsonAsync<ProjectSummary>();
        Assert.NotNull(project);

        using var ownerRead = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        using var ownerCannotReadPlatformDefaults = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", ownerToken);
        Assert.Equal(HttpStatusCode.Forbidden, ownerCannotReadPlatformDefaults.StatusCode);

        var adminToken = await IssueTokenAsync("projects.admin", ["tenant-1"], "platform_admin");
        Assert.Contains(
            new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims,
            claim => claim.Type == IdentityAuthorizationContext.RoleClaimType &&
                claim.Value == IdentityAuthorizationContext.PlatformAdminRole);
        using var adminRead = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", adminToken);
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);

        var adminScopeWithoutRole = await IssueTokenAsync("projects.admin", ["tenant-1"]);
        Assert.DoesNotContain(
            new JwtSecurityTokenHandler().ReadJwtToken(adminScopeWithoutRole).Claims,
            claim => claim.Type == IdentityAuthorizationContext.RoleClaimType);
        using var adminScopeCannotRead = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", adminScopeWithoutRole);
        Assert.Equal(HttpStatusCode.Forbidden, adminScopeCannotRead.StatusCode);

        var orchestratorToken = await IssueTokenAsync("projects.orchestrator", ["tenant-1"], "orchestrator");
        Assert.Contains(
            new JwtSecurityTokenHandler().ReadJwtToken(orchestratorToken).Claims,
            claim => claim.Type == IdentityAuthorizationContext.RoleClaimType &&
                claim.Value == IdentityAuthorizationContext.OrchestratorRole);
        using var missingSelection = await SendAsync(
            projects.Client,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/not-created/selection",
            orchestratorToken);
        Assert.Equal(HttpStatusCode.NotFound, missingSelection.StatusCode);

        var otherTenantAdmin = await IssueTokenAsync(
            "projects.other-tenant", ["tenant-2"], "platform_admin");
        using var crossTenantRead = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", otherTenantAdmin);
        Assert.Equal(HttpStatusCode.NotFound, crossTenantRead.StatusCode);

        var missingTenantAdmin = await IssueTokenAsync(
            "projects.no-tenant", [], "platform_admin");
        using var missingTenant = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", missingTenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, missingTenant.StatusCode);

        var ambiguousTenantAdmin = await IssueTokenAsync(
            "projects.ambiguous-tenant", ["tenant-1", "tenant-2"], "platform_admin");
        using var ambiguousTenant = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", ambiguousTenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, ambiguousTenant.StatusCode);

        var malformedTenantAdmin = await IssueTokenAsync(
            "projects.malformed-tenant", ["tenant-1/tenant-2"], "platform_admin");
        Assert.DoesNotContain(
            new JwtSecurityTokenHandler().ReadJwtToken(malformedTenantAdmin).Claims,
            claim => claim.Type == IdentityAuthorizationContext.TenantIdClaimType);
        using var malformedTenant = await SendAsync(
            projects.Client, HttpMethod.Get, "/api/platform/runtime-defaults", malformedTenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, malformedTenant.StatusCode);

        var untrustedRole = await IssueTokenAsync(
            "projects.untrusted-role", ["tenant-1"], "support_admin");
        Assert.DoesNotContain(
            new JwtSecurityTokenHandler().ReadJwtToken(untrustedRole).Claims,
            claim => claim.Type == IdentityAuthorizationContext.RoleClaimType);
        using var untrustedRoleRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/platform/runtime-defaults");
        untrustedRoleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", untrustedRole);
        untrustedRoleRequest.Headers.Add("X-Role", "platform_admin");
        using var untrustedRoleResponse = await projects.Client.SendAsync(untrustedRoleRequest);
        Assert.Equal(HttpStatusCode.Forbidden, untrustedRoleResponse.StatusCode);

        await using var wrongAudience = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), "https://other-api.test");
        using var wrongResource = await SendAsync(
            wrongAudience.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}", ownerToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResource.StatusCode);
    }

    private async Task<string> IssueTokenAsync(
        string additionalScope, IReadOnlyList<string> tenantIds, params string[] roles)
    {
        var tokens = await IssueTokensAsync(additionalScope, tenantIds, roles);
        return tokens.AccessToken;
    }

    private async Task<(string AccessToken, string IdToken)> IssueTokensAsync(
        string additionalScope, IReadOnlyList<string> tenantIds, params string[] roles)
    {
        _fakeIdp.Subject = $"external-subject-{Interlocked.Increment(ref _subjectSequence)}";
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
            challenge);
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        return (
            tokens.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("The Identity broker did not return an access token."),
            tokens.GetProperty("id_token").GetString()
                ?? throw new InvalidOperationException("The Identity broker did not return an ID token."));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
