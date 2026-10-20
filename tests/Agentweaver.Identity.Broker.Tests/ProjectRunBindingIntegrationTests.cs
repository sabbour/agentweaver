extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    /// <summary>Proves fresh interactive selection supports silent binding and first-message start through the Gateway.</summary>
    [Fact]
    public async Task CreatedChatSilentlyBindsAndStartsWithProjectsOwnerProof()
    {
        var signingKey = new X509SecurityKey(_runBindingSigningCertificate);
        // Owners use TestServer transport; the Broker independently queries the same store over pinned TLS.
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var adminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "chat-admin", null, null, []);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Silent chat");
        using (var defaultsRequest = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings("platform-model"),
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 12, MaxToolCalls = 100, MaxChildren = 4,
                        MaxConcurrentChildren = 2, MaxWallTimeSeconds = 3600, MaxPromptTokens = 20_000,
                    },
                },
            }),
        })
        {
            AddBearerAndTenant(defaultsRequest, adminToken, TenantId);
            using var defaultsResponse = await projects.Client.SendAsync(defaultsRequest);
            Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);
        }

        // Keep this browser's session cookie and permanent consent while the top-level binding changes.
        using var browser = _brokerFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        _fakeIdp.Subject = "chat-orchestrator";
        _fakeIdp.TenantIds = [TenantId];
        _fakeIdp.Roles = [];
        const string scopes = "openid profile email api.read offline_access projects.orchestrator";
        var (unboundVerifier, unboundChallenge) = Pkce.Create();
        var unboundCode = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            browser, _fakeIdpClient, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, scopes, unboundChallenge);
        var unboundTokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, unboundCode, unboundVerifier);
        var unboundToken = unboundTokens.GetProperty("access_token").GetString()!;
        var unboundClaims = new JwtSecurityTokenHandler().ReadJwtToken(unboundToken).Claims;
        Assert.DoesNotContain(unboundClaims, claim => claim.Type is "project_id" or "run_id");
        var subject = SingleClaim(unboundClaims, "sub");
        var orchestratorMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, orchestratorMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);

        var ownerSchema = "chat_owner_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "chat_events_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        using var environment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__Orchestrator"] = _connectionString,
            ["Orchestrator__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["Orchestrator__Schema"] = ownerSchema,
            ["ConnectionStrings__EventsAndSessions"] = _connectionString,
            ["EventsAndSessions__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["EventsAndSessions__Provider__ResourceId"] = "postgres-test",
            ["EventsAndSessions__Provider__DatabaseName"] = databaseName,
            ["EventsAndSessions__Provider__ResourceGeneration"] = "1",
            ["EventsAndSessions__Provider__Schema"] = eventsSchema,
            ["EventsAndSessions__Provider__OptionsRevision"] = "integration-v1",
            ["EventsAndSessions__Messaging__OptionsRevision"] = "integration-messaging-v1",
            ["ProjectsConfig__AuthorizationContext__OwnerBaseAddress"] = "https://projects.test/",
            ["ProjectsConfig__AuthorizationContext__Audience"] = "https://api.test",
            ["EventsAndSessions__Authorization__OwnerBaseAddress"] = "https://events.test/",
            ["EventsAndSessions__Authorization__Audience"] = "https://api.test",
            ["EventsAndSessions__OrchestratorOwner__OwnerBaseAddress"] = "https://orchestrator.test/",
            ["EventsAndSessions__OrchestratorOwner__Audience"] = "https://api.test",
            ["Identity__Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
            ["Identity__Audience"] = "https://api.test",
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsHost::Agentweaver.EventsAndSessions.EventsAndSessionsMigrator
                .MigrateAsync(migrations, eventsSchema);
        }
        EventsIntegrationFactory? eventsReference = null;
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => eventsReference!.Server.CreateHandler());
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler());
        eventsReference = eventsFactory;
        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test"),
        });
        await using var gateway = GatewayProductionResourceServer.Start(
            signingKey, (request, token) => ForwardOwnerRequestAsync(request, projects.Client, token),
            orchestratorOwner: (request, token) => ForwardOwnerRequestAsync(request, orchestrator, token),
            identityAudience: "https://api.test");

        using var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{project.ProjectId}/runs")
        {
            Content = JsonContent.Create(new CreateProjectRunRequest("Start this fresh chat.", project.ConfigurationRevision)),
        };
        AddBearerAndTenant(create, unboundToken, TenantId);
        create.Headers.Add("Idempotency-Key", "silent-chat-create");
        using var created = await gateway.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pending = await created.Content.ReadFromJsonAsync<CreateProjectRunResponse>(OrchestratorJsonOptions);
        Assert.NotNull(pending);
        Assert.Equal("awaiting_binding", pending.State);
        Assert.Equal(project.ProjectId, pending.Binding.ProjectId);
        Assert.Equal(pending.RunId, pending.Binding.RunId);

        // A single authorize request returns directly to the callback, without login or consent UI.
        var promptsBeforeBinding = _fakeIdp.AuthorizationPrompts.Count;
        var (boundVerifier, boundChallenge) = Pkce.Create();
        using var authorize = await BrokerFlowDriver.StartAuthorizeAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, scopes, boundChallenge, "S256",
            state: pending.RunId, projectId: project.ProjectId, runId: pending.RunId, prompt: "none");
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var callback = Assert.IsType<Uri>(authorize.Headers.Location);
        Assert.Equal(IdentityBrokerWebApplicationFactory.TestClientRedirectUri, callback.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.False(query.ContainsKey("error"));
        Assert.False(string.IsNullOrEmpty(query["code"]));
        Assert.Equal(pending.RunId, query["state"].ToString());
        Assert.Equal(promptsBeforeBinding, _fakeIdp.AuthorizationPrompts.Count);
        var boundTokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            browser, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, query["code"].ToString(), boundVerifier);
        var boundToken = boundTokens.GetProperty("access_token").GetString()!;
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(boundToken).Claims;
        Assert.Equal(project.ProjectId, SingleClaim(claims, "project_id"));
        Assert.Equal(pending.RunId, SingleClaim(claims, "run_id"));
        Assert.Equal(BrokerRunBindingAuthority.ProjectsAuthorityKind,
            SingleClaim(claims, BrokerRunBindingAuthority.AuthorityKindClaim));
        Assert.False(string.IsNullOrEmpty(SingleClaim(claims, "aw:run_binding_selection_hash")));

        using var started = await SendAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start", boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var result = await started.Content.ReadFromJsonAsync<StartProjectRunResponse>(OrchestratorJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("started", result.State);
        Assert.Equal(pending.RootSessionId, result.RootSessionId);

        // Replay start and check the real Events journal, not a mocked owner receipt.
        using var replay = await SendAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start", boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using var journal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
            """, connection);
        journal.Parameters.AddWithValue("project", project.ProjectId);
        journal.Parameters.AddWithValue("run", pending.RunId);
        journal.Parameters.AddWithValue("key", $"{pending.RunId}:first");
        Assert.Equal(1L, await journal.ExecuteScalarAsync());
    }
}
