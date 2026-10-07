extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
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

    private static readonly JsonSerializerOptions CoordinationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending()
    {
        using var signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(signingCertificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, signingKey);

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
        Assert.Contains(claims, claim => claim.Type == "project_id" && claim.Value == project.ProjectId);
        Assert.Contains(claims, claim => claim.Type == "run_id" && claim.Value == RunId);

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
                    Casting = [new ProjectAgentCast("test-agent", "implementer", 0)]
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
            ["Identity__Audience"] = "https://api.test"
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsAndSessionsMigrator.MigrateAsync(migrations, eventsSchema);
        }

        EventsIntegrationFactory? eventsFactoryReference = null;
        var admissions = new List<(HttpStatusCode Status, bool NoStore, string Body)>();
        var cacheObjectStore = new InMemoryObjectStore();
        var sandboxProvider = new ControlledSandboxResourceProvider();
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString,
            ownerSchema,
            signingKey,
            projects.CreateHandler,
            () => new CapturingHandler(
                eventsFactoryReference!.Server.CreateHandler(),
                (status, noStore, body) => admissions.Add((status, noStore, body))),
            cacheObjectStore,
            sandboxProvider);
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString,
            eventsSchema,
            signingKey,
            projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler());
        eventsFactoryReference = eventsFactory;

        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test")
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
                    WorkflowPlatformGate.BuildTest)
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
        var denyingJournal = new DenyingSessionsJournal();
        var sourceReceiptGuard = new ExecutableActionGuard(
            policyProvider,
            policyOptions,
            denyingJournal,
            receiptStore,
            sourceReceiptWriter: receiptStore);
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
                    effectInvoked = true;
                    return Task.FromResult("unexpected");
                });
        }
        finally
        {
            httpContextAccessor.HttpContext = previousHttpContext;
        }
        Assert.Equal(PolicyEvaluationOutcome.Error, receiptAttempt.Outcome);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, receiptAttempt.ReasonCode);
        Assert.False(receiptAttempt.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(PolicyEvaluationReasonCode.EvaluationFailed, duplicateReceiptAttempt.ReasonCode);
        Assert.False(duplicateReceiptAttempt.EffectInvoked);
        Assert.False(effectInvoked);
        Assert.Equal(1, denyingJournal.AppendCalls);

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

        using var proposedPlan = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/actions/propose_work_plan",
            runToken,
            new ProposeCoordinatorWorkPlanRequest(
                11, "work-plan-proposal-1", "work-plan-gate-1", acceptedPlan));
        Assert.True(
            proposedPlan.StatusCode == HttpStatusCode.OK,
            $"{proposedPlan.StatusCode}: {await proposedPlan.Content.ReadAsStringAsync()}");
        var proposedPlanResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(proposedPlan);
        Assert.True(proposedPlanResult.Accepted);
        Assert.Equal(12, proposedPlanResult.StateVersion);
        Assert.Equal("work-plan-gate-1", proposedPlanResult.PendingGate?.RequestId);
        Assert.Equal(1, sandboxProvider.NegotiationCount);
        await AssertAcceptedSandboxBindingAsync(
            _connectionString, ownerSchema, project.ProjectId, sandboxProvider.ResourceId);

        using var confirmedPlan = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/decisions/gates/work-plan-gate-1/answer",
            runToken,
            new AnswerCoordinatorGateRequest(12, "work-plan-answer-1", "approve", null));
        Assert.Equal(HttpStatusCode.OK, confirmedPlan.StatusCode);
        var confirmedPlanResult =
            await ReadJsonAsync<CoordinatorDecisionOperationResponse>(confirmedPlan);
        Assert.True(confirmedPlanResult.Accepted);
        Assert.Equal(13, confirmedPlanResult.StateVersion);

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
        using (var assemblyRequest = await SendJsonAsync(
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
                           WorkflowPlatformGate.BuildTest))))
        {
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
        Assert.Equal(1, sandboxProvider.NegotiationCount);

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

        using var secondChildResponse = await SendJsonAsync(
            orchestrator,
            HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/children",
            runToken,
            new RegisterChildRequest("child-two"));
        await AssertStatusAsync(secondChildResponse, HttpStatusCode.Created);
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

        using var events = eventsFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://events.test")
        });
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
        var journalEvent = Assert.Single(journal.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("addressedMessage", journalEvent.GetProperty("kind").GetString());
        Assert.Equal(
            presented.MessageId,
            journalEvent.GetProperty("payload").GetProperty("messageId").GetGuid());

        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource,
            runnerRole.AssignmentId,
            1);
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
            FROM "{schema}".coordinator_run_selection_contexts
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
            UPDATE "{schema}".coordinator_run_selection_contexts
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

    private sealed class OrchestratorIntegrationFactory(
        string connectionString,
        string schema,
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandler,
        Func<HttpMessageHandler> eventsHandler,
        IObjectStore? objectStore = null,
        ICoordinatorSandboxResourceProvider? sandboxProvider = null)
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
                services.AddSingleton(NpgsqlDataSource.Create(connectionString));
                services.RemoveAll<ProviderCatalog>();
                services.AddSingleton(CreateSandboxProviderCatalog());
                AddJwtBearer(services, signingKey);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.ProjectsRunSelectionClient>()
                    .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                services.AddHttpClient<OrchestratorHost::Agentweaver.Orchestrator.EventsAddressedMessageClient>()
                    .ConfigurePrimaryHttpMessageHandler(eventsHandler);
                if (objectStore is not null)
                    services.AddSingleton<IObjectStore>(objectStore);
                if (sandboxProvider is not null)
                    services.AddSingleton<ICoordinatorSandboxResourceProvider>(sandboxProvider);
            });
        }
    }

    private sealed class ControlledSandboxResourceProvider : ICoordinatorSandboxResourceProvider
    {
        private int _negotiationCount;

        public string ProviderId => "sandbox-platform";
        public string ResourceId => "controlled-sandbox-resource";
        public int NegotiationCount => Volatile.Read(ref _negotiationCount);
        public ImmutableArray<string> IsolationChoices { get; } = ["isolated-worktree"];

        public Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
            ProviderCandidate candidate,
            OrchestratorHost::Agentweaver.Orchestrator.EffectiveRunSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Seam != ProviderSeam.Sandbox || candidate.ProviderId != ProviderId ||
                selection.RunId != RunId)
                return Task.FromResult(ProviderResult<ResourceNegotiation>.Failure(
                    ProviderErrorCode.InvalidNegotiation,
                    "The controlled Sandbox provider received an unexpected candidate."));

            Interlocked.Increment(ref _negotiationCount);
            return Task.FromResult(ProviderResult<ResourceNegotiation>.Success(
                new ResourceNegotiation(
                    new ProviderResourceRef(
                        ProviderSeam.Sandbox, ProviderId, ResourceId, 7),
                    candidate.RequiredCapabilities)));
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
            var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
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
                services.AddSingleton(NpgsqlDataSource.Create(connectionString));
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
