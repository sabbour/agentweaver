extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using MAFCheckpointing = Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private const string ReceiptActionId = "propose_work_plan";
    private const string ReceiptAllowPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: platform-policy
        scope: global
        default_action: deny
        rules:
          - name: allow-coordinator-action
            condition: "action_id == 'propose_work_plan'"
            action: allow
            priority: 100
        """;
    private const string SourceControlMergeAllowPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: source-control-merge-policy
        scope: global
        default_action: deny
        rules:
          - name: allow-approved-source-control-merge
            condition: "action_id == 'source_control.merge'"
            action: allow
            priority: 100
        """;

    private static readonly JsonSerializerOptions CoordinationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending()
    {
        var sourceControlSecretBackend = new RecordingSecretRedemption();
        await RestartBrokerForSourceControlAsync(sourceControlSecretBackend);
        using var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(signingCertificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            signingKey,
            providerCatalog: CreateSourceControlProviderCatalog());

        var platformAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "coordination-admin", null, null, ["platform_admin"]);
        var platformAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(platformAdminToken).Claims, "sub");
        var platformAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, platformAdminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            platformAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

        using var createProject = new HttpRequestMessage(HttpMethod.Post, "/api/projects/")
        {
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Coordination integration" })
        };
        AddBearerAndTenant(createProject, platformAdminToken, TenantId);
        using var createdProject = await projects.Client.SendAsync(createProject);
        Assert.Equal(HttpStatusCode.Created, createdProject.StatusCode);
        var project = await createdProject.Content.ReadFromJsonAsync<ProjectSummary>(AuthorizationJsonOptions);
        Assert.NotNull(project);

        var bootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "coordination-runner", null, null, ["orchestrator"]);
        var runnerSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(bootstrapToken).Claims, "sub");
        var runnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, runnerSubject, TenantId);
        var runnerRole = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        await CreateRunBindingGrantAsync(runnerSubject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "coordination-runner",
            project.ProjectId,
            RunId,
            ["platform_admin"]);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(runToken).Claims.ToArray();
        var currentRunnerSubject = SingleClaim(claims, "sub");
        Assert.Contains(claims, claim => claim.Type == "aud" && claim.Value == "https://api.test");
        Assert.Contains(
            claims,
            claim => claim.Type == "aud" && claim.Value == "https://broker-redemption.test");
        Assert.Contains(claims, claim => claim.Type == "project_id" && claim.Value == project.ProjectId);
        Assert.Contains(claims, claim => claim.Type == "run_id" && claim.Value == RunId);
        await CreateSourceControlSecretGrantAsync(
            currentRunnerSubject,
            project.ProjectId,
            RunId,
            new SecretRef("github-api", "api-v1"),
            SourceControlSecretPurposes.Api);
        await CreateSourceControlSecretGrantAsync(
            currentRunnerSubject,
            project.ProjectId,
            RunId,
            new SecretRef("github-checkout", "checkout-v1"),
            SourceControlSecretPurposes.Checkout);
        await CreateSourceControlSecretGrantAsync(
            currentRunnerSubject,
            project.ProjectId,
            RunId,
            new SecretRef("github-webhook", "webhook-v1"),
            SourceControlSecretPurposes.Webhook);

        using var updateProjectConfiguration = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/configuration")
        {
            Content = JsonContent.Create(new UpdateProjectConfigurationRequest
            {
                ExpectedRevision = project.ConfigurationRevision,
                Configuration = new ProjectConfiguration
                {
                    AgentCharters =
                    [
                        new ProjectAgentCharter(
                            "test-agent", "Test agent", "implementer", "Implements the accepted work item.")
                    ],
                    Casting = [new ProjectAgentCast("test-agent", "implementer", 0)],
                    SourceControl = new SourceControlProjectSettings(
                        new SourceControlRepositoryIdentity("octo", "agentweaver"),
                        new SecretRef("github-api", "api-v1"),
                        checkoutSecretReference: new SecretRef("github-checkout", "checkout-v1"),
                        webhookSecretReference: new SecretRef("github-webhook", "webhook-v1"))
                }
            })
        };
        AddBearerAndTenant(updateProjectConfiguration, platformAdminToken, TenantId);
        using var updatedProjectConfiguration =
            await projects.Client.SendAsync(updateProjectConfiguration);
        Assert.Equal(HttpStatusCode.OK, updatedProjectConfiguration.StatusCode);
        var projectConfiguration =
            await updatedProjectConfiguration.Content.ReadFromJsonAsync<VersionedProjectConfiguration>(
                AuthorizationJsonOptions);
        Assert.NotNull(projectConfiguration);

        using var updateDefaults = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
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
                        MaxModelTurns = 12,
                        MaxToolCalls = 100,
                        MaxChildren = 4,
                        MaxConcurrentChildren = 2,
                        MaxWallTimeSeconds = 3600,
                        MaxPromptTokens = 20000
                    }
                }
            })
        };
        AddBearerAndTenant(updateDefaults, platformAdminToken, TenantId);
        using var updatedDefaults = await projects.Client.SendAsync(updateDefaults);
        Assert.Equal(HttpStatusCode.OK, updatedDefaults.StatusCode);

        var acceptedSelectionRequest = new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = projectConfiguration.Revision,
            ExpectedPlatformRuntimeRevision = 1,
            Context = new RunSelectionContext
            {
                Revision = "provider-catalog-v1",
                AvailableModelSelectionReferences = ImmutableHashSet.Create(
                    StringComparer.Ordinal, "platform-model"),
                ProviderRequirements =
                [
                    new ProviderRequirement
                    {
                        Seam = ProviderSeam.Sandbox,
                        RequiredAdapterVersion = "1.0.0",
                        RequiredOptionsSchemaVersion = 1,
                        RequiredCapabilities = ImmutableHashSet.Create(
                            StringComparer.Ordinal, "container.create")
                    },
                    new ProviderRequirement
                    {
                        Seam = ProviderSeam.SourceControl,
                        RequiredAdapterVersion = "1.0.0",
                        RequiredOptionsSchemaVersion = GitHubSourceControlAdapter.CurrentOptionsSchemaVersion,
                        RequiredCapabilities = ImmutableHashSet.Create(
                            StringComparer.Ordinal,
                            SourceControlCapabilities.RepositoryRead,
                            SourceControlCapabilities.IssueWrite,
                            SourceControlCapabilities.PullRequestWrite,
                            SourceControlCapabilities.ReviewRead,
                            SourceControlCapabilities.RepositoryCheckout,
                            SourceControlCapabilities.Merge)
                    },
                    new ProviderRequirement
                    {
                        Seam = ProviderSeam.Policy,
                        RequiredAdapterVersion = AgtPolicyProvider.AdapterVersion.ToString(),
                        RequiredOptionsSchemaVersion = AgtPolicyProvider.OptionsSchemaVersion,
                        RequiredCapabilities = PolicyProviderCapabilities.All
                    }
                ]
            }
        };
        using var acceptSelection = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(acceptedSelectionRequest)
        };
        AddBearerAndTenant(acceptSelection, runToken, TenantId);
        using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
        Assert.Equal(HttpStatusCode.OK, acceptedSelection.StatusCode);
        var acceptedProjectsSelection = await acceptedSelection.Content.ReadAsStringAsync();
        var acceptedProjectsSelectionHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(acceptedProjectsSelection)));
        using (var changedSelectionPut = await SendJsonAsync(
                   projects.Client,
                   HttpMethod.Put,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/selection",
                   runToken,
                   acceptedSelectionRequest with
                   {
                       Context = acceptedSelectionRequest.Context with
                       {
                           Revision = "provider-catalog-v2"
                       }
                   }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedSelectionPut.StatusCode);
        }
        using (var stableProjectsSelection = await SendAsync(
                   projects.Client,
                   HttpMethod.Get,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/selection",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, stableProjectsSelection.StatusCode);
            var stableSelectionJson = await stableProjectsSelection.Content.ReadAsStringAsync();
            Assert.Equal(acceptedProjectsSelection, stableSelectionJson);
            Assert.Equal(
                acceptedProjectsSelectionHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableSelectionJson))));
        }

        var ownerSchema = "coordination_it_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "events_it_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        using var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
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
            ["Identity__SecretRedemption__Audience"] = "https://broker-redemption.test",
            ["Identity__SecretRedemption__OwnerBaseAddress"] = "https://broker.test.local/"
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsAndSessionsMigrator.MigrateAsync(migrations, eventsSchema);
        }
        EventsIntegrationFactory? eventsFactoryReference = null;
        var projectsOwnerRequests = 0;
        var admissions = new List<(HttpStatusCode Status, bool NoStore, string Body)>();
        var forkAdmissionGate = new ForkAdmissionGate();
        var ownerForkRaceGate = new OwnerForkRaceGate();
        var cacheObjectStore = new InMemoryObjectStore();
        var sandboxProvider = new ControlledSandboxResourceProvider();
        var sourceControlGitHub = new ControlledGitHubApi(sourceControlSecretBackend.Value);
        var sourceControlRequestBarrier = new ControlledRequestBarrier();
        var mergeStartedSelectionBarrier = new ControlledRequestBarrier();
        sourceControlGitHub.BeforeResponseAsync = sourceControlRequestBarrier.PauseIfMatchedAsync;
        using var workspaceFiles = new SourceControlTemporaryDirectory();
        var checkoutRepositoryPath = Path.Combine(workspaceFiles.Path, "checkout-origin");
        var checkoutBaseSha = await CreateGitRepositoryAsync(checkoutRepositoryPath);
        var gitWorkspaceManager = new GitWorkspaceManager(
            Path.Combine(workspaceFiles.Path, "owned-workspaces"),
            new LocalGitRepositoryRemote(new Uri(checkoutRepositoryPath)));
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString,
            ownerSchema,
            signingKey,
            () => new RequestCountingHandler(
                projects.CreateHandler(),
                () => Interlocked.Increment(ref projectsOwnerRequests),
                mergeStartedSelectionBarrier.PauseIfMatchedAsync),
            () => new OwnerForkRaceHandler(
                new CapturingHandler(
                    eventsFactoryReference!.Server.CreateHandler(),
                    (status, noStore, body) => admissions.Add((status, noStore, body))),
                ownerForkRaceGate),
            cacheObjectStore,
            sandboxProvider,
            () => _brokerFactory.Server.CreateHandler(),
            sourceControlGitHub.CreateHandler,
            CreateSourceControlProviderCatalog(),
            policyOptions: SourceControlMergePolicyOptions(),
            gitWorkspaceManager: gitWorkspaceManager);
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString,
            eventsSchema,
            signingKey,
            projects.CreateHandler,
            () => new ForkAdmissionBarrierHandler(
                orchestratorFactory.Server.CreateHandler(), forkAdmissionGate));
        eventsFactoryReference = eventsFactory;

        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test")
        });
        using var events = eventsFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://events.test")
        });

        using var rootResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/root",
            runToken,
            new AcceptRootRequest("root"));
        await AssertStatusAsync(rootResponse, HttpStatusCode.Created);
        var root = await ReadJsonAsync<AcceptedRoot>(rootResponse);
        Assert.Equal("root", root.RootSessionId);
        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT issuer, actor_id, tenant_id, session_id, step_id, purpose,
                   execution_fence, source_state_version
            FROM "{ownerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND is_current AND grant_state = 'active'
              AND action_ids ? 'propose_outcome_spec'
            """, connection))
        {
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("session", root.RootSessionId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri, reader.GetString(0));
            Assert.Equal(currentRunnerSubject, reader.GetString(1));
            Assert.Equal(TenantId, reader.GetString(2));
            Assert.Equal(root.RootSessionId, reader.GetString(3));
            Assert.Equal("outcome", reader.GetString(4));
            Assert.Equal("coordinator.typed-decision", reader.GetString(5));
            Assert.Equal(root.ExecutionFence, reader.GetInt64(6));
            Assert.Equal(1, reader.GetInt64(7));
            Assert.False(await reader.ReadAsync());
        }

        const string sourceControlBrokerAudience = "https://broker-redemption.test";
        var decisionStatePath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions";
        var ownerEffectsBeforeAudienceChecks = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        var ownerRequestsBeforeAudienceChecks = Volatile.Read(ref projectsOwnerRequests);
        var brokerOnlyToken = ReissueTokenWithAudiences(runToken, sourceControlBrokerAudience);
        using (var brokerOnlyResponse = await SendAsync(
                   orchestrator,
                   HttpMethod.Get,
                   decisionStatePath,
                   brokerOnlyToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, brokerOnlyResponse.StatusCode);
        }
        Assert.Equal(ownerRequestsBeforeAudienceChecks, Volatile.Read(ref projectsOwnerRequests));
        Assert.Equal(
            ownerEffectsBeforeAudienceChecks,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));

        var dualAudienceToken = ReissueTokenWithAudiences(
            runToken, "https://api.test", sourceControlBrokerAudience);
        using (var dualAudienceResponse = await SendAsync(
                   orchestrator,
                   HttpMethod.Get,
                   decisionStatePath,
                   dualAudienceToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, dualAudienceResponse.StatusCode);
        }
        Assert.True(Volatile.Read(ref projectsOwnerRequests) > ownerRequestsBeforeAudienceChecks);
        Assert.Equal(
            ownerEffectsBeforeAudienceChecks,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));

        var ownerRequestsBeforeDirectWebhookChecks = Volatile.Read(ref projectsOwnerRequests);
        var ownerEffectsBeforeDirectWebhookChecks = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        using (var unauthenticatedWebhook = new HttpRequestMessage(
                   HttpMethod.Post, "/api/source-control/github/webhook")
               {
                   Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"action\":\"opened\"}"))
               })
        using (var unauthenticatedWebhookResponse =
               await orchestrator.SendAsync(unauthenticatedWebhook))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedWebhookResponse.StatusCode);
        }
        using (var authenticatedDirectWebhookResponse = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   "/api/source-control/github/webhook",
                   dualAudienceToken,
                   new { action = "opened" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, authenticatedDirectWebhookResponse.StatusCode);
        }
        Assert.Equal(ownerRequestsBeforeDirectWebhookChecks, Volatile.Read(ref projectsOwnerRequests));
        Assert.Equal(
            ownerEffectsBeforeDirectWebhookChecks,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));

        var issuePath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/source-control/sessions/{root.RootSessionId}/issues";
        using (var brokerOnlyIssueResponse = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   issuePath,
                   brokerOnlyToken,
                   new SourceControlIssueRequest("", null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, brokerOnlyIssueResponse.StatusCode);
        }
        using (var invalidIssueResponse = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   issuePath,
                   dualAudienceToken,
                   new SourceControlIssueRequest("", null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidIssueResponse.StatusCode);
        }
        Assert.Equal(ownerRequestsBeforeDirectWebhookChecks, Volatile.Read(ref projectsOwnerRequests));
        Assert.Equal(
            ownerEffectsBeforeDirectWebhookChecks,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));

        var sourceControlBasePath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/source-control/sessions/{root.RootSessionId}";
        using (var pinResponse = await SendAsync(
                   orchestrator,
                   HttpMethod.Post,
                   sourceControlBasePath + "/pin",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.Accepted, pinResponse.StatusCode);
        }
        Assert.Contains(
            sourceControlSecretBackend.Requests,
            request => request.Secret.Id == "github-api" &&
                request.Secret.Version == "api-v1" &&
                request.Purpose == SourceControlSecretPurposes.Api &&
                request.RunId == RunId);

        const string sourceControlWorkspaceId = "source-control-owner-proof";
        const string sourceControlWorkspaceBranch = "agent/source-control-owner";
        string sourceControlWorkspacePath;
        using (var prepareWorkspace = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   sourceControlBasePath + "/workspaces",
                   runToken,
                   new OrchestratorHost::Agentweaver.Orchestrator.PrepareSourceControlWorkspaceRequest(
                       sourceControlWorkspaceId,
                       checkoutBaseSha,
                       sourceControlWorkspaceBranch)))
        {
            Assert.Equal(HttpStatusCode.Accepted, prepareWorkspace.StatusCode);
            var workspaceBody = await prepareWorkspace.Content.ReadAsStringAsync();
            Assert.DoesNotContain(sourceControlSecretBackend.Value, workspaceBody, StringComparison.Ordinal);
            using var workspaceDocument = JsonDocument.Parse(workspaceBody);
            var workspace = workspaceDocument.RootElement;
            Assert.Equal(sourceControlWorkspaceId, workspace.GetProperty("workspaceId").GetString());
            Assert.Equal(checkoutBaseSha, workspace.GetProperty("baseSha").GetString());
            Assert.Equal(sourceControlWorkspaceBranch, workspace.GetProperty("branchName").GetString());
            sourceControlWorkspacePath = workspace.GetProperty("workspacePath").GetString()
                ?? throw new InvalidOperationException("The authenticated workspace path was missing.");
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(sourceControlWorkspacePath)!, "workspace.json")));
            Assert.Equal(
                new Uri(checkoutRepositoryPath).AbsoluteUri,
                (await RunFixtureGitAsync(
                    sourceControlWorkspacePath, ["remote", "get-url", "origin"])).Trim());
            await File.WriteAllTextAsync(
                Path.Combine(sourceControlWorkspacePath, "README.md"),
                "updated through the authorized owner route\n");
            await File.WriteAllTextAsync(
                Path.Combine(sourceControlWorkspacePath, "untracked.txt"),
                "owned temporary workspace\n");

            var workspaceCredential = sourceControlSecretBackend.LastCredential;
            Assert.NotNull(workspaceCredential);
            Assert.InRange(
                workspaceCredential.ExpiresAt - DateTimeOffset.UtcNow,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(3));
            Assert.Throws<InvalidOperationException>(() => workspaceCredential.GetValue());
            Assert.Contains(
                sourceControlSecretBackend.Requests,
                request => request.Secret.Id == "github-checkout" &&
                    request.Secret.Version == "checkout-v1" &&
                    request.Purpose == SourceControlSecretPurposes.Checkout &&
                    request.RunId == RunId);
        }

        using (var readWorkspaceDiff = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   sourceControlBasePath + "/workspaces/" + sourceControlWorkspaceId + "/diff",
                   runToken,
                   new OrchestratorHost::Agentweaver.Orchestrator.PrepareSourceControlWorkspaceRevisionRequest(
                       checkoutBaseSha,
                       sourceControlWorkspaceBranch)))
        {
            Assert.Equal(HttpStatusCode.OK, readWorkspaceDiff.StatusCode);
            var diffBody = await readWorkspaceDiff.Content.ReadAsStringAsync();
            Assert.DoesNotContain(sourceControlSecretBackend.Value, diffBody, StringComparison.Ordinal);
            using var diffDocument = JsonDocument.Parse(diffBody);
            Assert.Equal(
                checkoutBaseSha,
                diffDocument.RootElement.GetProperty("baseSha").GetString());
            Assert.Contains(
                "updated through the authorized owner route",
                diffDocument.RootElement.GetProperty("patch").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "untracked.txt",
                diffDocument.RootElement.GetProperty("patch").GetString(),
                StringComparison.Ordinal);
            var checkoutCredential = sourceControlSecretBackend.LastCredential;
            Assert.NotNull(checkoutCredential);
            Assert.Throws<InvalidOperationException>(() => checkoutCredential.GetValue());
            Assert.DoesNotContain(
                sourceControlSecretBackend.Value,
                await File.ReadAllTextAsync(Path.Combine(
                    Path.GetDirectoryName(sourceControlWorkspacePath)!, "workspace.json")),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                sourceControlSecretBackend.Value,
                await File.ReadAllTextAsync(Path.Combine(
                    sourceControlWorkspacePath, ".git", "config")),
                StringComparison.Ordinal);
        }

        using (var issueResponse = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   sourceControlBasePath + "/issues",
                   runToken,
                   new SourceControlIssueRequest("Integration issue", "Created through the authorized owner route.")))
        {
            Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
            var issue = await ReadJsonAsync<SourceControlIssue>(issueResponse);
            Assert.Equal(41, issue.Number);
            Assert.Equal("Integration issue", issue.Title);
        }

        const string sourceHeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sourceBaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (var pullRequestResponse = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   sourceControlBasePath + "/pull-requests",
                   runToken,
                   new SourceControlPullRequestRequest(
                       "Integration change",
                       "Created through the authorized owner route.",
                       "feature/source-control",
                       sourceHeadSha,
                       "main",
                       sourceBaseSha,
                       Draft: false)))
        {
            Assert.Equal(HttpStatusCode.OK, pullRequestResponse.StatusCode);
            var pullRequest = await ReadJsonAsync<SourceControlPullRequest>(pullRequestResponse);
            Assert.Equal(17, pullRequest.Number);
            Assert.Equal(sourceHeadSha, pullRequest.HeadSha);
            Assert.Equal(sourceBaseSha, pullRequest.BaseSha);
            Assert.Equal(SourceControlPullRequestDisposition.Reused, pullRequest.Disposition);
        }

        using (var reviewsResponse = await SendAsync(
                   orchestrator,
                   HttpMethod.Get,
                   sourceControlBasePath + "/pull-requests/17/reviews",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, reviewsResponse.StatusCode);
            var reviews = await ReadJsonAsync<SourceControlReview[]>(reviewsResponse);
            Assert.Collection(
                reviews,
                review =>
                {
                    Assert.Equal(11, review.Id);
                    Assert.Equal("reviewer", review.Reviewer);
                    Assert.Equal(SourceControlReviewState.Approved, review.State);
                });
        }

        var webhookPath = sourceControlBasePath + "/webhook-relay";
        var webhookDeliveryId = Guid.NewGuid().ToString("D");
        var webhookBody = Encoding.UTF8.GetBytes(
            "{\"repository\":{\"id\":12345,\"full_name\":\"octo/agentweaver\"}," +
            "\"action\":\"synchronize\",\"pull_request\":{\"number\":17," +
            "\"head\":{\"sha\":\"" + sourceHeadSha + "\"}," +
            "\"base\":{\"sha\":\"" + sourceBaseSha + "\"}}}");
        using (var webhookResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   TenantId,
                   webhookDeliveryId,
                   "pull_request",
                   webhookBody,
                   sourceControlSecretBackend.Value))
        {
            Assert.Equal(HttpStatusCode.Accepted, webhookResponse.StatusCode);
            var delivery = await ReadJsonAsync<JsonElement>(webhookResponse);
            Assert.Equal("accepted", delivery.GetProperty("state").GetString());
        }
        var webhookCountAfterFirstDelivery = await ReadWebhookDeliveryCountAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(1, webhookCountAfterFirstDelivery);

        using (var duplicateWebhookResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   TenantId,
                   webhookDeliveryId,
                   "pull_request",
                   webhookBody,
                   sourceControlSecretBackend.Value))
        {
            Assert.Equal(HttpStatusCode.OK, duplicateWebhookResponse.StatusCode);
            var delivery = await ReadJsonAsync<JsonElement>(duplicateWebhookResponse);
            Assert.Equal("duplicate", delivery.GetProperty("state").GetString());
        }

        using (var spoofedWebhookResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   TenantId,
                   Guid.NewGuid().ToString("D"),
                   "pull_request",
                   webhookBody,
                   "spoofed-secret"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, spoofedWebhookResponse.StatusCode);
        }

        var changedWebhookBody = Encoding.UTF8.GetBytes(
            "{\"repository\":{\"id\":12345,\"full_name\":\"octo/agentweaver\"}," +
            "\"action\":\"closed\"}");
        using (var changedPayloadResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   TenantId,
                   webhookDeliveryId,
                   "issues",
                   changedWebhookBody,
                   sourceControlSecretBackend.Value))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedPayloadResponse.StatusCode);
        }

        var foreignRepositoryBody = Encoding.UTF8.GetBytes(
            "{\"repository\":{\"id\":12345,\"full_name\":\"octo/other-repository\"}," +
            "\"action\":\"opened\"}");
        using (var foreignRepositoryResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   TenantId,
                   Guid.NewGuid().ToString("D"),
                   "issues",
                   foreignRepositoryBody,
                   sourceControlSecretBackend.Value))
        {
            Assert.Equal(HttpStatusCode.Conflict, foreignRepositoryResponse.StatusCode);
        }

        using (var foreignTenantResponse = await SendSignedWebhookRelayAsync(
                   orchestrator,
                   webhookPath,
                   runToken,
                   OtherTenantId,
                   Guid.NewGuid().ToString("D"),
                   "pull_request",
                   webhookBody,
                   sourceControlSecretBackend.Value))
        {
            Assert.Equal(HttpStatusCode.Forbidden, foreignTenantResponse.StatusCode);
        }

        Assert.Equal(
            webhookCountAfterFirstDelivery,
            await ReadWebhookDeliveryCountAsync(_connectionString, ownerSchema, project.ProjectId));

        var outcomeProposal = new ProposeCoordinatorOutcomeRequest(
            1,
            "outcome-proposal-1",
            "outcome-gate-1",
            new CoordinatorOutcomeSpecification(
                "outcome-1",
                "Deliver the approved change",
                "A validated implementation is ready",
                "Keep the change within the accepted work plan",
                "Use only the accepted project configuration",
                []));
        using var proposedOutcome = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_outcome_spec",
            runToken,
            outcomeProposal);
        await AssertStatusAsync(proposedOutcome, HttpStatusCode.OK);
        var proposedOutcomeResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(proposedOutcome);
        Assert.True(proposedOutcomeResult.Accepted);
        Assert.Equal(2, proposedOutcomeResult.StateVersion);
        Assert.Equal("outcome-gate-1", proposedOutcomeResult.PendingGate?.RequestId);
        Assert.Equal(CoordinatorGateKind.OutcomeConfirmation, proposedOutcomeResult.PendingGate?.Kind);

        using var currentDecisionState = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, currentDecisionState.StatusCode);
        Assert.True(currentDecisionState.Headers.CacheControl?.NoStore);
        var decisionStateView =
            await currentDecisionState.Content.ReadFromJsonAsync<CoordinatorDecisionStateView>(
                CoordinationJsonOptions);
        Assert.NotNull(decisionStateView);
        Assert.Equal(2, decisionStateView.StateVersion);
        Assert.Equal("outcome-gate-1", decisionStateView.PendingGate?.RequestId);
        Assert.False(decisionStateView.CanDecompose);

        using var outcomeProposalRetry = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_outcome_spec",
            runToken,
            outcomeProposal);
        Assert.Equal(HttpStatusCode.OK, outcomeProposalRetry.StatusCode);
        var retriedOutcomeResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(outcomeProposalRetry);
        Assert.Equal(proposedOutcomeResult.DecisionId, retriedOutcomeResult.DecisionId);
        Assert.Equal(2, retriedOutcomeResult.StateVersion);

        using var gateAcknowledgment = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/outcome-gate-1/acknowledge",
            runToken,
            new AcknowledgeCoordinatorGateRequest(2, "outcome-ack-1"));
        Assert.Equal(HttpStatusCode.OK, gateAcknowledgment.StatusCode);
        var acknowledgedGate =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(gateAcknowledgment);
        Assert.True(acknowledgedGate.Accepted);
        Assert.Equal(3, acknowledgedGate.StateVersion);
        Assert.Equal("outcome-gate-1", acknowledgedGate.PendingGate?.RequestId);

        using var invalidGateAnswer = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/outcome-gate-1/answer",
            runToken,
            new AnswerCoordinatorGateRequest(3, "outcome-answer-invalid", "not-allowed", null));
        Assert.Equal(HttpStatusCode.OK, invalidGateAnswer.StatusCode);
        var rejectedGateAnswer =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(invalidGateAnswer);
        Assert.False(rejectedGateAnswer.Accepted);
        Assert.Equal(4, rejectedGateAnswer.StateVersion);
        Assert.Equal("outcome-gate-1", rejectedGateAnswer.PendingGate?.RequestId);

        using var answeredGate = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/outcome-gate-1/answer",
            runToken,
            new AnswerCoordinatorGateRequest(4, "outcome-answer-1", "approve", null));
        Assert.Equal(HttpStatusCode.OK, answeredGate.StatusCode);
        var answeredGateResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(answeredGate);
        Assert.True(answeredGateResult.Accepted);
        Assert.Equal(5, answeredGateResult.StateVersion);
        Assert.Null(answeredGateResult.PendingGate);

        using var askedQuestion = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/questions",
            runToken,
            new AskCoordinatorQuestionRequest(
                5,
                "question-ask-1",
                "question-gate-1",
                "execution-detail",
                "Which controlled runner should execute the validated tests?",
                ["runner-a", "runner-b"],
                true));
        Assert.Equal(HttpStatusCode.OK, askedQuestion.StatusCode);
        var askedQuestionResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(askedQuestion);
        Assert.True(askedQuestionResult.Accepted);
        Assert.Equal(6, askedQuestionResult.StateVersion);
        Assert.Equal("question-gate-1", askedQuestionResult.PendingGate?.RequestId);

        using var answeredQuestion = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/question-gate-1/answer",
            runToken,
            new AnswerCoordinatorGateRequest(
                6,
                "question-answer-1",
                null,
                "runner-a is provisioned for this run"));
        Assert.Equal(HttpStatusCode.OK, answeredQuestion.StatusCode);
        var answeredQuestionResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(answeredQuestion);
        Assert.True(answeredQuestionResult.Accepted);
        Assert.Equal(7, answeredQuestionResult.StateVersion);
        Assert.Null(answeredQuestionResult.PendingGate);

        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{ownerSchema}".coordinator_decisions),
                (SELECT count(*) FROM "{ownerSchema}".coordinator_gates
                    WHERE request_id = 'outcome-gate-1' AND gate_state = 'approved'),
                (SELECT count(*) FROM "{ownerSchema}".coordinator_gates
                    WHERE request_id = 'question-gate-1' AND gate_state = 'answered'),
                (SELECT count(*) FROM "{ownerSchema}".coordinator_decision_outbox)
            """, connection))
        await using (var reader = await query.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(10, reader.GetInt64(3));
        }

        using var defaultWorkflowResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/select_workflow",
            runToken,
            new SelectCoordinatorWorkflowRequest(
                7, "workflow-default-1", "workflow-default-1", WorkflowId: null));
        Assert.Equal(HttpStatusCode.OK, defaultWorkflowResponse.StatusCode);
        var defaultWorkflow =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(defaultWorkflowResponse);
        Assert.True(defaultWorkflow.Accepted);
        Assert.Equal(8, defaultWorkflow.StateVersion);
        Assert.Null(defaultWorkflow.PendingGate);

        var generatedWorkflow = new WorkflowDefinition(
            "generated-test",
            "1.0.0",
            "generated-catalog-v1",
            WorkflowDefinitionOrigin.Generated,
            1,
            [
                new WorkflowStepDefinition(
                    "implement",
                    "Implement an accepted item using the selected isolation provider.",
                    WorkflowStepMode.Open,
                    0,
                    new WorkflowCardinality(1, 1),
                    [],
                    ["implementer"],
                    ["implementation"],
                    ["isolated-worktree"],
                    ["container.create"],
                    null,
                    null),
                new WorkflowStepDefinition(
                    "build-test",
                    "Record a typed platform build-and-test request.",
                    WorkflowStepMode.Platform,
                    1,
                    new WorkflowCardinality(0, 1),
                    [],
                    [],
                    [],
                    [],
                    [],
                    null,
                    WorkflowPlatformGate.BuildTest),
                new WorkflowStepDefinition(
                    "merge",
                    "Merge the approved pull request after current checks pass.",
                    WorkflowStepMode.Platform,
                    2,
                    new WorkflowCardinality(0, 1),
                    [],
                    [],
                    [],
                    [],
                    [],
                    null,
                    WorkflowPlatformGate.Merge)
            ]);
        using var selectWorkflowResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/select_workflow",
            runToken,
            new SelectCoordinatorWorkflowRequest(
                8,
                "workflow-select-generated-1",
                "generated-workflow-gate-1",
                generatedWorkflow.Id,
                generatedWorkflow));
        Assert.Equal(HttpStatusCode.OK, selectWorkflowResponse.StatusCode);
        var selectedWorkflow =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(selectWorkflowResponse);
        Assert.True(selectedWorkflow.Accepted);
        Assert.Equal(9, selectedWorkflow.StateVersion);
        Assert.Equal(CoordinatorGateKind.GeneratedWorkflowConfirmation, selectedWorkflow.PendingGate?.Kind);

        using var prematurePlan = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_work_plan",
            runToken,
            new ProposeCoordinatorWorkPlanRequest(
                9,
                "premature-plan-1",
                "premature-plan-gate-1",
                new WorkPlan("premature-plan", "generated-test", "1.0.0", "generated-catalog-v1", [])));
        Assert.Equal(HttpStatusCode.OK, prematurePlan.StatusCode);
        var prematurePlanResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(prematurePlan);
        Assert.False(prematurePlanResult.Accepted);
        Assert.Equal(10, prematurePlanResult.StateVersion);
        Assert.Equal("generated-workflow-gate-1", prematurePlanResult.PendingGate?.RequestId);

        using var confirmedGeneratedWorkflow = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/generated-workflow-gate-1/answer",
            runToken,
            new AnswerCoordinatorGateRequest(10, "generated-workflow-answer-1", "approve", null));
        Assert.Equal(HttpStatusCode.OK, confirmedGeneratedWorkflow.StatusCode);
        var confirmedGeneratedResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(confirmedGeneratedWorkflow);
        Assert.True(confirmedGeneratedResult.Accepted);
        Assert.Equal(11, confirmedGeneratedResult.StateVersion);
        Assert.Null(confirmedGeneratedResult.PendingGate);

        string receiptGrantId;
        string receiptGrantRevision;
        string receiptStepId;
        string receiptPurpose;
        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT grant_id, revision, step_id, purpose
            FROM "{ownerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND is_current AND grant_state = 'active'
              AND action_ids ? 'propose_work_plan'
            """, connection))
        {
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("session", root.RootSessionId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            receiptGrantId = reader.GetString(0);
            receiptGrantRevision = reader.GetString(1);
            receiptStepId = reader.GetString(2);
            receiptPurpose = reader.GetString(3);
            Assert.False(await reader.ReadAsync());
        }

        var cacheKey = new ObjectKey($"runs/{project.ProjectId}/{RunId}/root/copilot-cache");
        using (var cacheBytes = new MemoryStream(Encoding.UTF8.GetBytes("opaque SDK session bytes")))
            await cacheObjectStore.WriteAsync(cacheKey, cacheBytes);
        var checkpointStore = orchestratorFactory.Services
            .GetRequiredService<PostgresMafCheckpointStore>()
            .ForRun(new MafCheckpointBinding(
                new SessionIdentity(project.ProjectId, RunId, root.RootSessionId),
                new CoordinationActor(
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    currentRunnerSubject),
                root.ExecutionFence,
                "Microsoft.Agents.AI.Workflows/1.19.0",
                "platform-model",
                cacheKey));
        MAFCheckpointing.ICheckpointStore<JsonElement> mafStore = checkpointStore;
        var checkpoint = await mafStore.CreateCheckpointAsync(
            root.RootSessionId,
            Payload("""{"workflow":{"$type":1,"state":"pending","$id":"wf"},"step":"one"}"""));

        Assert.Contains(
            (await mafStore.RetrieveIndexAsync(root.RootSessionId))
                .Select(item => item.CheckpointId),
            item => item == checkpoint.CheckpointId);
        var restoredCheckpoint = await mafStore.RetrieveCheckpointAsync(root.RootSessionId, checkpoint);
        Assert.Equal(
            "$id",
            restoredCheckpoint.GetProperty("workflow").EnumerateObject().First().Name);

        var incompatibleCache = await checkpointStore.GetRecoveryDecisionAsync(
            new MafCheckpointBinding(
                new SessionIdentity(project.ProjectId, RunId, root.RootSessionId),
                new CoordinationActor(
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    currentRunnerSubject),
                root.ExecutionFence,
                "Microsoft.Agents.AI.Workflows/2.0.0",
                "platform-model",
                cacheKey),
            checkpoint.CheckpointId,
            "Microsoft.Agents.AI.Workflows/2.0.0",
            "platform-model",
            CancellationToken.None);
        Assert.Equal(CheckpointCacheRecovery.RebuildFromJournal, incompatibleCache.Recovery);
        Assert.Equal("sdk_cache_binding_incompatible", incompatibleCache.Reason);
        _ = await cacheObjectStore.DeleteAsync(cacheKey);
        var missingCache = await checkpointStore.GetRecoveryDecisionAsync(
            new MafCheckpointBinding(
                new SessionIdentity(project.ProjectId, RunId, root.RootSessionId),
                new CoordinationActor(
                    new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    currentRunnerSubject),
                root.ExecutionFence,
                "Microsoft.Agents.AI.Workflows/1.19.0",
                "platform-model",
                cacheKey),
            checkpoint.CheckpointId,
            "Microsoft.Agents.AI.Workflows/1.19.0",
            "platform-model",
            CancellationToken.None);
        Assert.Equal(CheckpointCacheRecovery.RebuildFromJournal, missingCache.Recovery);
        Assert.Equal("sdk_cache_object_missing", missingCache.Reason);
        await Assert.ThrowsAsync<CheckpointJournalRebuildRequiredException>(async () =>
            await mafStore.RetrieveCheckpointAsync(root.RootSessionId, checkpoint));

        using (var cacheBytes = new MemoryStream(Encoding.UTF8.GetBytes("opaque SDK session bytes")))
            await cacheObjectStore.WriteAsync(cacheKey, cacheBytes);
        var staleCheckpointStore = checkpointStore.ForRun(new MafCheckpointBinding(
            new SessionIdentity(project.ProjectId, RunId, root.RootSessionId),
            new CoordinationActor(
                new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                currentRunnerSubject),
            root.ExecutionFence + 1,
            "Microsoft.Agents.AI.Workflows/1.19.0",
            "platform-model",
            cacheKey));
        await Assert.ThrowsAsync<CoordinationException>(async () =>
            await staleCheckpointStore.CreateCheckpointAsync(
                root.RootSessionId, Payload("""{"state":"stale"}""")));

        using var missingReceipt = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{Guid.NewGuid():D}",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.NotFound, missingReceipt.StatusCode);
        Assert.True(missingReceipt.Headers.CacheControl?.NoStore);

        var receiptId = Guid.NewGuid();
        var receiptIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var policyProvider = new AgtPolicyProvider();
        var policyOptions = ReceiptPolicyOptions();
        var policyBinding = await ResolveReceiptPolicyBindingAsync(policyProvider, policyOptions, RunId);
        var receiptPrincipal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new JwtSecurityTokenHandler().ReadJwtToken(runToken).Claims,
                "integration-jwt"));
        var receiptStore = orchestratorFactory.Services.GetRequiredService<ExecutableActionGrantOwnerStore>();
        var policyJournal = orchestratorFactory.Services
            .GetRequiredService<IExecutableActionPolicyEvaluationJournal>();
        var sourceReceiptGuard = new ExecutableActionGuard(
            policyProvider,
            policyOptions,
            policyJournal,
            receiptStore,
            sourceReceiptWriter: receiptStore,
            evaluationReceiptWriter: receiptStore);
        var effectInvoked = false;
        var receiptInvocation = new ExecutableActionInvocation(
            receiptPrincipal,
            root.RootSessionId,
            receiptStepId,
            ReceiptActionId,
            receiptPurpose,
            new ExecutableActionGrantReference(receiptGrantId, receiptGrantRevision),
            root.ExecutionFence,
            policyBinding,
            receiptId);
        var httpContextAccessor = orchestratorFactory.Services.GetRequiredService<IHttpContextAccessor>();
        var previousHttpContext = httpContextAccessor.HttpContext;
        var authorityContext = new DefaultHttpContext
        {
            User = receiptPrincipal
        };
        authorityContext.Request.Headers.Authorization = $"Bearer {runToken}";
        authorityContext.Request.Headers["X-Agentweaver-Tenant"] = TenantId;
        httpContextAccessor.HttpContext = authorityContext;
        ExecutableActionGuardResult<string> receiptAttempt;
        ExecutableActionGuardResult<string> duplicateReceiptAttempt;
        var duplicateEffectInvoked = false;
        try
        {
            receiptAttempt = await sourceReceiptGuard.ExecuteAsync(
                receiptInvocation,
                _ =>
                {
                    effectInvoked = true;
                    return Task.FromResult("unexpected");
                });
            duplicateReceiptAttempt = await sourceReceiptGuard.ExecuteAsync(
                receiptInvocation,
                _ =>
                {
                    duplicateEffectInvoked = true;
                    return Task.FromResult("unexpected");
                });
        }
        finally
        {
            httpContextAccessor.HttpContext = previousHttpContext;
        }
        Assert.True(
            receiptAttempt.Outcome == PolicyEvaluationOutcome.Allow,
            $"Policy guard returned {receiptAttempt.Outcome}/{receiptAttempt.ReasonCode}; " +
            $"effectInvoked={receiptAttempt.EffectInvoked}.");
        Assert.True(receiptAttempt.EffectInvoked);
        Assert.True(effectInvoked);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, duplicateReceiptAttempt.ReasonCode);
        Assert.False(duplicateReceiptAttempt.EffectInvoked);
        Assert.False(duplicateEffectInvoked);

        using var policyReplay = await SendAsync(
            events,
            HttpMethod.Get,
            "/internal/sessions/root/events",
            runToken,
            [TenantId]);
        await AssertStatusAsync(policyReplay, HttpStatusCode.OK);
        using var policyJournalPage = JsonDocument.Parse(await policyReplay.Content.ReadAsStringAsync());
        var policyJournalEvent = Assert.Single(policyJournalPage.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("policyEvaluation", policyJournalEvent.GetProperty("kind").GetString());
        Assert.Equal(receiptId, policyJournalEvent.GetProperty("eventId").GetGuid());
        using var receiptRetry = await SendJsonAsync(
            events,
            HttpMethod.Post,
            $"/internal/sessions/{root.RootSessionId}/policy-evaluations",
            runToken,
            new PolicyEvaluationReceiptReferenceRequest(receiptId));
        Assert.Equal(HttpStatusCode.OK, receiptRetry.StatusCode);
        Assert.True(receiptRetry.Headers.CacheControl?.NoStore);
        var retryAcknowledgment =
            await ReadJsonAsync<PolicyEvaluationAppendAcknowledgment>(receiptRetry);
        Assert.True(retryAcknowledgment.IsDuplicate);
        Assert.Equal(policyJournalEvent.GetProperty("position").GetInt64(), retryAcknowledgment.Position);

        using var callerSuppliedOwner = await SendJsonAsync(
            events,
            HttpMethod.Post,
            $"/internal/sessions/{root.RootSessionId}/policy-evaluations",
            runToken,
            new
            {
                receiptId,
                ownerUrl = "https://127.0.0.1/"
            });
        Assert.Equal(HttpStatusCode.BadRequest, callerSuppliedOwner.StatusCode);

        using var storedReceiptResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{receiptId:D}",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, storedReceiptResponse.StatusCode);
        Assert.True(storedReceiptResponse.Headers.CacheControl?.NoStore);
        var storedReceipt = await storedReceiptResponse.Content.ReadFromJsonAsync<PolicyEvaluationReceiptView>(
            CoordinationJsonOptions);
        Assert.NotNull(storedReceipt);
        Assert.Equal(receiptId, storedReceipt.ReceiptId);
        Assert.Equal(receiptGrantId, storedReceipt.Evidence.GrantId);
        Assert.Equal(ReceiptActionId, storedReceipt.Evidence.ActionId);
        Assert.Equal(PolicyEvaluationOutcome.Allow, storedReceipt.Evidence.Outcome);
        Assert.Equal(receiptIssuer, storedReceipt.Issuer);
        Assert.Equal(
            new SessionIdentity(project.ProjectId, RunId, root.RootSessionId),
            storedReceipt.Identity);

        string inactiveGrantId;
        string inactiveGrantRevision;
        string inactiveGrantStep;
        string inactiveGrantPurpose;
        string inactiveGrantAction;
        long inactiveGrantFence;
        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT grant_id, revision, step_id, purpose, action_ids, execution_fence
            FROM "{ownerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND actor_id = @actor
              AND (NOT is_current OR grant_state <> 'active' OR expires_at <= clock_timestamp())
            ORDER BY created_at DESC
            LIMIT 1
            """, connection))
        {
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("session", root.RootSessionId);
            query.Parameters.AddWithValue("actor", currentRunnerSubject);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            inactiveGrantId = reader.GetString(0);
            inactiveGrantRevision = reader.GetString(1);
            inactiveGrantStep = reader.GetString(2);
            inactiveGrantPurpose = reader.GetString(3);
            var inactiveActions = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [];
            Assert.NotEmpty(inactiveActions);
            inactiveGrantAction = inactiveActions[0];
            inactiveGrantFence = reader.GetInt64(5);
        }

        var deniedReceiptId = Guid.NewGuid();
        var deniedInvocation = receiptInvocation with
        {
            StepId = inactiveGrantStep,
            ActionId = inactiveGrantAction,
            Purpose = inactiveGrantPurpose,
            GrantReference = new ExecutableActionGrantReference(
                inactiveGrantId, inactiveGrantRevision),
            Fence = inactiveGrantFence,
            EventId = deniedReceiptId
        };
        httpContextAccessor.HttpContext = authorityContext;
        ExecutableActionGuardResult<string> deniedReceiptAttempt;
        try
        {
            deniedReceiptAttempt = await sourceReceiptGuard.ExecuteAsync(
                deniedInvocation,
                _ => Task.FromResult("unexpected"));
        }
        finally
        {
            httpContextAccessor.HttpContext = previousHttpContext;
        }
        Assert.Equal(PolicyEvaluationOutcome.Deny, deniedReceiptAttempt.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, deniedReceiptAttempt.ReasonCode);
        Assert.False(deniedReceiptAttempt.EffectInvoked);
        using var deniedReceiptResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{deniedReceiptId:D}",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, deniedReceiptResponse.StatusCode);
        var deniedReceipt = await deniedReceiptResponse.Content.ReadFromJsonAsync<PolicyEvaluationReceiptView>(
            CoordinationJsonOptions);
        Assert.NotNull(deniedReceipt);
        Assert.Equal(PolicyEvaluationOutcome.Deny, deniedReceipt.Evidence.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.NoEffectiveGrant, deniedReceipt.Evidence.ReasonCode);
        Assert.Equal(inactiveGrantId, deniedReceipt.Evidence.GrantId);

        using var callerReceiptWrite = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{Guid.NewGuid():D}",
            runToken,
            new { accepted = true, actorId = currentRunnerSubject });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, callerReceiptWrite.StatusCode);

        var acceptedPlan = new WorkPlan(
            "accepted-plan",
            "generated-test",
            "1.0.0",
            "generated-catalog-v1",
            [
                new WorkPlanItem(
                    "implement-1",
                    "implement",
                    "Implement the accepted change",
                    "Implement the accepted change and verify it with targeted tests.",
                    "implementer",
                    "test-agent",
                    "implementation",
                    "platform-model",
                    "sandbox-platform",
                    "isolated-worktree",
                    [],
                    [])
            ]);
        await using (var unavailableFactory = new OrchestratorIntegrationFactory(
                         _connectionString,
                         ownerSchema,
                         signingKey,
                         projects.CreateHandler,
                         () => eventsFactory.Server.CreateHandler()))
        using (var unavailableClient = unavailableFactory.CreateClient(new WebApplicationFactoryClientOptions
               {
                   BaseAddress = new Uri("https://orchestrator.test")
               }))
        using (var unavailablePlan = await SendJsonAsync(
                   unavailableClient,
                   HttpMethod.Post,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_work_plan",
                   runToken,
                   new ProposeCoordinatorWorkPlanRequest(
                       11,
                       "unavailable-work-plan-1",
                       "unavailable-work-plan-gate-1",
                       acceptedPlan)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailablePlan.StatusCode);
            await using var database = NpgsqlDataSource.Create(_connectionString);
            await using var connection = await database.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{ownerSchema}\".coordinator_run_selection_contexts " +
                "WHERE project_id = @project AND run_id = @run",
                connection);
            count.Parameters.AddWithValue("project", project.ProjectId);
            count.Parameters.AddWithValue("run", RunId);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        var effectsBeforeRevokedPlan = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        sandboxProvider.AfterNegotiationAsync = cancellationToken =>
            RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
        using (var revokedPlan = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_work_plan",
                   runToken,
                   new ProposeCoordinatorWorkPlanRequest(
                       11, "work-plan-revoked-1", "work-plan-revoked-gate-1", acceptedPlan)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedPlan.StatusCode);
        }
        Assert.Equal(
            effectsBeforeRevokedPlan,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));
        Assert.Equal(1, sandboxProvider.NegotiationCount);

        runnerRole = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);
        var concurrentNegotiations = 0;
        var bothNegotiationsReached = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sandboxProvider.AfterNegotiationAsync = async cancellationToken =>
        {
            if (Interlocked.Increment(ref concurrentNegotiations) == 2)
                bothNegotiationsReached.TrySetResult(true);
            await bothNegotiationsReached.Task.WaitAsync(
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        };
        var proposalPath =
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_work_plan";
        var proposalRequest = new ProposeCoordinatorWorkPlanRequest(
            11, "work-plan-proposal-1", "work-plan-gate-1", acceptedPlan);
        var effectsBeforeConcurrentProposals = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        var proposalResponses = await Task.WhenAll(
            SendJsonAsync(orchestrator, HttpMethod.Post, proposalPath, runToken, proposalRequest),
            SendJsonAsync(orchestrator, HttpMethod.Post, proposalPath, runToken, proposalRequest));
        using var proposedPlan = proposalResponses[0];
        using var proposalReplay = proposalResponses[1];
        sandboxProvider.AfterNegotiationAsync = null;
        Assert.True(
            proposedPlan.StatusCode == HttpStatusCode.OK,
            $"{proposedPlan.StatusCode}: {await proposedPlan.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, proposalReplay.StatusCode);
        var proposedPlanResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(proposedPlan);
        var proposalReplayResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(proposalReplay);
        Assert.True(proposedPlanResult.Accepted);
        Assert.Equal(12, proposedPlanResult.StateVersion);
        Assert.Equal("work-plan-gate-1", proposedPlanResult.PendingGate?.RequestId);
        Assert.Equal(proposedPlanResult.DecisionId, proposalReplayResult.DecisionId);
        Assert.Equal(proposedPlanResult.StateVersion, proposalReplayResult.StateVersion);
        Assert.Equal(3, sandboxProvider.NegotiationCount);
        var effectsAfterConcurrentProposals = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(effectsBeforeConcurrentProposals.Bindings + 1, effectsAfterConcurrentProposals.Bindings);
        Assert.Equal(effectsBeforeConcurrentProposals.Decisions + 1, effectsAfterConcurrentProposals.Decisions);
        Assert.Equal(effectsBeforeConcurrentProposals.Outbox + 1, effectsAfterConcurrentProposals.Outbox);
        await AssertAcceptedSandboxBindingAsync(
            _connectionString, ownerSchema, project.ProjectId, sandboxProvider.ResourceId);

        var restoringSandboxProvider = new ControlledSandboxResourceProvider();
        await using (var restoringFactory = new OrchestratorIntegrationFactory(
                         _connectionString,
                         ownerSchema,
                         signingKey,
                         projects.CreateHandler,
                         () => eventsFactory.Server.CreateHandler(),
                         sandboxProvider: restoringSandboxProvider))
        using (var restoredOrchestrator = restoringFactory.CreateClient(
                   new WebApplicationFactoryClientOptions
                   {
                       BaseAddress = new Uri("https://orchestrator.test")
                   }))
        {
            using (var restoredGateAnswer = await SendJsonAsync(
                       restoredOrchestrator,
                       HttpMethod.Post,
                       $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/work-plan-gate-1/answer",
                       runToken,
                       new AnswerCoordinatorGateRequest(12, "work-plan-answer-1", "approve", null)))
            {
                Assert.Equal(HttpStatusCode.OK, restoredGateAnswer.StatusCode);
                var restoredGateResult =
                    await ReadJsonAsync<CoordinatorDecisionOperationResponse>(restoredGateAnswer);
                Assert.True(restoredGateResult.Accepted);
                Assert.Equal(13, restoredGateResult.StateVersion);
                Assert.Null(restoredGateResult.PendingGate);
            }

            using var assemblyRequest = await SendJsonAsync(
                restoredOrchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/request_assembly",
                runToken,
                new RequestCoordinatorAssemblyRequest(
                    13,
                    "assembly-idempotency-1",
                    new CoordinatorAssemblyRequest(
                        "assembly-request-1",
                        "generated-test",
                        "1.0.0",
                        "accepted-plan",
                        "build-test",
                        WorkflowPlatformGate.BuildTest)));
            Assert.Equal(HttpStatusCode.OK, assemblyRequest.StatusCode);
            var assemblyRequestResult =
                await ReadJsonAsync<CoordinatorDecisionOperationResponse>(assemblyRequest);
            Assert.True(assemblyRequestResult.Accepted);
            Assert.Equal(14, assemblyRequestResult.StateVersion);
            Assert.Null(assemblyRequestResult.PendingGate);
            Assert.Equal(0, restoringSandboxProvider.NegotiationCount);
        }
        using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{ownerSchema}".coordination_sessions
                    WHERE project_id = @project AND run_id = @run AND parent_session_id IS NOT NULL),
                (SELECT execution_state FROM "{ownerSchema}".accepted_runs
                    WHERE project_id = @project AND run_id = @run)
            """, connection))
        {
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal("idle", reader.GetString(1));
        }

        using var revisedPlan = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/revise_work_plan",
            runToken,
            new ReviseCoordinatorWorkPlanRequest(
                14,
                "work-plan-revision-1",
                "work-plan-revision-1",
                acceptedPlan));
        Assert.True(
            revisedPlan.StatusCode == HttpStatusCode.OK,
            $"{revisedPlan.StatusCode}: {await revisedPlan.Content.ReadAsStringAsync()}");
        var revisedPlanResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(revisedPlan);
        Assert.True(revisedPlanResult.Accepted);
        Assert.Equal(15, revisedPlanResult.StateVersion);
        Assert.Null(revisedPlanResult.PendingGate);
        Assert.Equal(3, sandboxProvider.NegotiationCount);

        var mergeIntentPath = sourceControlBasePath + "/merge-intents";
        async Task<(string IntentId, string ApprovalRequestId)> CreateApprovedMergeIntentAsync(
            string idempotencyKey)
        {
            using var decisionStateResponse = await SendAsync(
                orchestrator, HttpMethod.Get, decisionStatePath, runToken, [TenantId]);
            await AssertStatusAsync(decisionStateResponse, HttpStatusCode.OK);
            var decisionState =
                await ReadJsonAsync<CoordinatorDecisionStateView>(decisionStateResponse);
            using var mergeApprovalResponse = await SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                mergeIntentPath,
                runToken,
                new OrchestratorHost::Agentweaver.Orchestrator.PrepareSourceControlMergeIntentRequest(
                    idempotencyKey,
                    decisionState.StateVersion,
                    "generated-test",
                    "1.0.0",
                    "accepted-plan",
                    "merge",
                    17,
                    SourceControlMergeMethod.Rebase));
            Assert.True(
                mergeApprovalResponse.StatusCode == HttpStatusCode.Accepted,
                $"{mergeApprovalResponse.StatusCode}: {await mergeApprovalResponse.Content.ReadAsStringAsync()}");
            var mergeApproval = await ReadJsonAsync<JsonElement>(mergeApprovalResponse);
            var intentId = mergeApproval.GetProperty("intentId").GetString()
                ?? throw new InvalidOperationException("The source-control merge intent ID was missing.");
            var approvalRequestId = mergeApproval.GetProperty("approvalRequestId").GetString()
                ?? throw new InvalidOperationException("The source-control approval request ID was missing.");
            Assert.Equal("approval_pending", mergeApproval.GetProperty("state").GetString());
            var approvalStateVersion = mergeApproval.GetProperty("stateVersion").GetInt64();
            Assert.Equal(decisionState.StateVersion + 1, approvalStateVersion);

            using var approveMerge = await SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/{approvalRequestId}/answer",
                runToken,
                new AnswerCoordinatorGateRequest(
                    approvalStateVersion, idempotencyKey + "-approve", "approve", null));
            Assert.True(
                approveMerge.StatusCode == HttpStatusCode.OK,
                $"{approveMerge.StatusCode}: {await approveMerge.Content.ReadAsStringAsync()}");
            var approvedMerge = await ReadJsonAsync<CoordinatorDecisionOperationResponse>(approveMerge);
            Assert.True(approvedMerge.Accepted);
            Assert.Equal(approvalStateVersion + 1, approvedMerge.StateVersion);
            Assert.Null(approvedMerge.PendingGate);
            return (intentId, approvalRequestId);
        }

        async Task<(string State, string? MergeSha, string? FailureCode)> ReadMergeIntentStateAsync(
            string intentId)
        {
            await using var database = NpgsqlDataSource.Create(_connectionString);
            await using var connection = await database.OpenConnectionAsync();
            await using var query = new NpgsqlCommand($"""
                SELECT intent_state, merge_sha, last_failure_code
                FROM "{ownerSchema}".source_control_merge_intents
                WHERE project_id = @project AND run_id = @run AND intent_id = @intent
                """, connection);
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("intent", intentId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        async Task<(string GrantId, string Revision, bool IsCurrent, string State,
            long GrantStateVersion, long ApprovalStateVersion, long CurrentStateVersion,
            long IntentFence, long CurrentFence)> ReadMergeGrantAndFenceStateAsync(string intentId)
        {
            await using var database = NpgsqlDataSource.Create(_connectionString);
            await using var connection = await database.OpenConnectionAsync();
            await using var query = new NpgsqlCommand($"""
                SELECT g.grant_id, g.revision, g.is_current, g.grant_state,
                       g.source_state_version, i.approval_state_version,
                       current_decision.state_version, i.execution_fence,
                       current_decision.execution_fence
                FROM "{ownerSchema}".executable_action_grants AS g
                INNER JOIN "{ownerSchema}".source_control_merge_intents AS i
                  ON i.project_id = g.project_id AND i.run_id = g.run_id
                 AND i.intent_id = g.source_control_intent_id
                CROSS JOIN LATERAL (
                    SELECT state_version, execution_fence
                    FROM "{ownerSchema}".coordinator_decisions
                    WHERE project_id = g.project_id AND run_id = g.run_id
                      AND session_id = g.session_id
                    ORDER BY state_version DESC
                    LIMIT 1
                ) AS current_decision
                WHERE g.project_id = @project AND g.run_id = @run
                  AND g.session_id = @session AND g.source_control_intent_id = @intent
                  AND g.action_ids ? 'source_control.merge'
                  AND g.purpose = 'source-control.merge'
                """, connection);
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("session", root.RootSessionId);
            query.Parameters.AddWithValue("intent", intentId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var state = (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8));
            Assert.False(await reader.ReadAsync());
            return state;
        }

        async Task<string> AssertPreEffectConflictAsync(
            HttpResponseMessage response,
            string intentId,
            int mergeCountBefore)
        {
            var body = await ReadJsonAsync<JsonElement>(response);
            string failureCode;
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("conflict", body.GetProperty("state").GetString());
                failureCode = body.GetProperty("failureCode").GetString()
                    ?? throw new InvalidOperationException("The merge conflict failure code was missing.");
            }
            else
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected a persisted conflict or authority denial; got {(int)response.StatusCode} {response.StatusCode}: {body}");
                failureCode = body.GetProperty("error").GetString()
                    ?? throw new InvalidOperationException("The merge denial error code was missing.");
            }
            Assert.Contains(
                failureCode,
                new[]
                {
                    "run_selection_permission_denied",
                    "source_control_merge_grant_not_current",
                    "source_control_run_binding_changed"
                });
            var persisted = await ReadMergeIntentStateAsync(intentId);
            Assert.Equal("conflict", persisted.State);
            Assert.Null(persisted.MergeSha);
            Assert.Equal(failureCode, persisted.FailureCode);
            Assert.Equal(mergeCountBefore, sourceControlGitHub.MergeRequestCount);
            return failureCode;
        }

        var (lockConflictIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-lock-race");
        var mergeCountBeforeLockRace = sourceControlGitHub.MergeRequestCount;
        await using (var lockSource = NpgsqlDataSource.Create(_connectionString))
        await using (var lockConnection = await lockSource.OpenConnectionAsync())
        {
            await using (var acquireRepositoryLock = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                lockConnection))
            {
                acquireRepositoryLock.Parameters.AddWithValue("repository", "octo/agentweaver");
                await acquireRepositoryLock.ExecuteScalarAsync();
            }

            var lockHeld = true;
            var roleRevoked = false;
            try
            {
                var blockedMerge = SendAsync(
                    orchestrator,
                    HttpMethod.Post,
                    mergeIntentPath + "/" + lockConflictIntentId + "/execute",
                    runToken,
                    [TenantId]);
                Assert.True(
                    await WaitForSourceControlAdvisoryLockWaitAsync(lockSource),
                    "the merge request must be observed waiting on the PostgreSQL repository advisory lock");
                Assert.False(blockedMerge.IsCompleted);

                await RevokeRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerRole.AssignmentId,
                    runnerRole.Revision);
                roleRevoked = true;
                await using (var releaseRepositoryLock = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                    lockConnection))
                {
                    releaseRepositoryLock.Parameters.AddWithValue("repository", "octo/agentweaver");
                    Assert.True((bool)(await releaseRepositoryLock.ExecuteScalarAsync())!);
                }
                lockHeld = false;
                using var lockConflict = await blockedMerge.WaitAsync(TimeSpan.FromSeconds(30));
                await AssertPreEffectConflictAsync(
                    lockConflict, lockConflictIntentId, mergeCountBeforeLockRace);
            }
            finally
            {
                if (lockHeld)
                {
                    await using var releaseRepositoryLock = new NpgsqlCommand(
                        "SELECT pg_advisory_unlock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                        lockConnection);
                    releaseRepositoryLock.Parameters.AddWithValue("repository", "octo/agentweaver");
                    await releaseRepositoryLock.ExecuteScalarAsync();
                }
                if (roleRevoked)
                    runnerRole = await AssignRoleAsync(
                        projects.PrivilegedFixtureDataSource,
                        runnerMembership.MembershipId,
                        ProjectAuthorityResourceType.Project,
                        project.ProjectId,
                        ProjectAuthorityRole.Orchestrator);
            }
        }

        var (redemptionConflictIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-redemption-race");
        var mergeCountBeforeRedemptionRace = sourceControlGitHub.MergeRequestCount;
        var redemptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRedemption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sourceControlSecretBackend.Handler = async (_, token) =>
        {
            redemptionStarted.TrySetResult();
            await releaseRedemption.Task.WaitAsync(token);
            return sourceControlSecretBackend.NewCredential(sourceControlSecretBackend.Value);
        };
        var redemptionRoleRevoked = false;
        try
        {
            var blockedRedemptionMerge = SendAsync(
                orchestrator,
                HttpMethod.Post,
                mergeIntentPath + "/" + redemptionConflictIntentId + "/execute",
                runToken,
                [TenantId]);
            await redemptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
            redemptionRoleRevoked = true;
            releaseRedemption.TrySetResult();
            using var redemptionConflict =
                await blockedRedemptionMerge.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertPreEffectConflictAsync(
                redemptionConflict, redemptionConflictIntentId, mergeCountBeforeRedemptionRace);
        }
        finally
        {
            releaseRedemption.TrySetResult();
            sourceControlSecretBackend.Handler = null;
            if (redemptionRoleRevoked)
                runnerRole = await AssignRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerMembership.MembershipId,
                    ProjectAuthorityResourceType.Project,
                    project.ProjectId,
                    ProjectAuthorityRole.Orchestrator);
        }

        var (readinessConflictIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-readiness-race");
        var mergeCountBeforeReadinessRace = sourceControlGitHub.MergeRequestCount;
        sourceControlRequestBarrier.Arm((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get &&
            request.RequestUri?.AbsolutePath == "/repos/octo/agentweaver/rules/branches/main"));
        var readinessMerge = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + readinessConflictIntentId + "/execute",
            runToken,
            [TenantId]);
        var readinessRoleRevoked = false;
        try
        {
            await sourceControlRequestBarrier.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
            readinessRoleRevoked = true;
            sourceControlRequestBarrier.Release();
            using var readinessConflict = await readinessMerge.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertPreEffectConflictAsync(
                readinessConflict, readinessConflictIntentId, mergeCountBeforeReadinessRace);
        }
        finally
        {
            sourceControlRequestBarrier.Release();
            if (readinessRoleRevoked)
                runnerRole = await AssignRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerMembership.MembershipId,
                    ProjectAuthorityResourceType.Project,
                    project.ProjectId,
                    ProjectAuthorityRole.Orchestrator);
        }

        var (mergeStartedConflictIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-started-race");
        var mergeCountBeforeStartedRace = sourceControlGitHub.MergeRequestCount;
        mergeStartedSelectionBarrier.Arm(async (request, token) =>
        {
            if (request.Method != HttpMethod.Get ||
                request.RequestUri?.AbsolutePath !=
                $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
                return false;
            return (await ReadMergeIntentStateAsync(mergeStartedConflictIntentId)).State == "merge_started";
        });
        var mergeStartedMerge = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + mergeStartedConflictIntentId + "/execute",
            runToken,
            [TenantId]);
        var mergeStartedRoleRevoked = false;
        try
        {
            await mergeStartedSelectionBarrier.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("merge_started", (await ReadMergeIntentStateAsync(mergeStartedConflictIntentId)).State);
            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
            mergeStartedRoleRevoked = true;
            mergeStartedSelectionBarrier.Release();
            using var mergeStartedConflict = await mergeStartedMerge.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertPreEffectConflictAsync(
                mergeStartedConflict, mergeStartedConflictIntentId, mergeCountBeforeStartedRace);
        }
        finally
        {
            mergeStartedSelectionBarrier.Release();
            if (mergeStartedRoleRevoked)
                runnerRole = await AssignRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerMembership.MembershipId,
                    ProjectAuthorityResourceType.Project,
                    project.ProjectId,
                    ProjectAuthorityRole.Orchestrator);
        }

        var (mergeIntentId, mergeApprovalRequestId) =
            await CreateApprovedMergeIntentAsync("source-control-merge-prepare-1");

        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var query = new NpgsqlCommand($"""
            SELECT grant_id, revision, step_id, purpose, action_ids::text,
                   source_decision_id::text, source_request_id, source_control_intent_id
            FROM "{ownerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND is_current AND grant_state = 'active'
              AND action_ids ? 'source_control.merge'
            """, connection))
        {
            query.Parameters.AddWithValue("project", project.ProjectId);
            query.Parameters.AddWithValue("run", RunId);
            query.Parameters.AddWithValue("session", root.RootSessionId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0)));
            Assert.Equal("1", reader.GetString(1));
            Assert.Equal("merge", reader.GetString(2));
            Assert.Equal("source-control.merge", reader.GetString(3));
            Assert.Equal(
                new[] { "source_control.merge" },
                JsonSerializer.Deserialize<string[]>(reader.GetString(4), CoordinationJsonOptions));
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(5)));
            Assert.Equal(mergeApprovalRequestId, reader.GetString(6));
            Assert.Equal(mergeIntentId, reader.GetString(7));
            Assert.False(await reader.ReadAsync());
        }

        using (var executeMerge = await SendAsync(
                   orchestrator,
                   HttpMethod.Post,
                   mergeIntentPath + "/" + mergeIntentId + "/execute",
                   runToken,
                   [TenantId]))
        {
            Assert.True(
                executeMerge.StatusCode == HttpStatusCode.OK,
                $"{executeMerge.StatusCode}: {await executeMerge.Content.ReadAsStringAsync()}");
            var result = await ReadJsonAsync<JsonElement>(executeMerge);
            Assert.Equal("merged", result.GetProperty("state").GetString());
            Assert.Equal(
                "cccccccccccccccccccccccccccccccccccccccc",
                result.GetProperty("mergeSha").GetString());
        }
        Assert.Equal(1, sourceControlGitHub.MergeRequestCount);
        var mergeRequestBody = sourceControlGitHub.LastMergeRequestBody;
        Assert.NotNull(mergeRequestBody);
        using (var mergeRequestDocument = JsonDocument.Parse(mergeRequestBody))
        {
            Assert.Equal(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                mergeRequestDocument.RootElement.GetProperty("sha").GetString());
            Assert.Equal("rebase", mergeRequestDocument.RootElement.GetProperty("merge_method").GetString());
        }

        using (var mergeReplay = await SendAsync(
                   orchestrator,
                   HttpMethod.Post,
                   mergeIntentPath + "/" + mergeIntentId + "/execute",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, mergeReplay.StatusCode);
            var result = await ReadJsonAsync<JsonElement>(mergeReplay);
            Assert.Equal("merged", result.GetProperty("state").GetString());
        }
        Assert.Equal(1, sourceControlGitHub.MergeRequestCount);

        var (concurrentMergeIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-concurrent");
        var mergeCountBeforeConcurrentExecution = sourceControlGitHub.MergeRequestCount;
        sourceControlRequestBarrier.Arm((request, _) => Task.FromResult(
            request.Method == HttpMethod.Put &&
            request.RequestUri?.AbsolutePath == "/repos/octo/agentweaver/pulls/17/merge"));
        var firstConcurrentMerge = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + concurrentMergeIntentId + "/execute",
            runToken,
            [TenantId]);
        await sourceControlRequestBarrier.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(
            "merge_started",
            (await ReadMergeIntentStateAsync(concurrentMergeIntentId)).State);
        var secondConcurrentMerge = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + concurrentMergeIntentId + "/execute",
            runToken,
            [TenantId]);
        await using (var lockObservationSource = NpgsqlDataSource.Create(_connectionString))
        {
            Assert.True(
                await WaitForSourceControlAdvisoryLockWaitAsync(lockObservationSource),
                "the concurrent merge request must wait in PostgreSQL on the repository advisory lock");
        }
        sourceControlRequestBarrier.Release();
        var concurrentResponses = await Task.WhenAll(
            firstConcurrentMerge.WaitAsync(TimeSpan.FromSeconds(30)),
            secondConcurrentMerge.WaitAsync(TimeSpan.FromSeconds(30)));
        try
        {
            Assert.Equal(2, concurrentResponses.Length);
            foreach (var concurrentResponse in concurrentResponses)
            {
                Assert.Equal(HttpStatusCode.OK, concurrentResponse.StatusCode);
                var concurrentResult = await ReadJsonAsync<JsonElement>(concurrentResponse);
                Assert.Equal("merged", concurrentResult.GetProperty("state").GetString());
                Assert.Equal(
                    "cccccccccccccccccccccccccccccccccccccccc",
                    concurrentResult.GetProperty("mergeSha").GetString());
            }
        }
        finally
        {
            foreach (var concurrentResponse in concurrentResponses)
                concurrentResponse.Dispose();
            sourceControlRequestBarrier.Release();
        }
        Assert.Equal(mergeCountBeforeConcurrentExecution + 1, sourceControlGitHub.MergeRequestCount);
        var persistedConcurrentMerge = await ReadMergeIntentStateAsync(concurrentMergeIntentId);
        Assert.Equal("merged", persistedConcurrentMerge.State);
        Assert.Equal("cccccccccccccccccccccccccccccccccccccccc", persistedConcurrentMerge.MergeSha);

        var (postEffectRevocationIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-post-effect-revocation");
        sourceControlRequestBarrier.Arm((request, _) => Task.FromResult(
            request.Method == HttpMethod.Put &&
            request.RequestUri?.AbsolutePath == "/repos/octo/agentweaver/pulls/17/merge"));
        var postEffectMerge = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + postEffectRevocationIntentId + "/execute",
            runToken,
            [TenantId]);
        var postEffectRoleRevoked = false;
        try
        {
            await sourceControlRequestBarrier.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(
                "merge_started",
                (await ReadMergeIntentStateAsync(postEffectRevocationIntentId)).State);
            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
            postEffectRoleRevoked = true;
            sourceControlRequestBarrier.Release();
            using var postEffectResponse = await postEffectMerge.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, postEffectResponse.StatusCode);
            var postEffectResult = await ReadJsonAsync<JsonElement>(postEffectResponse);
            Assert.Equal("merged", postEffectResult.GetProperty("state").GetString());
            Assert.Equal(
                "cccccccccccccccccccccccccccccccccccccccc",
                postEffectResult.GetProperty("mergeSha").GetString());
            var persistedPostEffectMerge =
                await ReadMergeIntentStateAsync(postEffectRevocationIntentId);
            Assert.Equal("merged", persistedPostEffectMerge.State);
            Assert.Equal(
                "cccccccccccccccccccccccccccccccccccccccc",
                persistedPostEffectMerge.MergeSha);
            Assert.Equal(
                "run_selection_permission_denied",
                persistedPostEffectMerge.FailureCode);
        }
        finally
        {
            sourceControlRequestBarrier.Release();
            if (postEffectRoleRevoked)
                runnerRole = await AssignRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerMembership.MembershipId,
                    ProjectAuthorityResourceType.Project,
                    project.ProjectId,
                    ProjectAuthorityRole.Orchestrator);
        }

        Assert.Equal(3, sourceControlGitHub.MergeRequestCount);
        await using (var restartedOrchestratorFactory = new OrchestratorIntegrationFactory(
                         _connectionString,
                         ownerSchema,
                         signingKey,
                         () => new RequestCountingHandler(
                             projects.CreateHandler(),
                             () => Interlocked.Increment(ref projectsOwnerRequests),
                             mergeStartedSelectionBarrier.PauseIfMatchedAsync),
                         () => eventsFactory.Server.CreateHandler(),
                         cacheObjectStore,
                         sandboxProvider,
                         () => _brokerFactory.Server.CreateHandler(),
                         sourceControlGitHub.CreateHandler,
                         CreateSourceControlProviderCatalog(),
                         policyOptions: SourceControlMergePolicyOptions()))
        using (var restartedOrchestrator = restartedOrchestratorFactory.CreateClient(
                   new WebApplicationFactoryClientOptions
                   {
                       BaseAddress = new Uri("https://orchestrator.test")
                   }))
        using (var restartedConcurrentMergeReplay = await SendAsync(
                   restartedOrchestrator,
                   HttpMethod.Post,
                   mergeIntentPath + "/" + concurrentMergeIntentId + "/execute",
                   runToken,
                   [TenantId]))
        using (var restartedMergeReplay = await SendAsync(
                   restartedOrchestrator,
                   HttpMethod.Post,
                   mergeIntentPath + "/" + postEffectRevocationIntentId + "/execute",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, restartedConcurrentMergeReplay.StatusCode);
            var concurrentResult = await ReadJsonAsync<JsonElement>(restartedConcurrentMergeReplay);
            Assert.Equal("merged", concurrentResult.GetProperty("state").GetString());
            Assert.Equal(
                "cccccccccccccccccccccccccccccccccccccccc",
                concurrentResult.GetProperty("mergeSha").GetString());
            Assert.Equal(HttpStatusCode.OK, restartedMergeReplay.StatusCode);
            var result = await ReadJsonAsync<JsonElement>(restartedMergeReplay);
            Assert.Equal("merged", result.GetProperty("state").GetString());
            Assert.Equal(
                "cccccccccccccccccccccccccccccccccccccccc",
                result.GetProperty("mergeSha").GetString());
        }
        Assert.Equal(3, sourceControlGitHub.MergeRequestCount);

        var registrationEffectsBeforeRevocation = await ReadChildRegistrationEffectsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        await using (var lockSource = NpgsqlDataSource.Create(_connectionString))
        await using (var lockConnection = await lockSource.OpenConnectionAsync())
        await using (var observationConnection = await lockSource.OpenConnectionAsync())
        await using (var ownerLock = await lockConnection.BeginTransactionAsync())
        {
            await using (var lockRun = new NpgsqlCommand($"""
                SELECT execution_fence
                FROM "{ownerSchema}".accepted_runs
                WHERE project_id = @project AND run_id = @run
                FOR UPDATE
                """, lockConnection, ownerLock))
            {
                lockRun.Parameters.AddWithValue("project", project.ProjectId);
                lockRun.Parameters.AddWithValue("run", RunId);
                Assert.Equal(root.ExecutionFence, await lockRun.ExecuteScalarAsync());
            }

            var blockedRegistration = SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
                runToken,
                new RegisterChildRequest("revoked-while-waiting"));
            var waiting = false;
            using var lockWaitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!waiting)
            {
                await using var wait = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity
                        WHERE @locker = ANY(pg_blocking_pids(pid)))
                    """, observationConnection);
                wait.Parameters.AddWithValue("locker", lockConnection.ProcessID);
                waiting = (bool)(await wait.ExecuteScalarAsync(lockWaitDeadline.Token))!;
                if (!waiting)
                    await Task.Delay(20, lockWaitDeadline.Token);
            }

            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
            await ownerLock.RollbackAsync();
            using var deniedRegistration = await blockedRegistration.WaitAsync(TimeSpan.FromSeconds(20));
            await AssertStatusAsync(deniedRegistration, HttpStatusCode.Forbidden);
        }
        Assert.Equal(
            registrationEffectsBeforeRevocation,
            await ReadChildRegistrationEffectsAsync(_connectionString, ownerSchema, project.ProjectId));
        runnerRole = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Orchestrator);

        using var childResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child"));
        await AssertStatusAsync(childResponse, HttpStatusCode.Created);
        var child = await ReadJsonAsync<RegisteredChild>(childResponse);

        using var childReplay = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child"));
        await AssertStatusAsync(childReplay, HttpStatusCode.Created);
        Assert.Equal(child, await ReadJsonAsync<RegisteredChild>(childReplay));

        using var forgedAdmission = await SendJsonAsync(
            events,
            HttpMethod.Post,
            "/internal/addressed-messages/admit",
            runToken,
            new OwnerOutboundMessage(
                Guid.NewGuid(),
                new SessionIdentity(project.ProjectId, RunId, "root"),
                child.Identity,
                "forged-message",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Question,
                AddressedMessageKind.Question,
                Payload("""{"text":"forged"}"""),
                root.ExecutionFence,
                child.ExecutionFence,
                Guid.NewGuid(),
                null,
                child.PendingRequestId,
                null,
                null,
                null));
        Assert.Equal(HttpStatusCode.BadGateway, forgedAdmission.StatusCode);

        using var sendQuestion = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/messages",
            runToken,
            new CoordinationMessageRequest(
                child.Identity.SessionId,
                "root-question",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.Question,
                AddressedMessageKind.Question,
                Payload("""{"text":"question"}"""),
                RequestId: child.PendingRequestId));
        Assert.True(sendQuestion.StatusCode == HttpStatusCode.OK,
            $"Events admission response: {string.Join("; ", admissions)}; " +
            await sendQuestion.Content.ReadAsStringAsync());
        var sentQuestion = await ReadJsonAsync<CoordinationMessageResult>(sendQuestion);

        using var childBindingResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/owner-binding",
            runToken,
            [TenantId]);
        await AssertStatusAsync(childBindingResponse, HttpStatusCode.OK);
        Assert.True(childBindingResponse.Headers.CacheControl?.NoStore);
        var childBinding = await ReadJsonAsync<CoordinationSessionBinding>(childBindingResponse);
        Assert.False(childBinding.PendingWake);

        using var boundaryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/turn-boundary",
            runToken,
            new TurnBoundaryRequest(child.ExecutionFence, childBinding.StateVersion));
        await AssertStatusAsync(boundaryResponse, HttpStatusCode.OK);
        var boundary = await ReadJsonAsync<TurnBoundaryResult>(boundaryResponse);
        var presented = Assert.IsType<AddressedMessageEnvelope>(boundary.PresentedMessage);
        Assert.NotEqual(Guid.Empty, sentQuestion.OwnerMessageId);
        Assert.Equal("child", presented.Recipient.SessionId);
        Assert.Equal(AddressedMessageStatus.Delivered, presented.Status);
        Assert.Equal("active", boundary.ExecutionState);

        using var secondChildResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/spawn",
            runToken,
            new SpawnSessionRequest(
                "child-two",
                CoordinationSessionKind.ChildWork,
                "child-two-spawn",
                "Execute the confirmed work item.",
                WorkPlanItemId: "implement-1"));
        await AssertStatusAsync(secondChildResponse, HttpStatusCode.Accepted);
        var spawnedChildTwo = await ReadJsonAsync<SpawnedSession>(secondChildResponse);
        var childTwo = new RegisteredChild(
            spawnedChildTwo.Node.Identity,
            "root",
            spawnedChildTwo.PendingRequestId,
            spawnedChildTwo.Node.ExecutionFence);
        using var childTwoBindingResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child-two/owner-binding",
            runToken,
            [TenantId]);
        await AssertStatusAsync(childTwoBindingResponse, HttpStatusCode.OK);
        var childTwoBinding = await ReadJsonAsync<CoordinationSessionBinding>(childTwoBindingResponse);
        using var childTwoBoundaryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child-two/turn-boundary",
            runToken,
            new TurnBoundaryRequest(childTwo.ExecutionFence, childTwoBinding.StateVersion));
        await AssertStatusAsync(childTwoBoundaryResponse, HttpStatusCode.OK);
        Assert.Equal(
            "active",
            (await ReadJsonAsync<TurnBoundaryResult>(childTwoBoundaryResponse)).ExecutionState);
        using var overConcurrentLimit = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child-three"));
        Assert.Equal(HttpStatusCode.Conflict, overConcurrentLimit.StatusCode);
        Assert.Contains(
            "run_concurrent_child_limit_exceeded",
            await overConcurrentLimit.Content.ReadAsStringAsync());

        long runtimeOwnerDecisionStateVersion;
        var selectionPause = new RuntimeOwnerContextSelectionPause();
        await using (var runtimeOwnerFactory = new OrchestratorIntegrationFactory(
                         _connectionString,
                         ownerSchema,
                         signingKey,
                         () => new RuntimeOwnerContextSelectionBarrierHandler(
                             projects.CreateHandler(), selectionPause),
                         () => eventsFactory.Server.CreateHandler(),
                         cacheObjectStore,
                         sandboxProvider))
        using (var runtimeOwnerClient = runtimeOwnerFactory.CreateClient(
                   new WebApplicationFactoryClientOptions
                   {
                       BaseAddress = new Uri("https://orchestrator.test")
                   }))
        {
            var runtimeOwnerPath =
                $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child-two/runtime-owner-context";
            var runtimeOwnerTask = SendAsync(
                runtimeOwnerClient, HttpMethod.Get, runtimeOwnerPath, runToken, [TenantId]);
            try
            {
                await selectionPause.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var ownerBeforeDecisionChange = await ReadRuntimeOwnerRowAsync(
                    _connectionString, ownerSchema, project.ProjectId, "child-two");
                Assert.Equal("implement-1", ownerBeforeDecisionChange.WorkPlanItemId);

                using var runtimeOwnerDecisionStateResponse = await SendAsync(
                    orchestrator, HttpMethod.Get, decisionStatePath, runToken, [TenantId]);
                await AssertStatusAsync(runtimeOwnerDecisionStateResponse, HttpStatusCode.OK);
                var runtimeOwnerDecisionState =
                    await ReadJsonAsync<CoordinatorDecisionStateView>(runtimeOwnerDecisionStateResponse);
                runtimeOwnerDecisionStateVersion = runtimeOwnerDecisionState.StateVersion;
                using var pendingOwnerContextGate = await SendJsonAsync(
                    orchestrator,
                    HttpMethod.Post,
                    $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/questions",
                    runToken,
                    new AskCoordinatorQuestionRequest(
                        runtimeOwnerDecisionStateVersion,
                        "runtime-owner-context-question",
                        "runtime-owner-context-gate",
                        "execution-detail",
                        "Should the approved work item continue?",
                        ["continue"],
                        true));
                Assert.Equal(HttpStatusCode.OK, pendingOwnerContextGate.StatusCode);
                var pendingGateResult =
                    await ReadJsonAsync<CoordinatorDecisionOperationResponse>(pendingOwnerContextGate);
                Assert.True(pendingGateResult.Accepted);
                Assert.Equal(runtimeOwnerDecisionStateVersion + 1, pendingGateResult.StateVersion);
                Assert.Equal("runtime-owner-context-gate", pendingGateResult.PendingGate?.RequestId);
                Assert.Equal(
                    ownerBeforeDecisionChange,
                    await ReadRuntimeOwnerRowAsync(
                        _connectionString, ownerSchema, project.ProjectId, "child-two"));
            }
            finally
            {
                selectionPause.Release();
            }

            using var staleRuntimeOwnerContext =
                await runtimeOwnerTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Conflict, staleRuntimeOwnerContext.StatusCode);
        }

        using (var answerOwnerContextGate = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/runtime-owner-context-gate/answer",
                   runToken,
                   new AnswerCoordinatorGateRequest(
                       runtimeOwnerDecisionStateVersion + 1,
                       "runtime-owner-context-answer",
                       "continue",
                       null)))
        {
            Assert.Equal(HttpStatusCode.OK, answerOwnerContextGate.StatusCode);
            var answeredOwnerContextGate =
                await ReadJsonAsync<CoordinatorDecisionOperationResponse>(answerOwnerContextGate);
            Assert.True(answeredOwnerContextGate.Accepted);
            Assert.Equal(runtimeOwnerDecisionStateVersion + 2, answeredOwnerContextGate.StateVersion);
            Assert.Null(answeredOwnerContextGate.PendingGate);
        }

        var mappedActiveChild = await ReadRuntimeOwnerRowAsync(
            _connectionString, ownerSchema, project.ProjectId, "child-two");
        Assert.Equal("implement-1", mappedActiveChild.WorkPlanItemId);
        await using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await ownerDatabase.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT node_kind, lifecycle_state
            FROM "{ownerSchema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = 'child-two'
            """, connection))
        {
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("child_work", reader.GetString(0));
            Assert.Equal("active", reader.GetString(1));
        }
        using (var availableOwnerContext = await SendAsync(
                   orchestrator,
                   HttpMethod.Get,
                   $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child-two/runtime-owner-context",
                   runToken,
                   [TenantId]))
        {
            await AssertStatusAsync(availableOwnerContext, HttpStatusCode.OK);
            Assert.Equal(
                mappedActiveChild.ExecutionFence,
                (await ReadJsonAsync<RuntimeOwnerContext>(availableOwnerContext)).ExecutionFence);
        }

        var originalContextRow = await ReadSelectionContextRowAsync(
            _connectionString, ownerSchema, project.ProjectId, mappedActiveChild.ExecutionFence)
            ?? throw new InvalidOperationException("Expected the current-fence selection context.");
        var originalContextBinding = await ReadSelectionContextBindingSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId, mappedActiveChild.ExecutionFence);
        var acceptedSelectionSnapshot = await ReadAcceptedSelectionSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId);
        await using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await ownerDatabase.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var disableTrigger = new NpgsqlCommand($"""
                ALTER TABLE "{ownerSchema}".coordinator_run_selection_context_versions
                DISABLE TRIGGER coordinator_run_selection_context_versions_immutable
                """, connection, transaction))
                await disableTrigger.ExecuteNonQueryAsync();
            try
            {
                await using var removeContext = new NpgsqlCommand($"""
                    DELETE FROM "{ownerSchema}".coordinator_run_selection_context_versions
                    WHERE project_id = @project AND run_id = @run AND execution_fence = @fence
                    """, connection, transaction);
                removeContext.Parameters.AddWithValue("project", project.ProjectId);
                removeContext.Parameters.AddWithValue("run", RunId);
                removeContext.Parameters.AddWithValue("fence", mappedActiveChild.ExecutionFence);
                Assert.Equal(1, await removeContext.ExecuteNonQueryAsync());
            }
            finally
            {
                await using var enableTrigger = new NpgsqlCommand($"""
                    ALTER TABLE "{ownerSchema}".coordinator_run_selection_context_versions
                    ENABLE TRIGGER coordinator_run_selection_context_versions_immutable
                    """, connection, transaction);
                await enableTrigger.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        var effectsBeforeMissingContext = await ReadOwnerEffectCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        using (var missingContext = await SendAsync(
                   orchestrator,
                   HttpMethod.Get,
                   $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child-two/runtime-owner-context",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.Conflict, missingContext.StatusCode);
            Assert.Contains(
                "runtime_owner_context_unavailable",
                await missingContext.Content.ReadAsStringAsync());
        }
        Assert.Equal(
            effectsBeforeMissingContext,
            await ReadOwnerEffectCountsAsync(_connectionString, ownerSchema, project.ProjectId));
        await using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await ownerDatabase.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{ownerSchema}".coordinator_run_selection_context_versions
                 WHERE project_id = @project AND run_id = @run AND execution_fence = @fence),
                (SELECT accepted_selection IS NOT NULL FROM "{ownerSchema}".accepted_runs
                 WHERE project_id = @project AND run_id = @run),
                (SELECT tgenabled FROM pg_trigger
                 WHERE tgrelid = '"{ownerSchema}".coordinator_run_selection_context_versions'::regclass
                   AND tgname = 'coordinator_run_selection_context_versions_immutable')::text
            """, connection))
        {
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("fence", mappedActiveChild.ExecutionFence);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal("O", reader.GetString(2));
        }
        Assert.Equal(
            acceptedSelectionSnapshot,
            await ReadAcceptedSelectionSnapshotAsync(_connectionString, ownerSchema, project.ProjectId));
        await using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await ownerDatabase.OpenConnectionAsync())
        await using (var restoreContext = new NpgsqlCommand($"""
            INSERT INTO "{ownerSchema}".coordinator_run_selection_context_versions
            SELECT (jsonb_populate_record(
                NULL::"{ownerSchema}".coordinator_run_selection_context_versions, @context::jsonb)).*
            """, connection))
        {
            restoreContext.Parameters.AddWithValue("context", originalContextRow);
            Assert.Equal(1, await restoreContext.ExecuteNonQueryAsync());
        }
        Assert.Equal(
            originalContextBinding,
            await ReadSelectionContextBindingSnapshotAsync(
                _connectionString, ownerSchema, project.ProjectId, mappedActiveChild.ExecutionFence));

        using var boundaryRetryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/turn-boundary",
            runToken,
            new TurnBoundaryRequest(child.ExecutionFence, childBinding.StateVersion));
        await AssertStatusAsync(boundaryRetryResponse, HttpStatusCode.OK);
        var boundaryRetry = await ReadJsonAsync<TurnBoundaryResult>(boundaryRetryResponse);
        Assert.Equal(boundary.StateVersion, boundaryRetry.StateVersion);
        Assert.Equal(boundary.PresentedMessage?.MessageId, boundaryRetry.PresentedMessage?.MessageId);

        using var acknowledgmentResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/messages/{presented.MessageId:D}/acknowledge",
            runToken,
            new AddressedMessageClaimFenceRequest(presented.ClaimFence));
        await AssertStatusAsync(acknowledgmentResponse, HttpStatusCode.OK);
        var acknowledged = await ReadJsonAsync<AddressedMessageEnvelope>(acknowledgmentResponse);
        Assert.Equal(AddressedMessageStatus.Acknowledged, acknowledged.Status);
        Assert.NotNull(acknowledged.AcknowledgedAt);
        Assert.Equal("pending", await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var sendReply = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/messages",
            runToken,
            new CoordinationMessageRequest(
                "root",
                "child-reply",
                AddressedMessageDeliveryMode.Immediate,
                AddressedMessagePurpose.NeedsInput,
                AddressedMessageKind.Question,
                Payload("""{"text":"reply"}"""),
                ThreadId: presented.ThreadId,
                ReplyToId: sentQuestion.OwnerMessageId,
                RequestId: child.PendingRequestId,
                ReplyCorrelationId: child.PendingRequestId));
        await AssertStatusAsync(sendReply, HttpStatusCode.OK);
        Assert.Equal("input_available",
            await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var rootBindingResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/owner-binding",
            runToken,
            [TenantId]);
        await AssertStatusAsync(rootBindingResponse, HttpStatusCode.OK);
        var rootBinding = await ReadJsonAsync<CoordinationSessionBinding>(rootBindingResponse);
        Assert.True(rootBinding.PendingWake);

        using var rootBoundaryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/turn-boundary",
            runToken,
            new TurnBoundaryRequest(root.ExecutionFence, rootBinding.StateVersion));
        await AssertStatusAsync(rootBoundaryResponse, HttpStatusCode.OK);
        var rootBoundary = await ReadJsonAsync<TurnBoundaryResult>(rootBoundaryResponse);
        var replyPresented = Assert.IsType<AddressedMessageEnvelope>(rootBoundary.PresentedMessage);
        Assert.Equal(AddressedMessageStatus.Delivered, replyPresented.Status);
        var notification = Assert.Single(rootBoundary.ParentNotifications);
        Assert.Equal("child", notification.ChildSessionId);
        Assert.Equal(AddressedMessagePurpose.NeedsInput, notification.Purpose);
        Assert.True(notification.WakesParent);

        var rootEventsBasePath =
            $"/internal/addressed-messages/projects/{project.ProjectId}/runs/{RunId}/sessions/root";
        using var outOfBoundaryClaim = await SendAsync(
            events, HttpMethod.Post, $"{rootEventsBasePath}/claim", runToken, [TenantId]);
        Assert.Equal(HttpStatusCode.Conflict, outOfBoundaryClaim.StatusCode);
        using var outOfBoundaryPresentation = await SendJsonAsync(
            events,
            HttpMethod.Post,
            $"{rootEventsBasePath}/messages/{replyPresented.MessageId:D}/present",
            runToken,
            new AddressedMessageClaimFenceRequest(replyPresented.ClaimFence));
        Assert.Equal(HttpStatusCode.Conflict, outOfBoundaryPresentation.StatusCode);

        using var replyAcknowledgment = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/messages/{replyPresented.MessageId:D}/acknowledge",
            runToken,
            new AddressedMessageClaimFenceRequest(replyPresented.ClaimFence));
        await AssertStatusAsync(replyAcknowledgment, HttpStatusCode.OK);
        Assert.Equal("input_available",
            await ReadGateStateAsync(_connectionString, ownerSchema, child.PendingRequestId));

        using var replyJournalResponse = await SendAsync(
            events,
            HttpMethod.Get,
            "/internal/sessions/child/events",
            runToken,
            [TenantId]);
        await AssertStatusAsync(replyJournalResponse, HttpStatusCode.OK);
        using var replyJournal = JsonDocument.Parse(await replyJournalResponse.Content.ReadAsStringAsync());
        var replyJournalEvent = Assert.Single(replyJournal.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("addressedMessage", replyJournalEvent.GetProperty("kind").GetString());
        Assert.Equal(
            replyPresented.MessageId,
            replyJournalEvent.GetProperty("payload").GetProperty("messageId").GetGuid());

        using var notificationAcknowledgment = await SendAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/notifications/{notification.NotificationId:D}/acknowledge",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.NoContent, notificationAcknowledgment.StatusCode);

        using var senderReplay = await SendAsync(
            events,
            HttpMethod.Get,
            "/internal/sessions/root/events",
            runToken,
            [TenantId]);
        await AssertStatusAsync(senderReplay, HttpStatusCode.OK);
        using var journal = JsonDocument.Parse(await senderReplay.Content.ReadAsStringAsync());
        var journalEvent = Assert.Single(
            journal.RootElement.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "addressedMessage");
        Assert.Equal("addressedMessage", journalEvent.GetProperty("kind").GetString());
        Assert.Equal(
            presented.MessageId,
            journalEvent.GetProperty("payload").GetProperty("messageId").GetGuid());

        async Task CompleteChildForForkCapacityAsync(RegisteredChild registeredChild)
        {
            using var bindingResponse = await SendAsync(
                orchestrator,
                HttpMethod.Get,
                $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/{registeredChild.Identity.SessionId}/owner-binding",
                runToken,
                [TenantId]);
            await AssertStatusAsync(bindingResponse, HttpStatusCode.OK);
            var binding = await ReadJsonAsync<CoordinationSessionBinding>(bindingResponse);
            var expectedStateVersion = binding.StateVersion;
            if (binding.TurnState == "idle")
            {
                using var boundaryResponse = await SendJsonAsync(
                    orchestrator,
                    HttpMethod.Post,
                    $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/{registeredChild.Identity.SessionId}/turn-boundary",
                    runToken,
                    new TurnBoundaryRequest(registeredChild.ExecutionFence, binding.StateVersion));
                await AssertStatusAsync(boundaryResponse, HttpStatusCode.OK);
                expectedStateVersion = (await ReadJsonAsync<TurnBoundaryResult>(boundaryResponse)).StateVersion;
            }
            else
            {
                Assert.Equal("active", binding.TurnState);
            }

            using var completionResponse = await SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/{registeredChild.Identity.SessionId}/turn-completion",
                runToken,
                new FinishTurnRequest(
                    registeredChild.ExecutionFence,
                    expectedStateVersion,
                    LogicalTurnCompletion.Completed));
            await AssertStatusAsync(completionResponse, HttpStatusCode.OK);
        }
        await CompleteChildForForkCapacityAsync(child);
        await CompleteChildForForkCapacityAsync(childTwo);

        var forkEventId = Guid.NewGuid();
        using var forkEventAppend = await SendJsonAsync(
            events,
            HttpMethod.Post,
            "/internal/sessions/root/events",
            runToken,
            new AppendSessionEvent(
                forkEventId,
                SessionsContractVersions.CurrentSchemaVersion,
                SessionsContractVersions.CurrentEventVersion,
                new TurnSessionPayload(
                    "assistant",
                    new SessionObjectReference(new ObjectKey("turns/fork-source"), "turns"))));
        await AssertStatusAsync(forkEventAppend, HttpStatusCode.Created);
        using var forkSourceReplay = await SendAsync(
            events, HttpMethod.Get, "/internal/sessions/root/events?limit=100", runToken, [TenantId]);
        await AssertStatusAsync(forkSourceReplay, HttpStatusCode.OK);
        using var forkSourceDocument =
            JsonDocument.Parse(await forkSourceReplay.Content.ReadAsStringAsync());
        var forkSourceEvents = forkSourceDocument.RootElement.GetProperty("events");
        Assert.Equal(forkEventId, forkSourceEvents[forkSourceEvents.GetArrayLength() - 1]
            .GetProperty("eventId").GetGuid());
        var forkCursor = Assert.IsType<string>(
            forkSourceDocument.RootElement.GetProperty("nextCursor").GetString());

        string originalForkRetention;
        async Task AssertNoEventsForkEffectsAsync(string targetSessionId)
        {
            await using var database = NpgsqlDataSource.Create(_connectionString);
            await using var connection = await database.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT
                    (SELECT count(*) FROM "{eventsSchema}".sessions
                     WHERE project_id = @project AND run_id = @run AND session_id = @target),
                    (SELECT count(*) FROM "{eventsSchema}".session_fork_lineage
                     WHERE project_id = @project AND run_id = @run AND target_session_id = @target),
                    (SELECT retain_until::text FROM "{eventsSchema}".session_object_references
                     WHERE project_id = @project AND run_id = @run AND event_id = @event)
                """, connection);
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("target", targetSessionId);
            command.Parameters.AddWithValue("event", forkEventId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(originalForkRetention, reader.GetString(2));
        }

        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT retain_until::text
            FROM "{eventsSchema}".session_object_references
            WHERE project_id = @project AND run_id = @run AND event_id = @event
            """, connection))
        {
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("event", forkEventId);
            originalForkRetention = (string)(await command.ExecuteScalarAsync() ??
                throw new InvalidOperationException("Expected bounded source-object retention."));
        }

        using var directFork = await SendJsonAsync(
            events,
            HttpMethod.Post,
            "/internal/sessions/root/fork",
            runToken,
            new SessionForkRequest(
                "fork-without-owner-reservation",
                forkEventId,
                forkCursor,
                "fork-without-owner-reservation"));
        await AssertStatusAsync(directFork, HttpStatusCode.Conflict);
        Assert.True(directFork.Headers.CacheControl?.NoStore);
        await AssertNoEventsForkEffectsAsync("fork-without-owner-reservation");

        var forkRequest = new CoordinationSessionForkRequest(
            root.ExecutionFence,
            "fork-owner-admitted",
            "fork-owner-target",
            forkEventId,
            forkCursor,
            CoordinationSessionKind.ChildWork);
        var firstOwnerForkTask = SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
            runToken,
            forkRequest);
        CoordinationSessionForkResult ownerForkResult;
        CoordinationSessionForkResult ownerForkReplayResult;
        try
        {
            await ownerForkRaceGate.WaitUntilFirstResponsePausedAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            var secondOwnerForkTask = SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
                runToken,
                forkRequest);
            await ownerForkRaceGate.WaitUntilSecondRequestPausedAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));

            ownerForkRaceGate.ReleaseFirstResponse();
            using (var ownerFork = await firstOwnerForkTask)
            {
                await AssertStatusAsync(ownerFork, HttpStatusCode.Created);
                ownerForkResult = await ReadJsonAsync<CoordinationSessionForkResult>(ownerFork);
                Assert.Equal(
                    CoordinationForkRegistrationState.Registered,
                    ownerForkResult.RegistrationState);
                Assert.Equal(forkEventId, ownerForkResult.Lineage?.SourceEventId);
                Assert.Equal(forkCursor, ownerForkResult.Lineage?.SourceCursor);
            }

            ownerForkRaceGate.ReleaseSecondRequest();
            using var ownerForkReplay = await secondOwnerForkTask;
            await AssertStatusAsync(ownerForkReplay, HttpStatusCode.OK);
            ownerForkReplayResult =
                await ReadJsonAsync<CoordinationSessionForkResult>(ownerForkReplay);
            Assert.True(ownerForkReplayResult.IsDuplicate);
            Assert.Equal(ownerForkResult.CommandId, ownerForkReplayResult.CommandId);
        }
        finally
        {
            ownerForkRaceGate.ReleaseFirstResponse();
            ownerForkRaceGate.ReleaseSecondRequest();
        }

        await using (var database = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{eventsSchema}".sessions
                 WHERE project_id = @project AND run_id = @run AND session_id = @target),
                (SELECT count(*) FROM "{eventsSchema}".session_fork_lineage
                 WHERE project_id = @project AND run_id = @run AND target_session_id = @target),
                (SELECT retain_until::text FROM "{eventsSchema}".session_object_references
                 WHERE project_id = @project AND run_id = @run AND event_id = @event),
                (SELECT count(*) FROM "{ownerSchema}".coordination_sessions
                 WHERE project_id = @project AND run_id = @run
                   AND session_id = @target AND parent_session_id = 'root'),
                (SELECT count(*) FROM "{ownerSchema}".outbox_events
                 WHERE id = @command AND event_type = 'orchestrator.session.forked')
            """, connection))
        {
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("target", forkRequest.TargetSessionId);
            command.Parameters.AddWithValue("event", forkEventId);
            command.Parameters.AddWithValue("command", ownerForkResult.CommandId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(originalForkRetention, reader.GetString(2));
            Assert.Equal(1L, reader.GetInt64(3));
            Assert.Equal(1L, reader.GetInt64(4));
        }

        async Task RestoreRunnerRoleAsync()
        {
            runnerRole = await AssignRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerMembership.MembershipId,
                ProjectAuthorityResourceType.Project,
                project.ProjectId,
                ProjectAuthorityRole.Orchestrator);
        }

        var latePrepareRequest = forkRequest with
        {
            IdempotencyKey = "fork-owner-prepare-late-revoke",
            TargetSessionId = "fork-owner-prepare-late-revoke"
        };
        using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var lockConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var observationConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var ownerLock = await lockConnection.BeginTransactionAsync())
        {
            await using (var lockCommands = new NpgsqlCommand($"""
                LOCK TABLE "{ownerSchema}".coordination_tree_commands IN SHARE MODE
                """, lockConnection, ownerLock))
                await lockCommands.ExecuteNonQueryAsync();

            var latePrepareTask = SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
                runToken,
                latePrepareRequest);
            try
            {
                await WaitForBlockedSqlAsync(
                    observationConnection,
                    lockConnection.ProcessID,
                    $"""INSERT INTO "{ownerSchema}".coordination_tree_commands""");
                await RevokeRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerRole.AssignmentId,
                    runnerRole.Revision);
            }
            finally
            {
                await ownerLock.RollbackAsync();
            }

            using var deniedPrepare = await latePrepareTask.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertStatusAsync(deniedPrepare, HttpStatusCode.Forbidden);
        }
        await AssertNoEventsForkEffectsAsync(latePrepareRequest.TargetSessionId);
        await using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await ownerDatabase.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{ownerSchema}".coordination_tree_commands
            WHERE project_id = @project AND run_id = @run
              AND requested_target_session_id = @target
            """, connection))
        {
            command.Parameters.AddWithValue("project", project.ProjectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("target", latePrepareRequest.TargetSessionId);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        await RestoreRunnerRoleAsync();

        var lateCompleteRequest = forkRequest with
        {
            IdempotencyKey = "fork-owner-complete-late-revoke",
            TargetSessionId = "fork-owner-complete-late-revoke"
        };
        var ownerStreamId = $"coordination/{project.ProjectId}/{RunId}/root";
        using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var lockConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var observationConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var ownerLock = await lockConnection.BeginTransactionAsync())
        {
            await using (var lockStream = new NpgsqlCommand($"""
                SELECT stream_id FROM "{ownerSchema}".outbox_streams
                WHERE stream_id = @stream FOR UPDATE
                """, lockConnection, ownerLock))
            {
                lockStream.Parameters.AddWithValue("stream", ownerStreamId);
                Assert.Equal(ownerStreamId, await lockStream.ExecuteScalarAsync());
            }

            var lateCompleteTask = SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
                runToken,
                lateCompleteRequest);
            try
            {
                await WaitForBlockedSqlAsync(
                    observationConnection,
                    lockConnection.ProcessID,
                    $"""UPDATE "{ownerSchema}".outbox_streams SET last_sequence""");
                await RevokeRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerRole.AssignmentId,
                    runnerRole.Revision);
            }
            finally
            {
                await ownerLock.RollbackAsync();
            }

            using var deniedComplete = await lateCompleteTask.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertStatusAsync(deniedComplete, HttpStatusCode.Forbidden);
            var unregistered = await ReadJsonAsync<CoordinationSessionForkResult>(deniedComplete);
            Assert.Equal(CoordinationForkRegistrationState.Unregistered, unregistered.RegistrationState);
            Assert.Equal("run_selection_permission_denied", unregistered.UnavailableCode);

            await using var verifyDatabase = NpgsqlDataSource.Create(_connectionString);
            await using var connection = await verifyDatabase.OpenConnectionAsync();
            await using var verify = new NpgsqlCommand($"""
                SELECT
                    (SELECT count(*) FROM "{ownerSchema}".coordination_sessions
                     WHERE project_id = @project AND run_id = @run AND session_id = @target),
                    (SELECT count(*) FROM "{ownerSchema}".coordination_requests
                     WHERE project_id = @project AND run_id = @run
                       AND recipient_session_id = @target),
                    (SELECT count(*) FROM "{ownerSchema}".outbox_events
                     WHERE id = @command AND event_type = 'orchestrator.session.forked'),
                    (SELECT count(*) FROM "{ownerSchema}".outbox_events
                     WHERE id = @command AND event_type = 'orchestrator.session.fork_unregistered'),
                    (SELECT result ->> 'registrationState'
                     FROM "{ownerSchema}".coordination_tree_commands WHERE command_id = @command),
                    (SELECT count(*) FROM "{eventsSchema}".sessions
                     WHERE project_id = @project AND run_id = @run AND session_id = @target),
                    (SELECT count(*) FROM "{eventsSchema}".session_fork_lineage
                     WHERE project_id = @project AND run_id = @run AND target_session_id = @target)
                """, connection);
            verify.Parameters.AddWithValue("project", project.ProjectId);
            verify.Parameters.AddWithValue("run", RunId);
            verify.Parameters.AddWithValue("target", lateCompleteRequest.TargetSessionId);
            verify.Parameters.AddWithValue("command", unregistered.CommandId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
            Assert.Equal("unregistered", reader.GetString(4));
            Assert.Equal(1L, reader.GetInt64(5));
            Assert.Equal(1L, reader.GetInt64(6));
        }
        await RestoreRunnerRoleAsync();

        using (var ownerDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var lockConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var observationConnection = await ownerDatabase.OpenConnectionAsync())
        await using (var ownerLock = await lockConnection.BeginTransactionAsync())
        {
            await using (var lockCommand = new NpgsqlCommand($"""
                SELECT command_id FROM "{ownerSchema}".coordination_tree_commands
                WHERE command_id = @command FOR UPDATE
                """, lockConnection, ownerLock))
            {
                lockCommand.Parameters.AddWithValue("command", ownerForkResult.CommandId);
                Assert.Equal(ownerForkResult.CommandId, await lockCommand.ExecuteScalarAsync());
            }

            var blockedReplay = SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
                runToken,
                forkRequest);
            try
            {
                await WaitForBlockedSqlAsync(
                    observationConnection,
                    lockConnection.ProcessID,
                    $"""coordination_tree_commands%FOR UPDATE""");
                await RevokeRoleAsync(
                    projects.PrivilegedFixtureDataSource,
                    runnerRole.AssignmentId,
                    runnerRole.Revision);
            }
            finally
            {
                await ownerLock.RollbackAsync();
            }

            using var deniedReplay = await blockedReplay.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertStatusAsync(deniedReplay, HttpStatusCode.Forbidden);
        }
        await RestoreRunnerRoleAsync();

        var (fenceConflictIntentId, _) =
            await CreateApprovedMergeIntentAsync("source-control-merge-execution-fence-race");
        var currentMergeGrantBeforeFence = await ReadMergeGrantAndFenceStateAsync(fenceConflictIntentId);
        Assert.True(currentMergeGrantBeforeFence.IsCurrent);
        Assert.Equal("active", currentMergeGrantBeforeFence.State);
        Assert.Equal(
            currentMergeGrantBeforeFence.ApprovalStateVersion,
            currentMergeGrantBeforeFence.GrantStateVersion);
        Assert.Equal(
            currentMergeGrantBeforeFence.ApprovalStateVersion,
            currentMergeGrantBeforeFence.CurrentStateVersion);
        Assert.Equal(
            currentMergeGrantBeforeFence.IntentFence,
            currentMergeGrantBeforeFence.CurrentFence);
        var mergeCountBeforeFenceRace = sourceControlGitHub.MergeRequestCount;
        await using var fenceLockSource = NpgsqlDataSource.Create(_connectionString);
        await using var fenceLockConnection = await fenceLockSource.OpenConnectionAsync();
        await using (var acquireFenceRepositoryLock = new NpgsqlCommand(
                         "SELECT pg_advisory_lock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                         fenceLockConnection))
        {
            acquireFenceRepositoryLock.Parameters.AddWithValue("repository", "octo/agentweaver");
            await acquireFenceRepositoryLock.ExecuteNonQueryAsync();
        }
        var fenceLockHeld = true;
        var fenceConflictExecution = SendAsync(
            orchestrator,
            HttpMethod.Post,
            mergeIntentPath + "/" + fenceConflictIntentId + "/execute",
            runToken,
            [TenantId]);
        Assert.True(
            await WaitForSourceControlAdvisoryLockWaitAsync(fenceLockSource),
            "the pre-effect fence transition must follow a merge request observed waiting on the PostgreSQL repository lock");

        using var preFailureDecisionResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions",
            runToken,
            [TenantId]);
        await AssertStatusAsync(preFailureDecisionResponse, HttpStatusCode.OK);
        var preFailureDecision =
            await ReadJsonAsync<CoordinatorDecisionStateView>(preFailureDecisionResponse);
        Assert.True(preFailureDecision.CanDispatch);
        Assert.Null(preFailureDecision.PendingGate);

        long pendingGateStateVersion;
        using (var pendingFailureGate = await SendJsonAsync(
                   orchestrator,
                   HttpMethod.Post,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/questions",
                   runToken,
                   new AskCoordinatorQuestionRequest(
                       preFailureDecision.StateVersion,
                       "failure-pending-gate-question",
                       "failure-pending-gate-once",
                       "execution-detail",
                       "Should the recovered work continue?",
                       ["continue"],
                       true)))
        {
            await AssertStatusAsync(pendingFailureGate, HttpStatusCode.OK);
            var pendingGateResult =
                await ReadJsonAsync<CoordinatorDecisionOperationResponse>(pendingFailureGate);
            Assert.True(pendingGateResult.Accepted);
            Assert.Equal("failure-pending-gate-once", pendingGateResult.PendingGate?.RequestId);
            pendingGateStateVersion = pendingGateResult.StateVersion;
        }

        var historicalFence = preFailureDecision.ExecutionFence;
        var historicalRows = await ReadOwnerFenceHistoryAsync(
            _connectionString, ownerSchema, project.ProjectId, historicalFence);
        var contextBindingBeforeFailure = await ReadSelectionContextBindingSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId, historicalFence);
        Assert.NotNull(contextBindingBeforeFailure);
        var grantsBeforeFailure = await ReadGrantStateCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.True(grantsBeforeFailure.Total > 0);

        using var runStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(runStatusResponse, HttpStatusCode.OK);
        var runStatus = await ReadJsonAsync<OwnerRunStatus>(runStatusResponse);
        using var rootStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(rootStatusResponse, HttpStatusCode.OK);
        var rootStatus = await ReadJsonAsync<SessionStatusSnapshot>(rootStatusResponse);
        var failureRequest = new ReportRunFailureRequest(
            runStatus.ExecutionFence,
            runStatus.StateVersion,
            rootStatus.StateVersion,
            "integration-runtime-failure",
            OwnerRunFailureState.Indeterminate,
            "runtime_lost",
            "sdk-turn-unknown");
        HttpResponseMessage failureResponse;
        try
        {
            failureResponse = await SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/turn-failure",
                runToken,
                failureRequest);
        }
        finally
        {
            if (fenceLockHeld)
            {
                await using var releaseFenceRepositoryLock = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                    fenceLockConnection);
                releaseFenceRepositoryLock.Parameters.AddWithValue("repository", "octo/agentweaver");
                Assert.True((bool)(await releaseFenceRepositoryLock.ExecuteScalarAsync())!);
                fenceLockHeld = false;
            }
        }
        RunExecutionTransitionResult failure;
        using (failureResponse)
        {
            await AssertStatusAsync(failureResponse, HttpStatusCode.OK);
            failure = await ReadJsonAsync<RunExecutionTransitionResult>(failureResponse);
        }
        using (var fenceConflictResponse =
               await fenceConflictExecution.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            var failureCode = await AssertPreEffectConflictAsync(
                fenceConflictResponse,
                fenceConflictIntentId,
                mergeCountBeforeFenceRace);
            Assert.Equal("source_control_run_binding_changed", failureCode);
        }
        var supersededMergeGrant = await ReadMergeGrantAndFenceStateAsync(fenceConflictIntentId);
        Assert.Equal(currentMergeGrantBeforeFence.GrantId, supersededMergeGrant.GrantId);
        Assert.Equal(currentMergeGrantBeforeFence.Revision, supersededMergeGrant.Revision);
        Assert.False(supersededMergeGrant.IsCurrent);
        Assert.Equal("superseded", supersededMergeGrant.State);
        Assert.True(supersededMergeGrant.CurrentStateVersion > supersededMergeGrant.ApprovalStateVersion);
        Assert.True(supersededMergeGrant.CurrentFence > supersededMergeGrant.IntentFence);
        using (var currentProjectsSelection = await SendAsync(
                   projects.Client,
                   HttpMethod.Get,
                   $"/api/projects/{project.ProjectId}/runs/{RunId}/selection",
                   runToken,
                   [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, currentProjectsSelection.StatusCode);
            var currentSelectionJson = await currentProjectsSelection.Content.ReadAsStringAsync();
            Assert.Equal(acceptedProjectsSelection, currentSelectionJson);
            Assert.Equal(
                acceptedProjectsSelectionHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(currentSelectionJson))));
        }
        Assert.Equal("indeterminate", failure.State);
        Assert.True(failure.ExecutionFence > runStatus.ExecutionFence);
        Assert.Equal("runtime_lost", failure.CauseCode);
        Assert.Equal("sdk-turn-unknown", failure.Reference);

        using var failedRunStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(failedRunStatusResponse, HttpStatusCode.OK);
        var failedRunStatus = await ReadJsonAsync<OwnerRunStatus>(failedRunStatusResponse);
        Assert.Equal("indeterminate", failedRunStatus.ExecutionState);
        Assert.Equal("runtime_lost", failedRunStatus.CauseCode);
        Assert.Equal("sdk-turn-unknown", failedRunStatus.Reference);
        using var failedRootStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(failedRootStatusResponse, HttpStatusCode.OK);
        var failedRootStatus = await ReadJsonAsync<SessionStatusSnapshot>(failedRootStatusResponse);
        Assert.Equal(failure.ExecutionFence, failedRootStatus.ExecutionFence);
        Assert.Equal(CoordinationActivityState.Unknown, failedRootStatus.Activity);
        Assert.Equal("unavailable", failedRootStatus.RuntimeEffectsState);
        Assert.Equal("indeterminate", failedRootStatus.RunExecution.State);
        Assert.Equal("runtime_lost", failedRootStatus.RunExecution.CauseCode);
        Assert.Equal("sdk-turn-unknown", failedRootStatus.RunExecution.Reference);
        using var failedDecisionResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions",
            runToken,
            [TenantId]);
        await AssertStatusAsync(failedDecisionResponse, HttpStatusCode.OK);
        var failedDecision = await ReadJsonAsync<CoordinatorDecisionStateView>(failedDecisionResponse);
        Assert.Equal(failure.ExecutionFence, failedDecision.ExecutionFence);
        Assert.Equal(pendingGateStateVersion + 1, failedDecision.StateVersion);
        Assert.Null(failedDecision.PendingGate);
        Assert.True(failedDecision.CanDispatch);
        Assert.True(failedDecision.OutcomeConfirmed);
        Assert.True(failedDecision.WorkflowConfirmed);
        var failedDecisionRow = await ReadLatestDecisionSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(failure.ExecutionFence, failedDecisionRow.ExecutionFence);
        Assert.Equal(failedDecision.StateVersion, failedDecisionRow.StateVersion);
        Assert.Equal(failure.ExecutionFence, failedDecisionRow.EnvelopeFence);
        Assert.True(failedDecisionRow.PendingGateMissing);
        Assert.True(failedDecisionRow.ConfirmedWorkPlanPresent);
        Assert.True(failedDecisionRow.OutcomeConfirmed);
        Assert.True(failedDecisionRow.WorkflowConfirmed);
        Assert.Equal("cancelled", await ReadCoordinatorGateStateAsync(
            _connectionString, ownerSchema, project.ProjectId, "failure-pending-gate-once"));
        Assert.Equal(
            historicalRows.Decisions,
            (await ReadOwnerFenceHistoryAsync(
                _connectionString, ownerSchema, project.ProjectId, historicalFence)).Decisions);
        Assert.Equal(
            historicalRows.Contexts,
            (await ReadOwnerFenceHistoryAsync(
                _connectionString, ownerSchema, project.ProjectId, historicalFence)).Contexts);
        var contextBindingAfterFailure = await ReadSelectionContextBindingSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId, failure.ExecutionFence);
        Assert.Equal(contextBindingBeforeFailure, contextBindingAfterFailure);
        var grantsAfterFailure = await ReadGrantStateCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(grantsBeforeFailure.Total, grantsAfterFailure.Total);
        Assert.Equal(0, grantsAfterFailure.CurrentActive);
        Assert.Equal(
            grantsBeforeFailure.Superseded + grantsBeforeFailure.CurrentActive,
            grantsAfterFailure.Superseded);

        var recoveryRequest = new RecoverRunExecutionRequest(
            failure.ExecutionFence,
            failure.RunStateVersion,
            "integration-runtime-recovery",
            "operator_recovery",
            "recovery-approved");
        using var recoveryResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/recovery",
            runToken,
            recoveryRequest);
        await AssertStatusAsync(recoveryResponse, HttpStatusCode.OK);
        var recovered = await ReadJsonAsync<RunExecutionTransitionResult>(recoveryResponse);
        Assert.Equal("idle", recovered.State);
        Assert.True(recovered.ExecutionFence > failure.ExecutionFence);
        Assert.Equal("runtime_lost", recovered.PreviousCauseCode);
        Assert.Equal("sdk-turn-unknown", recovered.PreviousReference);
        using var recoveredStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(recoveredStatusResponse, HttpStatusCode.OK);
        var recoveredStatus = await ReadJsonAsync<OwnerRunStatus>(recoveredStatusResponse);
        Assert.Equal("idle", recoveredStatus.ExecutionState);
        Assert.Null(recoveredStatus.CauseCode);
        Assert.Null(recoveredStatus.Reference);
        using var recoveredRootStatusResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/status",
            runToken,
            [TenantId]);
        await AssertStatusAsync(recoveredRootStatusResponse, HttpStatusCode.OK);
        var recoveredRootStatus = await ReadJsonAsync<SessionStatusSnapshot>(recoveredRootStatusResponse);
        Assert.Equal(recovered.ExecutionFence, recoveredRootStatus.ExecutionFence);
        Assert.Equal(CoordinationActivityState.Idle, recoveredRootStatus.Activity);
        Assert.Equal("unavailable", recoveredRootStatus.RuntimeEffectsState);
        Assert.Equal("idle", recoveredRootStatus.RunExecution.State);
        using var recoveredDecisionResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions",
            runToken,
            [TenantId]);
        await AssertStatusAsync(recoveredDecisionResponse, HttpStatusCode.OK);
        var recoveredDecision = await ReadJsonAsync<CoordinatorDecisionStateView>(recoveredDecisionResponse);
        Assert.Equal(recovered.ExecutionFence, recoveredDecision.ExecutionFence);
        Assert.Equal(pendingGateStateVersion + 2, recoveredDecision.StateVersion);
        Assert.Null(recoveredDecision.PendingGate);
        Assert.True(recoveredDecision.CanDispatch);
        Assert.True(recoveredDecision.OutcomeConfirmed);
        Assert.True(recoveredDecision.WorkflowConfirmed);
        var recoveredDecisionRow = await ReadLatestDecisionSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(recovered.ExecutionFence, recoveredDecisionRow.ExecutionFence);
        Assert.Equal(recoveredDecision.StateVersion, recoveredDecisionRow.StateVersion);
        Assert.Equal(recovered.ExecutionFence, recoveredDecisionRow.EnvelopeFence);
        Assert.True(recoveredDecisionRow.PendingGateMissing);
        Assert.True(recoveredDecisionRow.ConfirmedWorkPlanPresent);
        Assert.True(recoveredDecisionRow.OutcomeConfirmed);
        Assert.True(recoveredDecisionRow.WorkflowConfirmed);
        Assert.Equal("cancelled", await ReadCoordinatorGateStateAsync(
            _connectionString, ownerSchema, project.ProjectId, "failure-pending-gate-once"));
        var recoveredHistoryRows = await ReadOwnerFenceHistoryAsync(
            _connectionString, ownerSchema, project.ProjectId, historicalFence);
        Assert.Equal(historicalRows.Decisions, recoveredHistoryRows.Decisions);
        Assert.Equal(historicalRows.Contexts, recoveredHistoryRows.Contexts);
        var contextBindingAfterRecovery = await ReadSelectionContextBindingSnapshotAsync(
            _connectionString, ownerSchema, project.ProjectId, recovered.ExecutionFence);
        Assert.Equal(contextBindingBeforeFailure, contextBindingAfterRecovery);
        var grantsAfterRecovery = await ReadGrantStateCountsAsync(
            _connectionString, ownerSchema, project.ProjectId);
        Assert.Equal(grantsBeforeFailure.Total, grantsAfterRecovery.Total);
        Assert.Equal(0, grantsAfterRecovery.CurrentActive);
        Assert.Equal(
            grantsBeforeFailure.Superseded + grantsBeforeFailure.CurrentActive,
            grantsAfterRecovery.Superseded);

        await using (var outboxDatabase = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await outboxDatabase.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT event_type, payload ->> 'runtimeEffectsState',
                   payload ->> 'physicalEffectsReplayed'
            FROM "{ownerSchema}".outbox_events
            WHERE id IN (@failure, @recovery)
            ORDER BY event_type
            """, connection))
        {
            command.Parameters.AddWithValue("failure", failure.OperationId);
            command.Parameters.AddWithValue("recovery", recovered.OperationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("orchestrator.run.execution_indeterminate", reader.GetString(0));
            Assert.Equal("unavailable", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("orchestrator.run.execution_recovered", reader.GetString(0));
            Assert.Equal("unavailable", reader.GetString(1));
            Assert.Equal("false", reader.GetString(2));
        }

        await using (var restartedOwnerFactory = new OrchestratorIntegrationFactory(
                         _connectionString,
                         ownerSchema,
                         signingKey,
                         projects.CreateHandler,
                         () => eventsFactory.Server.CreateHandler(),
                         cacheObjectStore,
                         sandboxProvider))
        using (var restartedOrchestrator = restartedOwnerFactory.CreateClient(
                   new WebApplicationFactoryClientOptions
                   {
                       BaseAddress = new Uri("https://orchestrator.test")
                   }))
        {
            using var restartedStatusResponse = await SendAsync(
                restartedOrchestrator,
                HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/status",
                runToken,
                [TenantId]);
            await AssertStatusAsync(restartedStatusResponse, HttpStatusCode.OK);
            Assert.Equal(
                recovered.ExecutionFence,
                (await ReadJsonAsync<OwnerRunStatus>(restartedStatusResponse)).ExecutionFence);
            using var restartedDecisionResponse = await SendAsync(
                restartedOrchestrator,
                HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions",
                runToken,
                [TenantId]);
            await AssertStatusAsync(restartedDecisionResponse, HttpStatusCode.OK);
            var restartedDecision =
                await ReadJsonAsync<CoordinatorDecisionStateView>(restartedDecisionResponse);
            Assert.Equal(recovered.ExecutionFence, restartedDecision.ExecutionFence);
            Assert.Equal(recoveredDecision.StateVersion, restartedDecision.StateVersion);
        }
        forkRequest = forkRequest with { ExecutionFence = recovered.ExecutionFence };

        var revocationPause = forkAdmissionGate.PauseSecondOwnerAdmission();
        var revokedForkRequest = forkRequest with
        {
            IdempotencyKey = "fork-owner-revoked",
            TargetSessionId = "fork-owner-revoked-target"
        };
        var revokedForkTask = SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/fork",
            runToken,
            revokedForkRequest);
        try
        {
            await revocationPause.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await RevokeRoleAsync(
                projects.PrivilegedFixtureDataSource,
                runnerRole.AssignmentId,
                runnerRole.Revision);
        }
        finally
        {
            revocationPause.Release();
        }
        using var revokedFork = await revokedForkTask;
        await AssertStatusAsync(revokedFork, HttpStatusCode.Conflict);
        var revokedForkResult = await ReadJsonAsync<CoordinationSessionForkResult>(revokedFork);
        Assert.Equal(
            CoordinationForkRegistrationState.Unregistered,
            revokedForkResult.RegistrationState);
        Assert.Equal("session_fork_admission_denied", revokedForkResult.UnavailableCode);
        await AssertNoEventsForkEffectsAsync(revokedForkRequest.TargetSessionId);

        using var revokedBinding = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/child/owner-binding",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, revokedBinding.StatusCode);

        using var revokedReceiptRead = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{Guid.NewGuid():D}",
            runToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, revokedReceiptRead.StatusCode);
    }

    private static AgtPolicyProviderOptions ReceiptPolicyOptions() =>
        new("agt-policy-resource", 3, "agt-policy-receipt-v1", [ReceiptAllowPolicy]);

    private static async Task<(long Bindings, long Decisions, long Outbox, long Grants)> ReadOwnerEffectCountsAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{schema}".coordinator_run_selection_context_versions
                    WHERE project_id = @project AND run_id = @run),
                (SELECT count(*) FROM "{schema}".coordinator_decisions
                    WHERE project_id = @project AND run_id = @run),
                (SELECT count(*) FROM "{schema}".coordinator_decision_outbox
                    WHERE project_id = @project AND run_id = @run),
                (SELECT count(*) FROM "{schema}".executable_action_grants
                    WHERE project_id = @project AND run_id = @run)
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task<long> ReadWebhookDeliveryCountAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{schema}".source_control_webhook_deliveries
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The webhook delivery count was unavailable."));
    }

    private static async Task<(string[] Decisions, string[] Contexts)> ReadOwnerFenceHistoryAsync(
        string connectionString,
        string schema,
        string projectId,
        long throughFence)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        var decisions = new List<string>();
        await using (var command = new NpgsqlCommand($"""
            SELECT state_version, request_id, decision::text
            FROM "{schema}".coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = 'root'
              AND execution_fence <= @fence
            ORDER BY state_version, request_id
            """, connection))
        {
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("fence", throughFence);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                decisions.Add($"{reader.GetInt64(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
        }

        var contexts = new List<string>();
        await using (var command = new NpgsqlCommand($"""
            SELECT row_to_json(context_version)::text
            FROM "{schema}".coordinator_run_selection_context_versions AS context_version
            WHERE project_id = @project AND run_id = @run AND execution_fence <= @fence
            ORDER BY execution_fence
            """, connection))
        {
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("run", RunId);
            command.Parameters.AddWithValue("fence", throughFence);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                contexts.Add(reader.GetString(0));
        }

        return (decisions.ToArray(), contexts.ToArray());
    }

    private static async Task<(long Total, long CurrentActive, long Superseded)> ReadGrantStateCountsAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*),
                count(*) FILTER (WHERE is_current AND grant_state = 'active'),
                count(*) FILTER (WHERE NOT is_current AND grant_state = 'superseded')
            FROM "{schema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<(
        long ExecutionFence,
        long StateVersion,
        long EnvelopeFence,
        bool PendingGateMissing,
        bool ConfirmedWorkPlanPresent,
        bool OutcomeConfirmed,
        bool WorkflowConfirmed)> ReadLatestDecisionSnapshotAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT execution_fence, state_version,
                   (decision -> 'envelope' ->> 'fence')::bigint,
                   decision -> 'envelope' ->> 'pendingGate' IS NULL,
                   decision -> 'envelope' -> 'confirmedWorkPlan' IS NOT NULL,
                   (decision -> 'envelope' ->> 'outcomeConfirmed')::boolean,
                   (decision -> 'envelope' ->> 'workflowConfirmed')::boolean
            FROM "{schema}".coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = 'root'
            ORDER BY state_version DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6));
    }

    private static async Task<string?> ReadSelectionContextBindingSnapshotAsync(
        string connectionString,
        string schema,
        string projectId,
        long executionFence)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (to_jsonb(context_version) - 'execution_fence' - 'created_at')::text
            FROM "{schema}".coordinator_run_selection_context_versions AS context_version
            WHERE project_id = @project AND run_id = @run AND execution_fence = @fence
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("fence", executionFence);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string> ReadAcceptedSelectionSnapshotAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT accepted_selection::text
            FROM "{schema}".accepted_runs WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        return (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected the accepted run selection snapshot.");
    }

    private static async Task<string?> ReadSelectionContextRowAsync(
        string connectionString,
        string schema,
        string projectId,
        long executionFence)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT row_to_json(context_version)::text
            FROM "{schema}".coordinator_run_selection_context_versions AS context_version
            WHERE project_id = @project AND run_id = @run AND execution_fence = @fence
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("fence", executionFence);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task WaitForBlockedSqlAsync(
        NpgsqlConnection observer,
        int blockerProcessId,
        string queryPattern)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var wait = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE @locker = ANY(pg_blocking_pids(pid))
                      AND query ILIKE '%' || @pattern || '%')
                """, observer);
            wait.Parameters.AddWithValue("locker", blockerProcessId);
            wait.Parameters.AddWithValue("pattern", queryPattern);
            if ((bool)(await wait.ExecuteScalarAsync(timeout.Token) ?? false))
                return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<(long Children, long SpawnOutboxEvents)> ReadChildRegistrationEffectsAsync(
        string connectionString,
        string schema,
        string projectId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{schema}".coordination_sessions
                    WHERE project_id = @project AND run_id = @run AND parent_session_id IS NOT NULL),
                (SELECT count(*) FROM "{schema}".outbox_events
                    WHERE stream_id = @stream AND event_type = 'orchestrator.session.spawn_requested')
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("stream", $"coordination/{projectId}/{RunId}/root");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<(
        string? WorkPlanItemId,
        long ExecutionFence,
        long LogicalTurnOrdinal,
        long StateVersion)> ReadRuntimeOwnerRowAsync(
        string connectionString,
        string schema,
        string projectId,
        string sessionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT work_plan_item_id, execution_fence, logical_turn_ordinal, state_version
            FROM "{schema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static async Task AssertAcceptedSandboxBindingAsync(
        string connectionString,
        string schema,
        string projectId,
        string resourceId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var query = new NpgsqlCommand($"""
            SELECT provider_id, resource_id, resource_generation, negotiated_capabilities::text,
                   execution_fence, project_configuration_revision
            FROM "{schema}".coordinator_run_selection_context_versions
            WHERE project_id = @project AND run_id = @run
            """, connection))
        {
            query.Parameters.AddWithValue("project", projectId);
            query.Parameters.AddWithValue("run", RunId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("sandbox-platform", reader.GetString(0));
            Assert.Equal(resourceId, reader.GetString(1));
            Assert.Equal(7, reader.GetInt64(2));
            var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(3));
            Assert.NotNull(capabilities);
            Assert.Contains("container.create", capabilities);
            Assert.True(reader.GetInt64(4) > 0);
            Assert.True(reader.GetInt64(5) > 0);
            Assert.False(await reader.ReadAsync());
        }

        await using var mutation = new NpgsqlCommand($"""
            UPDATE "{schema}".coordinator_run_selection_context_versions
            SET resource_generation = resource_generation + 1
            WHERE project_id = @project AND run_id = @run
            """, connection);
        mutation.Parameters.AddWithValue("project", projectId);
        mutation.Parameters.AddWithValue("run", RunId);
        await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
    }

    private static async Task<PinnedProviderBinding> ResolveReceiptPolicyBindingAsync(
        AgtPolicyProvider provider,
        AgtPolicyProviderOptions options,
        string runId)
    {
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [provider.CreateRegistration(options)],
            [new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)],
            []).Value);
        var resolved = await provider.ResolveNegotiateAndPinAsync(
            new ProviderResolver(catalog), options, runId);
        return Assert.IsType<PinnedProviderBinding>(resolved.Value);
    }

    private static ProviderCatalog CreateSandboxProviderCatalog()
    {
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                "sandbox-platform",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true,
            "options-v1",
            1);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, "sandbox-platform")],
            []).Value);
    }

    private static ProviderCatalog CreateSourceControlProviderCatalog()
    {
        var policyOptions = SourceControlMergePolicyOptions();
        var policyProvider = new AgtPolicyProvider();
        var sandboxRegistration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Sandbox,
                "sandbox-platform",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true,
            "options-v1",
            1);
        var sourceControlRegistration = new ProviderRegistration(
            GitHubSourceControlAdapter.CreateDescriptor(),
            true,
            "options-v1",
            GitHubSourceControlAdapter.CurrentOptionsSchemaVersion);
        var policyRegistration = policyProvider.CreateRegistration(policyOptions);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [sandboxRegistration, sourceControlRegistration, policyRegistration],
            [
                new ProviderSelection(ProviderSeam.Sandbox, "sandbox-platform"),
                new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub),
                new ProviderSelection(ProviderSeam.Policy, AgtPolicyProvider.ProviderId)
            ],
            []).Value);
    }

    private static AgtPolicyProviderOptions SourceControlMergePolicyOptions() =>
        new(
            "source-control-policy-resource",
            1,
            "source-control-policy-v1",
            [SourceControlMergeAllowPolicy]);

    private async Task RestartBrokerForSourceControlAsync(RecordingSecretRedemption backend)
    {
        await _brokerFactory.DisposeAsync();
        _brokerFactory = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                settings["IdentityBroker__SecretRedemption__Audience"] =
                    "https://broker-redemption.test";
                settings["IdentityBroker__Clients__0__Resources__1"] =
                    "https://broker-redemption.test";
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
            },
            configureServices: services =>
            {
                services.RemoveAll<ISecretRedemption>();
                services.AddSingleton<ISecretRedemption>(backend);
            });
    }

    private async Task CreateSourceControlSecretGrantAsync(
        string subject,
        string projectId,
        string runId,
        SecretRef secret,
        string purpose)
    {
        using var scope = _brokerFactory.Services.CreateScope();
        var authority = scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>();
        var grant = new SecretRedemptionGrant(
            $"source-control:{purpose}:{runId}:{secret.Id}",
            subject,
            projectId,
            runId,
            purpose,
            secret,
            GrantState.Active,
            DateTimeOffset.UtcNow.AddMinutes(30),
            revision: "source-control-v1");
        await authority.ReplaceAsync(grant, 0, $"source-control-grant-{Guid.NewGuid():N}");
    }

    private static async Task<string> CreateGitRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        await RunFixtureGitAsync(path, ["init", "--initial-branch=main"]);
        await RunFixtureGitAsync(path, ["config", "--local", "user.name", "SourceControl Integration"]);
        await RunFixtureGitAsync(
            path, ["config", "--local", "user.email", "source-control-integration@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "initial repository content\n");
        await RunFixtureGitAsync(path, ["add", "--", "README.md"]);
        await RunFixtureGitAsync(path, ["commit", "-m", "initial"]);
        var head = (await RunFixtureGitAsync(path, ["rev-parse", "--verify", "HEAD^{commit}"])).Trim();
        Assert.Equal(40, head.Length);
        return head;
    }

    private static async Task<string> RunFixtureGitAsync(string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            startInfo.Environment.Remove(key);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fixture process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Git fixture command '{arguments[0]}' failed with exit code {process.ExitCode}: {error}");
        return output;
    }

    private static async Task<bool> WaitForSourceControlAdvisoryLockWaitAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event = 'advisory'
                  AND query LIKE '%pg_advisory_lock%'
                  AND query LIKE '%agentweaver.source-control.merge%')
            """, connection);
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if ((bool)(await command.ExecuteScalarAsync())!)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        return false;
    }

    private static async Task<string> ReadGateStateAsync(
        string connectionString,
        string schema,
        string requestId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT gate_state FROM \"{schema}\".coordination_requests WHERE request_id = @request",
            connection);
        command.Parameters.AddWithValue("request", requestId);
        return (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected the addressed request gate.");
    }

    private static async Task<string> ReadCoordinatorGateStateAsync(
        string connectionString,
        string schema,
        string projectId,
        string requestId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT gate_state FROM "{schema}".coordinator_gates
            WHERE project_id = @project AND run_id = @run AND request_id = @request
            """, connection);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("request", requestId);
        return (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected the coordinator gate to be persisted.");
    }

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        T body)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: CoordinationJsonOptions)
        };
        AddBearerAndTenant(request, token, TenantId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendSignedWebhookRelayAsync(
        HttpClient client,
        string path,
        string token,
        string tenantId,
        string deliveryId,
        string eventName,
        byte[] rawBody,
        string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(rawBody)
        };
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-GitHub-Event", eventName);
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), rawBody)).ToLowerInvariant();
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + signature);
        AddBearerAndTenant(request, token, tenantId);
        return await client.SendAsync(request);
    }

    private string ReissueTokenWithAudiences(string sourceToken, params string[] audiences)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var original = new JwtSecurityTokenHandler().ReadJwtToken(sourceToken);
        var claims = original.Claims.Where(claim => claim.Type is not
            ("iss" or "aud" or "exp" or "nbf" or "iat" or "jti"));
        var payload = new JwtPayload(
            original.Issuer,
            null,
            claims,
            original.ValidFrom,
            original.ValidTo);
        payload[JwtRegisteredClaimNames.Aud] = audiences;
        var token = new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(
                new X509SecurityKey(certificate), SecurityAlgorithms.RsaSha256)),
            payload);
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task AssertStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(CoordinationJsonOptions)
        ?? throw new InvalidOperationException($"Expected a {typeof(T).Name} response.");

    private static string TestTokenFile => Path.Combine(Path.GetTempPath(), "agentweaver-test-token.jwt");

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public TemporaryEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            _original = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (key, value) in values)
                Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class CapturingHandler(
        HttpMessageHandler innerHandler,
        Action<HttpStatusCode, bool, string> capture) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/internal/addressed-messages/admit", StringComparison.Ordinal) == true)
            {
                var originalContent = response.Content;
                var bytes = await originalContent.ReadAsByteArrayAsync(cancellationToken);
                var replacementContent = new ByteArrayContent(bytes);
                foreach (var header in originalContent.Headers)
                    replacementContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replacementContent;
                originalContent.Dispose();
                capture(
                    response.StatusCode,
                    response.Headers.CacheControl?.NoStore == true,
                    Encoding.UTF8.GetString(bytes));
            }

            return response;
        }
    }

    private sealed class DenyingSessionsJournal : ISessionsJournal
    {
        public int AppendCalls { get; private set; }

        public Task<SessionRecord> CreateSessionAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string sessionId,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionProviderBinding> GetProviderBindingAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionProviderBinding> GetRunProviderBindingAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string projectId,
            string runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionAppendResult> AppendAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string sessionId,
            AppendSessionEvent input,
            CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            return Task.FromException<SessionAppendResult>(
                new SessionAccessDeniedException("PolicyEvaluation append remains fail-closed."));
        }

        public Task<SessionForkResult> ForkFromExplicitEventAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            string sourceSessionId,
            SessionForkRequest request,
            Func<CancellationToken, Task> validateAdmission,
            CancellationToken cancellationToken = default) =>
            throw new SessionForkUnsupportedException("The denying journal does not support session forks.");

        public Task<SessionEventPage> ReplayAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            SessionEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionEventPage> ReplayRunAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            SessionRunEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            SessionSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
            System.Security.Claims.ClaimsPrincipal principal,
            SessionRunSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RequestCountingHandler(
        HttpMessageHandler innerHandler,
        Action onRequest,
        Func<HttpRequestMessage, CancellationToken, Task>? beforeRequest = null,
        Action? onResponse = null) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onRequest();
            if (beforeRequest is not null)
                await beforeRequest(request, cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            onResponse?.Invoke();
            return response;
        }
    }

    private sealed class SourceControlTemporaryDirectory : IDisposable
    {
        public SourceControlTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentweaver-source-control-owner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(entry, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class LocalGitRepositoryRemote(Uri origin) : IGitRepositoryRemote
    {
        public Uri GetCloneUri(SourceControlRepositoryIdentity repository)
        {
            Assert.Equal("octo/agentweaver", repository.FullName);
            return origin;
        }
    }

    private sealed class ControlledRequestBarrier
    {
        private readonly object _sync = new();
        private Func<HttpRequestMessage, CancellationToken, Task<bool>>? _matcher;
        private TaskCompletionSource _paused = NewSignal();
        private TaskCompletionSource _release = NewSignal();

        public void Arm(Func<HttpRequestMessage, CancellationToken, Task<bool>> matcher)
        {
            ArgumentNullException.ThrowIfNull(matcher);
            lock (_sync)
            {
                if (_matcher is not null)
                    throw new InvalidOperationException("A controlled request barrier is already armed.");
                _paused = NewSignal();
                _release = NewSignal();
                _matcher = matcher;
            }
        }

        public Task WaitUntilPausedAsync()
        {
            lock (_sync)
                return _paused.Task;
        }

        public void Release()
        {
            TaskCompletionSource release;
            lock (_sync)
                release = _release;
            release.TrySetResult();
        }

        public async Task PauseIfMatchedAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Func<HttpRequestMessage, CancellationToken, Task<bool>>? matcher;
            lock (_sync)
                matcher = _matcher;
            if (matcher is null || !await matcher(request, cancellationToken).ConfigureAwait(false))
                return;

            TaskCompletionSource paused;
            TaskCompletionSource release;
            lock (_sync)
            {
                if (!ReferenceEquals(_matcher, matcher))
                    return;
                _matcher = null;
                paused = _paused;
                release = _release;
            }

            paused.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InMemoryObjectStore : IObjectStore
    {
        private readonly Dictionary<ObjectKey, byte[]> _objects = [];

        public async Task WriteAsync(
            ObjectKey key,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!_objects.TryAdd(key, buffer.ToArray()))
                throw new IOException("Object keys are create-only.");
        }

        public Task<ObjectRead?> ReadAsync(
            ObjectKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_objects.TryGetValue(key, out var bytes))
                return Task.FromResult<ObjectRead?>(null);
            var stream = new MemoryStream(bytes, writable: false);
            return Task.FromResult<ObjectRead?>(new ObjectRead(stream, bytes.Length, stream.Dispose));
        }

        public Task<bool> DeleteAsync(
            ObjectKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_objects.Remove(key));
        }
    }

    private static void AddJwtBearer(IServiceCollection services, SecurityKey key)
    {
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
        });
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudience = "https://api.test",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                };
            });
    }

    private sealed class ForkAdmissionGate
    {
        private readonly object _sync = new();
        private int _admissionsToSkip;
        private ForkAdmissionPause? _armedPause;

        public ForkAdmissionPause PauseSecondOwnerAdmission()
        {
            lock (_sync)
            {
                if (_armedPause is not null)
                    throw new InvalidOperationException("A fork admission pause is already armed.");
                _admissionsToSkip = 1;
                return _armedPause = new ForkAdmissionPause();
            }
        }

        public async Task PauseIfArmedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri ||
                !uri.AbsolutePath.EndsWith("/fork-admission", StringComparison.Ordinal))
                return;

            ForkAdmissionPause? pause;
            lock (_sync)
            {
                if (_armedPause is null)
                    return;
                if (_admissionsToSkip > 0)
                {
                    _admissionsToSkip--;
                    return;
                }
                pause = _armedPause;
                _armedPause = null;
            }

            pause.MarkPaused();
            await pause.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OwnerForkRaceGate
    {
        private readonly TaskCompletionSource _firstResponsePaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRequestPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _forkRequests;

        public Task WaitUntilFirstResponsePausedAsync() => _firstResponsePaused.Task;
        public Task WaitUntilSecondRequestPausedAsync() => _secondRequestPaused.Task;
        public void ReleaseFirstResponse() => _releaseFirstResponse.TrySetResult();
        public void ReleaseSecondRequest() => _releaseSecondRequest.TrySetResult();

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            Func<Task<HttpResponseMessage>> send,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri ||
                !uri.AbsolutePath.EndsWith("/fork", StringComparison.Ordinal))
                return await send().ConfigureAwait(false);

            switch (Interlocked.Increment(ref _forkRequests))
            {
                case 1:
                {
                    var response = await send().ConfigureAwait(false);
                    _firstResponsePaused.TrySetResult();
                    await _releaseFirstResponse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return response;
                }
                case 2:
                    _secondRequestPaused.TrySetResult();
                    await _releaseSecondRequest.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }

            return await send().ConfigureAwait(false);
        }
    }

    private sealed class OwnerForkRaceHandler(
        HttpMessageHandler innerHandler,
        OwnerForkRaceGate gate) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            gate.SendAsync(
                request,
                () => base.SendAsync(request, cancellationToken),
                cancellationToken);
    }

    private sealed class RuntimeOwnerContextSelectionPause
    {
        private readonly TaskCompletionSource _paused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _selectionReads;

        public Task WaitUntilPausedAsync() => _paused.Task;
        public void Release() => _release.TrySetResult();

        public async Task PauseSecondSelectionReadAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri ||
                !uri.AbsolutePath.EndsWith("/selection", StringComparison.Ordinal) ||
                Interlocked.Increment(ref _selectionReads) != 2)
                return;

            _paused.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RuntimeOwnerContextSelectionBarrierHandler(
        HttpMessageHandler innerHandler,
        RuntimeOwnerContextSelectionPause pause) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await pause.PauseSecondSelectionReadAsync(request, cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ForkAdmissionPause
    {
        private readonly TaskCompletionSource _paused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilPausedAsync() => _paused.Task;
        public void Release() => _release.TrySetResult();
        public void MarkPaused() => _paused.TrySetResult();

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);
    }

    private sealed class ForkAdmissionBarrierHandler(
        HttpMessageHandler innerHandler,
        ForkAdmissionGate gate) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await gate.PauseIfArmedAsync(request, cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OrchestratorIntegrationFactory(
        string connectionString,
        string schema,
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandler,
        Func<HttpMessageHandler> eventsHandler,
        IObjectStore? objectStore = null,
        ICoordinatorSandboxResourceProvider? sandboxProvider = null,
        Func<HttpMessageHandler>? brokerHandler = null,
        Func<HttpMessageHandler>? sourceControlHandler = null,
        ProviderCatalog? providerCatalog = null,
        AgtPolicyProviderOptions? policyOptions = null,
        GitWorkspaceManager? gitWorkspaceManager = null)
        : WebApplicationFactory<OrchestratorHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Orchestrator"] = connectionString,
                    ["Orchestrator:Database:WorkloadIdentity:TenantId"] = Guid.NewGuid().ToString(),
                    ["Orchestrator:Database:WorkloadIdentity:ClientId"] = Guid.NewGuid().ToString(),
                    ["Orchestrator:Database:WorkloadIdentity:TokenFilePath"] = TestTokenFile,
                    ["Orchestrator:Schema"] = schema,
                    ["Identity:Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
                    ["Identity:Audience"] = "https://api.test",
                    ["ProjectsConfig:AuthorizationContext:OwnerBaseAddress"] = "https://projects.test/",
                    ["ProjectsConfig:AuthorizationContext:Audience"] = "https://api.test",
                    ["EventsAndSessions:Authorization:OwnerBaseAddress"] = "https://events.test/",
                    ["EventsAndSessions:Authorization:Audience"] = "https://api.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
                services.RemoveAll<ProviderCatalog>();
                services.AddSingleton(providerCatalog ?? CreateSandboxProviderCatalog());
                AddJwtBearer(services, signingKey);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.ProjectsRunSelectionClient>()
                    .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.EventsAddressedMessageClient>()
                    .ConfigurePrimaryHttpMessageHandler(eventsHandler);
                if (brokerHandler is not null)
                    services.AddHttpClient<
                            OrchestratorHost::Agentweaver.Orchestrator.SourceControlSecretRedemptionClient>()
                        .ConfigurePrimaryHttpMessageHandler(brokerHandler);
                if (sourceControlHandler is not null)
                    services.AddHttpClient<GitHubSourceControlAdapter>()
                        .ConfigurePrimaryHttpMessageHandler(sourceControlHandler);
                if (policyOptions is not null)
                    services.AddSingleton(policyOptions);
                if (gitWorkspaceManager is not null)
                    services.AddSingleton(gitWorkspaceManager);
                if (objectStore is not null)
                    services.AddSingleton<IObjectStore>(objectStore);
                if (sandboxProvider is not null)
                    services.AddSingleton<ICoordinatorSandboxResourceProvider>(sandboxProvider);
            });
        }
    }

    private sealed class ControlledGitHubApi(string credential)
    {
        private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly List<string> _requests = [];
        private int _mergeRequestCount;
        private string? _lastMergeRequestBody;

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToArray();
            }
        }

        public HttpMessageHandler CreateHandler() => new Handler(this);

        public int MergeRequestCount => Volatile.Read(ref _mergeRequestCount);
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponseAsync { get; set; }

        public string? LastMergeRequestBody
        {
            get
            {
                lock (_requests)
                    return _lastMergeRequestBody;
            }
        }

        private async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("GitHub request URI was missing.");
            var requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_requests)
            {
                _requests.Add(request.Method.Method + " " + uri.PathAndQuery);
                if (request.Method == HttpMethod.Put &&
                    uri.AbsolutePath == "/repos/octo/agentweaver/pulls/17/merge")
                {
                    _lastMergeRequestBody = requestBody;
                    Interlocked.Increment(ref _mergeRequestCount);
                }
            }

            if (request.Headers.Authorization?.Scheme != "Bearer" ||
                request.Headers.Authorization.Parameter != credential)
                return Json(HttpStatusCode.Unauthorized, "{}");

            if (BeforeResponseAsync is { } beforeResponse)
                await beforeResponse(request, cancellationToken).ConfigureAwait(false);

            if (request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/repos/octo/agentweaver")
                return Json(HttpStatusCode.OK,
                    "{\"id\":12345,\"full_name\":\"octo/agentweaver\",\"default_branch\":\"main\"," +
                    "\"private\":false,\"created_at\":\"2020-01-01T00:00:00Z\"," +
                    "\"permissions\":{\"pull\":true,\"push\":true}}");

            if (request.Method == HttpMethod.Post &&
                uri.AbsolutePath == "/repos/octo/agentweaver/issues")
                return Json(HttpStatusCode.Created,
                    "{\"number\":41,\"title\":\"Integration issue\"," +
                    "\"html_url\":\"https://github.com/octo/agentweaver/issues/41\",\"state\":\"open\"}");

            if (request.Method == HttpMethod.Post &&
                uri.AbsolutePath == "/repos/octo/agentweaver/pulls")
                return Json(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation failed\"}");

            if (request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/repos/octo/agentweaver/pulls" &&
                uri.Query.Contains("state=open", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "[" + PullRequestJson() + "]");

            if (request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/repos/octo/agentweaver/pulls/17")
                return Json(HttpStatusCode.OK, PullRequestJson());

            if (request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/repos/octo/agentweaver/pulls/17/reviews")
                return Json(HttpStatusCode.OK,
                    "[{\"id\":11,\"user\":{\"login\":\"reviewer\"},\"state\":\"APPROVED\"," +
                    "\"submitted_at\":\"2026-10-07T12:00:00Z\"}]");

            if (request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/repos/octo/agentweaver/rules/branches/main")
                return Json(HttpStatusCode.OK, "[]");

            if (request.Method == HttpMethod.Put &&
                uri.AbsolutePath == "/repos/octo/agentweaver/pulls/17/merge")
                return Json(HttpStatusCode.OK,
                    "{\"merged\":true,\"sha\":\"cccccccccccccccccccccccccccccccccccccccc\"}");

            return Json(HttpStatusCode.NotFound, "{\"message\":\"Unexpected controlled GitHub request\"}");
        }

        private static string PullRequestJson() =>
            "{\"number\":17,\"html_url\":\"https://github.com/octo/agentweaver/pull/17\"," +
            "\"state\":\"open\",\"merged\":false," +
            "\"head\":{\"ref\":\"feature/source-control\",\"sha\":\"" + HeadSha +
            "\",\"repo\":{\"full_name\":\"octo/agentweaver\"}}," +
            "\"base\":{\"ref\":\"main\",\"sha\":\"" + BaseSha +
            "\",\"repo\":{\"full_name\":\"octo/agentweaver\"}}}";

        private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
            new(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };

        private sealed class Handler(ControlledGitHubApi api) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return api.RespondAsync(request, cancellationToken);
            }
        }
    }

    private sealed class ControlledSandboxResourceProvider : ICoordinatorSandboxResourceProvider
    {
        private int _negotiationCount;

        public string ProviderId => "sandbox-platform";
        public string ResourceId => "controlled-sandbox-resource";
        public int NegotiationCount => Volatile.Read(ref _negotiationCount);
        public ImmutableArray<string> IsolationChoices { get; } = ["isolated-worktree"];
        public Func<CancellationToken, Task>? AfterNegotiationAsync { get; set; }

        public async Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
            ProviderCandidate candidate,
            OrchestratorHost::Agentweaver.Orchestrator.EffectiveRunSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Seam != ProviderSeam.Sandbox || candidate.ProviderId != ProviderId ||
                selection.RunId != RunId)
                return ProviderResult<ResourceNegotiation>.Failure(
                    ProviderErrorCode.InvalidNegotiation,
                    "The controlled Sandbox provider received an unexpected candidate.");

            Interlocked.Increment(ref _negotiationCount);
            if (AfterNegotiationAsync is { } afterNegotiation)
                await afterNegotiation(cancellationToken).ConfigureAwait(false);
            return ProviderResult<ResourceNegotiation>.Success(
                new ResourceNegotiation(
                    new ProviderResourceRef(ProviderSeam.Sandbox, ProviderId, ResourceId, 7),
                    candidate.RequiredCapabilities));
        }
    }

    private sealed class EventsIntegrationFactory(
        string connectionString,
        string schema,
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandler,
        Func<HttpMessageHandler> orchestratorHandler)
        : WebApplicationFactory<EventsHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
                ?? throw new InvalidOperationException("The integration database name is missing.");
            builder.UseSetting("EventsAndSessions:Knowledge:BaseAddress", "https://knowledge.test/");
            builder.UseSetting("ProjectsConfig:BaseAddress", "https://projects.test/");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:EventsAndSessions"] = connectionString,
                    ["EventsAndSessions:Database:WorkloadIdentity:TenantId"] = Guid.NewGuid().ToString(),
                    ["EventsAndSessions:Database:WorkloadIdentity:ClientId"] = Guid.NewGuid().ToString(),
                    ["EventsAndSessions:Database:WorkloadIdentity:TokenFilePath"] = TestTokenFile,
                    ["EventsAndSessions:Provider:ResourceId"] = "postgres-test",
                    ["EventsAndSessions:Provider:DatabaseName"] = databaseName,
                    ["EventsAndSessions:Provider:ResourceGeneration"] = "1",
                    ["EventsAndSessions:Provider:Schema"] = schema,
                    ["EventsAndSessions:Provider:OptionsRevision"] = "integration-v1",
                    ["EventsAndSessions:Messaging:OptionsRevision"] = "integration-messaging-v1",
                    ["EventsAndSessions:Knowledge:BaseAddress"] = "https://knowledge.test/",
                    ["ProjectsConfig:BaseAddress"] = "https://projects.test/",
                    ["Identity:Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
                    ["Identity:Audience"] = "https://api.test",
                    ["ProjectsConfig:AuthorizationContext:OwnerBaseAddress"] = "https://projects.test/",
                    ["ProjectsConfig:AuthorizationContext:Audience"] = "https://api.test",
                    ["EventsAndSessions:OrchestratorOwner:OwnerBaseAddress"] = "https://orchestrator.test/",
                    ["EventsAndSessions:OrchestratorOwner:Audience"] = "https://api.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
                AddJwtBearer(services, signingKey);
                services.AddHttpClient<
                        EventsHost::Agentweaver.EventsAndSessions.IProjectsAuthorizationContextClient,
                        EventsHost::Agentweaver.EventsAndSessions.ProjectsAuthorizationContextClient>()
                    .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                services.AddHttpClient<
                        EventsHost::Agentweaver.EventsAndSessions.ICoordinationOwnerClient,
                        EventsHost::Agentweaver.EventsAndSessions.CoordinationOwnerClient>()
                    .ConfigurePrimaryHttpMessageHandler(orchestratorHandler);
            });
        }
    }

}
