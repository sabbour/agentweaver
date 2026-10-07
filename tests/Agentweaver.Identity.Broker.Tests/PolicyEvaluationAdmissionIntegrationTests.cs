extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using EventsHost::Agentweaver.EventsAndSessions;
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
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private const string AdmissionPolicyActionId = "propose_outcome_spec";
    private const string AdmissionAllowPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: policy-admission
        scope: global
        default_action: deny
        rules:
          - name: allow-current-root-action
            condition: "action_id == 'propose_outcome_spec'"
            action: allow
            priority: 100
        """;
    private const string AdmissionDenyPolicy = """
        apiVersion: governance.toolkit/v1
        version: "1.0"
        name: policy-admission-deny
        scope: global
        default_action: deny
        rules:
          - name: allow-unrelated-action
            condition: "action_id == 'unrelated_action'"
            action: allow
            priority: 100
        """;

    [Theory]
    [InlineData("superseded")]
    [InlineData("expired")]
    public async Task PolicyReceiptAdmissionRejectsInactiveAllowAfterHistoricalReceiptFetch(string change)
    {
        var barrier = new PolicyOwnerAdmissionBarrier();
        await using var harness = await CreatePolicyReceiptAdmissionHarnessAsync(barrier);
        var receipt = await CreateUnappendedAllowReceiptAsync(harness);
        var before = await ReadEventsJournalSnapshotAsync(harness);
        var receiptPath =
            $"/api/projects/{harness.Project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{receipt.ReceiptId:D}";
        barrier.ArmAfterResponse(receiptPath, matchingRequest: 1);

        var append = SendJsonAsync(
            harness.Events,
            HttpMethod.Post,
            $"/internal/sessions/{harness.Root.RootSessionId}/policy-evaluations",
            harness.RunToken,
            new PolicyEvaluationReceiptReferenceRequest(receipt.ReceiptId));
        await barrier.WaitUntilBlockedAsync();
        await ChangeGrantAsync(harness, receipt.Grant, change);
        barrier.Release();

        using var response = await append.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await ReadEventsJournalSnapshotAsync(harness));
    }

    [Fact]
    public async Task PolicyReceiptAdmissionRechecksWriteRoleInsideJournalTransaction()
    {
        var barrier = new PolicyOwnerAdmissionBarrier();
        await using var harness = await CreatePolicyReceiptAdmissionHarnessAsync(barrier);
        var receipt = await CreateUnappendedAllowReceiptAsync(harness);
        var before = await ReadEventsJournalSnapshotAsync(harness);
        var admissionPath =
            $"/api/projects/{harness.Project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{receipt.ReceiptId:D}/admission";
        barrier.ArmBeforeRequest(admissionPath, matchingRequest: 2);

        var append = SendJsonAsync(
            harness.Events,
            HttpMethod.Post,
            $"/internal/sessions/{harness.Root.RootSessionId}/policy-evaluations",
            harness.RunToken,
            new PolicyEvaluationReceiptReferenceRequest(receipt.ReceiptId));
        await barrier.WaitUntilBlockedAsync();
        await RevokeRoleAsync(
            harness.Projects.PrivilegedFixtureDataSource,
            harness.RunnerRole.AssignmentId,
            harness.RunnerRole.Revision);
        barrier.Release();

        using var response = await append.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await ReadEventsJournalSnapshotAsync(harness));

        await using var database = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var grantQuery = new NpgsqlCommand($"""
            SELECT is_current, grant_state, expires_at > clock_timestamp()
            FROM "{harness.OwnerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                AND revision = @revision
            """, connection);
        grantQuery.Parameters.AddWithValue("project", harness.Project.ProjectId);
        grantQuery.Parameters.AddWithValue("run", RunId);
        grantQuery.Parameters.AddWithValue("grant", receipt.Grant.GrantId);
        grantQuery.Parameters.AddWithValue("revision", receipt.Grant.Revision);
        await using var grantReader = await grantQuery.ExecuteReaderAsync();
        Assert.True(await grantReader.ReadAsync());
        Assert.True(grantReader.GetBoolean(0));
        Assert.Equal("active", grantReader.GetString(1));
        Assert.True(grantReader.GetBoolean(2));
    }

    [Fact]
    public async Task PolicyReceiptAdmissionAppendsCurrentDenyAndRejectsRoleLossInsideTransaction()
    {
        var barrier = new PolicyOwnerAdmissionBarrier();
        await using var harness = await CreatePolicyReceiptAdmissionHarnessAsync(barrier);
        var receipt = await CreateUnappendedDenyReceiptAsync(harness);
        using var ownerResponse = await SendAsync(
            harness.Orchestrator,
            HttpMethod.Get,
            $"/api/projects/{harness.Project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{receipt.ReceiptId:D}",
            harness.RunToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        var ownerReceipt = await ReadJsonAsync<PolicyEvaluationReceiptView>(ownerResponse);
        Assert.Equal(PolicyEvaluationOutcome.Deny, ownerReceipt.Evidence.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.PlatformRuleDenied, ownerReceipt.Evidence.ReasonCode);

        var before = await ReadEventsJournalSnapshotAsync(harness);
        using var response = await SendJsonAsync(
            harness.Events,
            HttpMethod.Post,
            $"/internal/sessions/{harness.Root.RootSessionId}/policy-evaluations",
            harness.RunToken,
            new PolicyEvaluationReceiptReferenceRequest(receipt.ReceiptId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(true, response.Headers.CacheControl?.NoStore);
        var acknowledgment = await ReadJsonAsync<PolicyEvaluationAppendAcknowledgment>(response);
        Assert.Equal(receipt.ReceiptId, acknowledgment.ReceiptId);
        Assert.Equal(
            new SessionIdentity(harness.Project.ProjectId, RunId, harness.Root.RootSessionId),
            acknowledgment.Identity);
        Assert.Equal(before.Position + 1, acknowledgment.Position);
        Assert.False(acknowledgment.IsDuplicate);

        var after = await ReadEventsJournalSnapshotAsync(harness);
        Assert.Equal(before.Events + 1, after.Events);
        Assert.Equal(before.Position + 1, after.Position);
        Assert.Equal(before.Inbox + 1, after.Inbox);
        Assert.Equal(before.Outbox + 1, after.Outbox);
        Assert.Equal(before.OutboxSequence + 1, after.OutboxSequence);
        Assert.Equal(before.ObjectReferences, after.ObjectReferences);

        var revokedReceipt = await CreateUnappendedDenyReceiptAsync(harness);
        var beforeRevocation = await ReadEventsJournalSnapshotAsync(harness);
        var admissionPath =
            $"/api/projects/{harness.Project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{revokedReceipt.ReceiptId:D}/admission";
        barrier.ArmBeforeRequest(admissionPath, matchingRequest: 2);

        var append = SendJsonAsync(
            harness.Events,
            HttpMethod.Post,
            $"/internal/sessions/{harness.Root.RootSessionId}/policy-evaluations",
            harness.RunToken,
            new PolicyEvaluationReceiptReferenceRequest(revokedReceipt.ReceiptId));
        await barrier.WaitUntilBlockedAsync();
        await RevokeRoleAsync(
            harness.Projects.PrivilegedFixtureDataSource,
            harness.RunnerRole.AssignmentId,
            harness.RunnerRole.Revision);
        barrier.Release();

        using var revokedResponse = await append.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);
        Assert.Equal(beforeRevocation, await ReadEventsJournalSnapshotAsync(harness));
    }

    [Fact]
    public async Task PolicyReceiptWriterRejectsRoleLossDuringOwnerGrantLockWaitWithoutReceiptEventsOrEffect()
    {
        await using var harness = await CreatePolicyReceiptAdmissionHarnessAsync(
            new PolicyOwnerAdmissionBarrier());
        var grant = await ReadPolicyGrantAsync(harness);
        var ownerReceiptsBefore = await ReadPolicyOwnerReceiptCountAsync(harness);
        var eventsBefore = await ReadEventsJournalSnapshotAsync(harness);
        Assert.Equal(0, ownerReceiptsBefore);

        await using var ownerDataSource = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var ownerConnection = await ownerDataSource.OpenConnectionAsync();
        await using var ownerTransaction = await ownerConnection.BeginTransactionAsync();
        await using (var lockGrant = new NpgsqlCommand($"""
            SELECT grant_id
            FROM "{harness.OwnerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                AND revision = @revision
            FOR UPDATE
            """, ownerConnection, ownerTransaction))
        {
            lockGrant.Parameters.AddWithValue("project", harness.Project.ProjectId);
            lockGrant.Parameters.AddWithValue("run", RunId);
            lockGrant.Parameters.AddWithValue("grant", grant.GrantId);
            lockGrant.Parameters.AddWithValue("revision", grant.Revision);
            Assert.NotNull(await lockGrant.ExecuteScalarAsync());
        }

        var effectInvoked = false;
        var write = CreateUnappendedAllowReceiptAsync(harness, () => effectInvoked = true);
        await WaitForPolicyGrantLockWaitAsync(harness);
        await RevokeRoleAsync(
            harness.Projects.PrivilegedFixtureDataSource,
            harness.RunnerRole.AssignmentId,
            harness.RunnerRole.Revision);
        await ownerTransaction.CommitAsync();
        _ = await write.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ownerReceiptsBefore, await ReadPolicyOwnerReceiptCountAsync(harness));
        Assert.Equal(eventsBefore, await ReadEventsJournalSnapshotAsync(harness));
        Assert.False(effectInvoked);
    }

    [Theory]
    [InlineData("session-completed")]
    [InlineData("run-completed")]
    [InlineData("fence-advanced")]
    [InlineData("selection-superseded")]
    public async Task PolicyDenyAndErrorReceiptsRequireCurrentOwnerSessionRunAndSelection(string change)
    {
        await using var harness = await CreatePolicyReceiptAdmissionHarnessAsync(
            new PolicyOwnerAdmissionBarrier());
        var ownerReceiptsBefore = await ReadPolicyOwnerReceiptCountAsync(harness);
        var eventsBefore = await ReadEventsJournalSnapshotAsync(harness);

        var deny = await CreateUnappendedDenyReceiptAsync(harness);
        var error = await CreateUnappendedPolicyReceiptAsync(harness, string.Empty);
        Assert.Equal(ownerReceiptsBefore + 2, await ReadPolicyOwnerReceiptCountAsync(harness));

        foreach (var (receipt, expectedOutcome) in new[]
        {
            (deny, PolicyEvaluationOutcome.Deny),
            (error, PolicyEvaluationOutcome.Error)
        })
        {
            using var ownerResponse = await SendAsync(
                harness.Orchestrator,
                HttpMethod.Get,
                $"/api/projects/{harness.Project.ProjectId}/runs/{RunId}/coordination/policy-evaluations/{receipt.ReceiptId:D}",
                harness.RunToken,
                [TenantId]);
            Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
            var stored = await ReadJsonAsync<PolicyEvaluationReceiptView>(ownerResponse);
            Assert.Equal(expectedOutcome, stored.Evidence.Outcome);
        }

        await ChangeOwnerReceiptStateAsync(harness, change);
        using (var currentAuthority = await SendAsync(
                   harness.Projects.Client,
                   HttpMethod.Get,
                   "/api/authorization/context",
                   harness.RunToken,
                   [TenantId]))
            Assert.Equal(HttpStatusCode.OK, currentAuthority.StatusCode);

        var effectInvoked = false;
        _ = await CreateUnappendedPolicyReceiptAsync(
            harness, AdmissionDenyPolicy, () => effectInvoked = true);
        Assert.False(effectInvoked);
        Assert.Equal(ownerReceiptsBefore + 2, await ReadPolicyOwnerReceiptCountAsync(harness));

        foreach (var receipt in new[] { deny, error })
        {
            using var response = await SendJsonAsync(
                harness.Events,
                HttpMethod.Post,
                $"/internal/sessions/{harness.Root.RootSessionId}/policy-evaluations",
                harness.RunToken,
                new PolicyEvaluationReceiptReferenceRequest(receipt.ReceiptId));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(ownerReceiptsBefore + 2, await ReadPolicyOwnerReceiptCountAsync(harness));
        Assert.Equal(eventsBefore, await ReadEventsJournalSnapshotAsync(harness));
    }

    private async Task<PolicyReceiptAdmissionHarness> CreatePolicyReceiptAdmissionHarnessAsync(
        PolicyOwnerAdmissionBarrier barrier)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);

        var platformAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "policy-admission-admin", null, null, ["platform_admin"]);
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
            Content = JsonContent.Create(new CreateProjectRequest { Name = "Policy admission integration" })
        };
        AddBearerAndTenant(createProject, platformAdminToken, TenantId);
        using var createdProject = await projects.Client.SendAsync(createProject);
        Assert.Equal(HttpStatusCode.Created, createdProject.StatusCode);
        var project = await createdProject.Content.ReadFromJsonAsync<ProjectSummary>(AuthorizationJsonOptions);
        Assert.NotNull(project);

        var bootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "policy-admission-runner", null, null, ["orchestrator"]);
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
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Viewer);
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
            "policy-admission-runner",
            project.ProjectId,
            RunId,
            ["platform_admin"]);

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
                            "policy-agent", "Policy agent", "implementer", "Tests policy receipt admission.")
                    ],
                    Casting = [new ProjectAgentCast("policy-agent", "implementer", 0)]
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

        using var acceptSelection = new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection")
        {
            Content = JsonContent.Create(new AcceptRunSelectionRequest
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
                        }
                    ]
                }
            })
        };
        AddBearerAndTenant(acceptSelection, runToken, TenantId);
        using var acceptedSelection = await projects.Client.SendAsync(acceptSelection);
        Assert.Equal(HttpStatusCode.OK, acceptedSelection.StatusCode);

        var ownerSchema = "policy_admission_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "policy_events_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
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
            ["Identity__Audience"] = "https://api.test"
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsAndSessionsMigrator.MigrateAsync(migrations, eventsSchema);
        }

        EventsIntegrationFactory? eventsFactoryReference = null;
        var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString,
            ownerSchema,
            signingKey,
            projects.CreateHandler,
            () => eventsFactoryReference!.Server.CreateHandler());
        var eventsFactory = new EventsIntegrationFactory(
            _connectionString,
            eventsSchema,
            signingKey,
            projects.CreateHandler,
            () => new PolicyOwnerAdmissionBarrierHandler(
                orchestratorFactory.Server.CreateHandler(), barrier));
        eventsFactoryReference = eventsFactory;
        var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test")
        });
        var events = eventsFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://events.test")
        });

        var resourcesTransferred = false;
        try
        {
            using var rootResponse = await SendJsonAsync(
                orchestrator,
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/root",
                runToken,
                new AcceptRootRequest("policy-root"));
            if (rootResponse.StatusCode != HttpStatusCode.Created)
            {
                var body = await rootResponse.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Expected root registration to return 201, got {(int)rootResponse.StatusCode}: {body}");
            }
            var root = await ReadJsonAsync<AcceptedRoot>(rootResponse);

            var harness = new PolicyReceiptAdmissionHarness(
                certificate,
                projects,
                serviceEnvironment,
                orchestratorFactory,
                eventsFactory,
                orchestrator,
                events,
                _connectionString,
                ownerSchema,
                eventsSchema,
                project,
                runnerRole,
                runToken,
                root);
            resourcesTransferred = true;
            return harness;
        }
        finally
        {
            if (!resourcesTransferred)
            {
                events.Dispose();
                orchestrator.Dispose();
                await eventsFactory.DisposeAsync();
                await orchestratorFactory.DisposeAsync();
                serviceEnvironment.Dispose();
                await projects.DisposeAsync();
                certificate.Dispose();
            }
        }
    }

    private static Task<StagedPolicyReceipt> CreateUnappendedAllowReceiptAsync(
        PolicyReceiptAdmissionHarness harness,
        Action? onEffectInvoked = null) =>
        CreateUnappendedPolicyReceiptAsync(harness, AdmissionAllowPolicy, onEffectInvoked);

    private static Task<StagedPolicyReceipt> CreateUnappendedDenyReceiptAsync(
        PolicyReceiptAdmissionHarness harness) =>
        CreateUnappendedPolicyReceiptAsync(harness, AdmissionDenyPolicy);

    private static async Task<StagedPolicyReceipt> CreateUnappendedPolicyReceiptAsync(
        PolicyReceiptAdmissionHarness harness,
        string policy,
        Action? onEffectInvoked = null)
    {
        var grant = await ReadPolicyGrantAsync(harness);
        var receiptId = Guid.NewGuid();
        var provider = new AgtPolicyProvider();
        var options = new AgtPolicyProviderOptions(
            "agt-policy-admission-resource", 3, "agt-policy-admission-v1", [policy]);
        var bindingOptions = string.IsNullOrEmpty(policy)
            ? options with { PlatformPolicyDocuments = [AdmissionDenyPolicy] }
            : options;
        var binding = await ResolveReceiptPolicyBindingAsync(provider, bindingOptions, RunId);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new JwtSecurityTokenHandler().ReadJwtToken(harness.RunToken).Claims,
            "integration-jwt"));
        var store = harness.OrchestratorFactory.Services
            .GetRequiredService<ExecutableActionGrantOwnerStore>();
        var guard = new ExecutableActionGuard(
            provider,
            options,
            new DuplicatePolicyReceiptJournal(),
            store,
            sourceReceiptWriter: store,
            evaluationReceiptWriter: store);
        var accessor = harness.OrchestratorFactory.Services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        var authorityContext = new DefaultHttpContext { User = principal };
        authorityContext.Request.Headers.Authorization = $"Bearer {harness.RunToken}";
        authorityContext.Request.Headers["X-Agentweaver-Tenant"] = TenantId;
        accessor.HttpContext = authorityContext;
        ExecutableActionGuardResult<string> result;
        var effectInvoked = false;
        try
        {
            result = await guard.ExecuteAsync(
                new ExecutableActionInvocation(
                    principal,
                    harness.Root.RootSessionId,
                    grant.StepId,
                    grant.ActionId,
                    grant.Purpose,
                    new ExecutableActionGrantReference(grant.GrantId, grant.Revision),
                    harness.Root.ExecutionFence,
                    binding,
                    receiptId),
                _ =>
                {
                    effectInvoked = true;
                    onEffectInvoked?.Invoke();
                    return Task.FromResult("unexpected");
                });
        }
        finally
        {
            accessor.HttpContext = previous;
        }

        Assert.Equal(PolicyEvaluationOutcome.Error, result.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, result.ReasonCode);
        Assert.False(effectInvoked);
        return new StagedPolicyReceipt(receiptId, grant);
    }

    private static async Task<PolicyGrant> ReadPolicyGrantAsync(PolicyReceiptAdmissionHarness harness)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT grant_id, revision, step_id, purpose, execution_fence
            FROM "{harness.OwnerSchema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND is_current AND grant_state = 'active' AND action_ids ? @action
            ORDER BY created_at DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("project", harness.Project.ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("session", harness.Root.RootSessionId);
        command.Parameters.AddWithValue("action", AdmissionPolicyActionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PolicyGrant(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            AdmissionPolicyActionId,
            reader.GetInt64(4));
    }

    private static async Task ChangeOwnerReceiptStateAsync(
        PolicyReceiptAdmissionHarness harness,
        string change)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        switch (change)
        {
            case "session-completed":
            {
                await using var command = new NpgsqlCommand($"""
                    UPDATE "{harness.OwnerSchema}".coordination_sessions
                    SET lifecycle_state = 'completed', turn_state = 'completed',
                        state_version = state_version + 1
                    WHERE project_id = @project AND run_id = @run AND session_id = @session
                        AND lifecycle_state = 'active'
                    """, connection, transaction);
                BindOwnerRun(command, harness);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                break;
            }
            case "run-completed":
            {
                await using var command = new NpgsqlCommand($"""
                    UPDATE "{harness.OwnerSchema}".accepted_runs
                    SET execution_state = 'completed', state_version = state_version + 1
                    WHERE project_id = @project AND run_id = @run AND execution_state <> 'completed'
                    """, connection, transaction);
                BindOwnerRun(command, harness);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                break;
            }
            case "fence-advanced":
            {
                await using (var updateRun = new NpgsqlCommand($"""
                    UPDATE "{harness.OwnerSchema}".accepted_runs
                    SET execution_fence = execution_fence + 1, state_version = state_version + 1
                    WHERE project_id = @project AND run_id = @run
                    """, connection, transaction))
                {
                    BindOwnerRun(updateRun, harness);
                    Assert.Equal(1, await updateRun.ExecuteNonQueryAsync());
                }
                await using (var updateSession = new NpgsqlCommand($"""
                    UPDATE "{harness.OwnerSchema}".coordination_sessions
                    SET execution_fence = execution_fence + 1, state_version = state_version + 1
                    WHERE project_id = @project AND run_id = @run AND session_id = @session
                    """, connection, transaction))
                {
                    BindOwnerRun(updateSession, harness);
                    Assert.Equal(1, await updateSession.ExecuteNonQueryAsync());
                }
                break;
            }
            case "selection-superseded":
            {
                await using var command = new NpgsqlCommand($"""
                    UPDATE "{harness.OwnerSchema}".accepted_runs
                    SET accepted_selection_hash = 'superseded-selection',
                        state_version = state_version + 1
                    WHERE project_id = @project AND run_id = @run
                    """, connection, transaction);
                BindOwnerRun(command, harness);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        await transaction.CommitAsync();
    }

    private static void BindOwnerRun(NpgsqlCommand command, PolicyReceiptAdmissionHarness harness)
    {
        command.Parameters.AddWithValue("project", harness.Project.ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        if (command.CommandText.Contains("@session", StringComparison.Ordinal))
            command.Parameters.AddWithValue("session", harness.Root.RootSessionId);
    }

    private static async Task ChangeGrantAsync(
        PolicyReceiptAdmissionHarness harness,
        PolicyGrant grant,
        string change)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        var sql = change switch
        {
            "superseded" => $"""
                UPDATE "{harness.OwnerSchema}".executable_action_grants
                SET is_current = false, grant_state = 'superseded'
                WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                    AND revision = @revision AND is_current
                """,
            "expired" => $"""
                UPDATE "{harness.OwnerSchema}".executable_action_grants
                SET expires_at = clock_timestamp() - interval '1 minute'
                WHERE project_id = @project AND run_id = @run AND grant_id = @grant
                    AND revision = @revision AND is_current
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("project", harness.Project.ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("grant", grant.GrantId);
        command.Parameters.AddWithValue("revision", grant.Revision);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<EventsJournalSnapshot> ReadEventsJournalSnapshotAsync(
        PolicyReceiptAdmissionHarness harness)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{harness.EventsSchema}".session_events
                    WHERE project_id = @project AND run_id = @run),
                COALESCE((SELECT last_position FROM "{harness.EventsSchema}".session_run_streams
                    WHERE project_id = @project AND run_id = @run), 0),
                (SELECT count(*) FROM "{harness.EventsSchema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events-and-sessions.session-events'),
                (SELECT count(*) FROM "{harness.EventsSchema}".outbox_events
                    WHERE stream_id = @stream),
                COALESCE((SELECT last_sequence FROM "{harness.EventsSchema}".outbox_streams
                    WHERE stream_id = @stream), 0),
                (SELECT count(*) FROM "{harness.EventsSchema}".session_object_references
                    WHERE project_id = @project AND run_id = @run)
            """, connection);
        command.Parameters.AddWithValue("project", harness.Project.ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        command.Parameters.AddWithValue("stream", $"sessions/{harness.Project.ProjectId}/{RunId}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new EventsJournalSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private static async Task<long> ReadPolicyOwnerReceiptCountAsync(
        PolicyReceiptAdmissionHarness harness)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*)
            FROM "{harness.OwnerSchema}".policy_evaluation_receipts
            WHERE project_id = @project AND run_id = @run
            """, connection);
        command.Parameters.AddWithValue("project", harness.Project.ProjectId);
        command.Parameters.AddWithValue("run", RunId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForPolicyGrantLockWaitAsync(
        PolicyReceiptAdmissionHarness harness)
    {
        await using var database = NpgsqlDataSource.Create(harness.ConnectionString);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND query LIKE '%executable_action_grants%'
            )
            """, connection);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await command.ExecuteScalarAsync() is true)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for the policy owner grant-row lock.");
    }

    private sealed class DuplicatePolicyReceiptJournal : IExecutableActionPolicyEvaluationJournal
    {
        public Task<ExecutableActionPolicyEvaluationAppendResult> AppendReceiptAsync(
            SessionIdentity identity,
            Guid receiptId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ExecutableActionPolicyEvaluationAppendResult(IsDuplicate: true));
        }
    }

    private sealed class PolicyOwnerAdmissionBarrierHandler(
        HttpMessageHandler innerHandler,
        PolicyOwnerAdmissionBarrier barrier) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await barrier.BeforeRequestAsync(request, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);
            await barrier.AfterResponseAsync(request, cancellationToken);
            return response;
        }
    }

    private sealed class PolicyOwnerAdmissionBarrier
    {
        private readonly object _sync = new();
        private string? _pathSuffix;
        private bool _afterResponse;
        private int _matchingRequest;
        private int _matches;
        private bool _blocked;
        private TaskCompletionSource _entered = NewSource();
        private TaskCompletionSource _release = NewSource();

        public void ArmAfterResponse(string pathSuffix, int matchingRequest) =>
            Arm(pathSuffix, matchingRequest, afterResponse: true);

        public void ArmBeforeRequest(string pathSuffix, int matchingRequest) =>
            Arm(pathSuffix, matchingRequest, afterResponse: false);

        public Task WaitUntilBlockedAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        public void Release() => _release.TrySetResult();

        public Task BeforeRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            PauseAsync(request, afterResponse: false, cancellationToken);

        public Task AfterResponseAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            PauseAsync(request, afterResponse: true, cancellationToken);

        private void Arm(string pathSuffix, int matchingRequest, bool afterResponse)
        {
            lock (_sync)
            {
                _pathSuffix = pathSuffix;
                _afterResponse = afterResponse;
                _matchingRequest = matchingRequest;
                _matches = 0;
                _blocked = false;
                _entered = NewSource();
                _release = NewSource();
            }
        }

        private async Task PauseAsync(
            HttpRequestMessage request,
            bool afterResponse,
            CancellationToken cancellationToken)
        {
            Task? wait = null;
            lock (_sync)
            {
                if (!_blocked &&
                    _afterResponse == afterResponse &&
                    request.RequestUri?.AbsolutePath.EndsWith(_pathSuffix ?? "\0", StringComparison.Ordinal) == true &&
                    ++_matches == _matchingRequest)
                {
                    _blocked = true;
                    _entered.TrySetResult();
                    wait = _release.Task;
                }
            }

            if (wait is not null)
                await wait.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PolicyGrant(
        string GrantId,
        string Revision,
        string StepId,
        string Purpose,
        string ActionId,
        long Fence);

    private sealed record StagedPolicyReceipt(Guid ReceiptId, PolicyGrant Grant);

    private readonly record struct EventsJournalSnapshot(
        long Events,
        long Position,
        long Inbox,
        long Outbox,
        long OutboxSequence,
        long ObjectReferences);

    private sealed class PolicyReceiptAdmissionHarness(
        X509Certificate2 certificate,
        ProjectsConfigResourceServer projects,
        TemporaryEnvironment serviceEnvironment,
        OrchestratorIntegrationFactory orchestratorFactory,
        EventsIntegrationFactory eventsFactory,
        HttpClient orchestrator,
        HttpClient events,
        string connectionString,
        string ownerSchema,
        string eventsSchema,
        ProjectSummary project,
        ProjectRoleAssignmentRecord runnerRole,
        string runToken,
        AcceptedRoot root) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public X509Certificate2 Certificate { get; } = certificate;
        public ProjectsConfigResourceServer Projects { get; } = projects;
        public TemporaryEnvironment ServiceEnvironment { get; } = serviceEnvironment;
        public OrchestratorIntegrationFactory OrchestratorFactory { get; } = orchestratorFactory;
        public EventsIntegrationFactory EventsFactory { get; } = eventsFactory;
        public HttpClient Orchestrator { get; } = orchestrator;
        public HttpClient Events { get; } = events;
        public string OwnerSchema { get; } = ownerSchema;
        public string EventsSchema { get; } = eventsSchema;
        public ProjectSummary Project { get; } = project;
        public ProjectRoleAssignmentRecord RunnerRole { get; } = runnerRole;
        public string RunToken { get; } = runToken;
        public AcceptedRoot Root { get; } = root;

        public async ValueTask DisposeAsync()
        {
            Events.Dispose();
            Orchestrator.Dispose();
            await EventsFactory.DisposeAsync();
            await OrchestratorFactory.DisposeAsync();
            ServiceEnvironment.Dispose();
            await Projects.DisposeAsync();
            Certificate.Dispose();
        }
    }
}
