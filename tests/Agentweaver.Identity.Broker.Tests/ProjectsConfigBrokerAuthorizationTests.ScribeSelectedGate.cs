extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    public async Task SelectedScribeGateCheckpointsKnowledgeReceiptAndReplaysAfterServerRestart()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        await using var database = await KnowledgeTestDatabase.CreateAsync(_connectionString);
        var memory = new ProviderRegistration(database.Provider.Descriptor, true,
            database.Options.OptionsRevision, database.Options.OptionsSchemaVersion);
        var sandbox = new ProviderRegistration(new ProviderDescriptor(
            ProviderSeam.Sandbox, "scribe-sandbox", new Version(1, 0, 0), 1,
            ProviderHostingPattern.InProcess, ImmutableHashSet.Create(StringComparer.Ordinal, "container.create")),
            true, "options-v1", 1);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [memory, sandbox, new(GitHubSourceControlAdapter.CreateDescriptor(), true, "source-v1", 1)],
            [new(ProviderSeam.Memory, memory.Descriptor.Id), new(ProviderSeam.Sandbox, sandbox.Descriptor.Id),
                new(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []).Value);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, signingKey, providerCatalog: catalog, additionalAudiences: ["https://api.test/"]);
        var token = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/", "selected-scribe-owner",
            additionalScopes: "projects.admin projects.orchestrator", roles: []);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var subject = SingleClaim(jwt.Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(
            projects.Client, token, TenantId, "Selected Scribe gate");
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);
        var selected = await AcceptKnowledgeRunSelectionAsync(
            projects, database, project, token, includeScribeChild: true);
        token = CreateSignedAccessToken(subject, new Claim("tenant_id", TenantId),
            new Claim("project_id", project.ProjectId), new Claim("run_id", selected.RunId),
            new Claim("scope", "projects.orchestrator"));
        jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var actor = new CoordinationActor(jwt.Issuer, subject);
        var root = new SessionIdentity(project.ProjectId, selected.RunId, "root");
        var schema = "scribe_gate_" + Guid.NewGuid().ToString("N");
        using var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__Orchestrator"] = _connectionString,
            ["Orchestrator__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["Orchestrator__Schema"] = schema,
            ["Orchestrator__KnowledgeOwner__OwnerBaseAddress"] = "https://knowledge.test/",
            ["ProjectsConfig__AuthorizationContext__OwnerBaseAddress"] = "https://projects.test/",
            ["ProjectsConfig__AuthorizationContext__Audience"] = "https://api.test",
            ["EventsAndSessions__Authorization__OwnerBaseAddress"] = "https://events.test/",
            ["EventsAndSessions__Authorization__Audience"] = "https://api.test",
            ["Identity__Issuer"] = IdentityBrokerWebApplicationFactory.Issuer,
            ["Identity__Audience"] = "https://api.test"
        });
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, schema);
        const string planId = "selected-scribe-plan";
        const string associationId = "accepted-child";
        const string result = "Full child result\nincluding all output and provenance.";
        long stateVersion = 1;
        long fence = 0;
        MafExecutionScribeGateIntent? persistedIntent = null;
        MafExecutionCheckpointSnapshot? persistedCheckpoint = null;
        WorkPlanSnapshot? checkpointPlan = null;
        CoordinatorDecisionCurrentState? currentDecision = null;
        var ownerCalls = 0;
        var rootPath = $"/api/projects/{root.ProjectId}/runs/{root.RunId}/coordination/sessions/root";
        var dispatchPath = rootPath + "/actions/dispatch";

        WebApplicationFactory<OrchestratorHost::Program> CreateOwner(Func<HttpMessageHandler> knowledgeHandler)
        {
            var factory = new OrchestratorIntegrationFactory(
                _connectionString, schema, signingKey, projects.CreateHandler,
                () => new ScribeUnexpectedOwnerHandler(),
                sandboxProvider: new ScribeSandboxResourceProvider(), providerCatalog: catalog);
            return factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Orchestrator:KnowledgeOwner:OwnerBaseAddress"] = "https://knowledge.test/"
                    }));
                builder.ConfigureTestServices(services => ConfigureTestHttpClient(
                    services, "maf-scribe-knowledge",
                    () => new ScribeCountingHandler(knowledgeHandler(), () => ownerCalls++)));
            });
        }

        await using (var knowledge = await CreateKnowledgeTestAppAsync(
                         database, catalog, projects.Client, signingKey, additionalAudiences: ["https://api.test/"]))
        await using (var owner = CreateOwner(() =>
                         new ScribeLostAcknowledgmentHandler(knowledge.GetTestServer().CreateHandler())))
        using (var client = owner.CreateClient(new() { BaseAddress = new Uri("https://orchestrator.test/") }))
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "fixture"))
            };
            context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Headers["X-Agentweaver-Tenant"] = TenantId;
            var selection = await owner.Services.GetRequiredService<ProjectsRunSelectionClient>()
                .ReadAcceptedSelectionWithAuthorityAsync(context, root.ProjectId, root.RunId, default);
            var coordination = owner.Services.GetRequiredService<CoordinationOwnerStore>();
            var accepted = await coordination.AcceptRootAsync(actor, selection, root.SessionId, default);
            fence = accepted.ExecutionFence;
            var decisions = owner.Services.GetRequiredService<CoordinatorDecisionOwnerStore>();
            _ = await decisions.InitializeRootAsync(actor, root, selection, default);
            async Task ApplyAsync(string path, object body)
            {
                using var response = await SendJsonAsync(client, HttpMethod.Post, rootPath + path, token, body);
                await AssertStatusAsync(response, HttpStatusCode.OK);
                var operation = await ReadJsonAsync<CoordinatorDecisionOperationResponse>(response);
                Assert.True(operation.Accepted, JsonSerializer.Serialize(operation.Issues));
                stateVersion = operation.StateVersion;
            }
            await ApplyAsync("/actions/propose_outcome_spec", new ProposeCoordinatorOutcomeRequest(
                stateVersion, "outcome", "outcome-gate",
                new CoordinatorOutcomeSpecification("outcome", "Retain the accepted answer", "Pending Memory exists",
                    "Use the selected Scribe gate", "Keep current authority", [])));
            await ApplyAsync("/decisions/gates/outcome-gate/answer",
                new AnswerCoordinatorGateRequest(stateVersion, "outcome-answer", "approve", null));
            var workflow = new WorkflowDefinition("selected-scribe", "1", "scribe-catalog",
                WorkflowDefinitionOrigin.Generated, 2,
                [
                    new("implement", "Produce the accepted result", WorkflowStepMode.Open, 0, new(1, 1),
                        [], ["engineer"], ["execution"], ["isolated-worktree"], [], null, null),
                    new("scribe", "Retain completed results", WorkflowStepMode.Platform, 1, new(1, 1),
                        ["implement"], [], [], [], [], null, WorkflowPlatformGate.Scribe)
                ]);
            await ApplyAsync("/actions/select_workflow",
                new SelectCoordinatorWorkflowRequest(stateVersion, "workflow", "workflow-gate", workflow.Id, workflow));
            await ApplyAsync("/decisions/gates/workflow-gate/answer",
                new AnswerCoordinatorGateRequest(stateVersion, "workflow-answer", "approve", null));
            var plan = new WorkPlan(planId, workflow.Id, workflow.Revision, workflow.CatalogVersion,
                [new(associationId, "implement", "Produce result", "Return the accepted output", "engineer",
                    "actual-producer", "execution", "knowledge-auth-model", sandbox.Descriptor.Id,
                    "isolated-worktree", [], ["output.txt"])]);
            await ApplyAsync("/actions/propose_work_plan",
                new ProposeCoordinatorWorkPlanRequest(stateVersion, "plan", "plan-gate", plan));
            await ApplyAsync("/decisions/gates/plan-gate/answer",
                new AnswerCoordinatorGateRequest(stateVersion, "plan-answer", "approve", null));
            var decision = await decisions.ReadCurrentAsync(actor, root, selection, default);
            currentDecision = decision;
            checkpointPlan = decision.State.ConfirmedWorkPlan!;
            var sourceControl = owner.Services.GetRequiredService<SourceControlOwnerStore>();
            var resolver = owner.Services.GetRequiredService<ProviderResolver>();
            var candidate = resolver.Resolve(new ProviderResolutionRequest(
                ProviderSeam.SourceControl, null, new Version(1, 0, 0), 1,
                ImmutableHashSet.Create(StringComparer.Ordinal, SourceControlCapabilities.RepositoryRead)))
                .Value!.Candidate!;
            var binding = resolver.Pin(root.RunId, candidate, "scribe-repository",
                new ResourceNegotiation(new(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub,
                    "scribe-repository", 1), candidate.RequiredCapabilities)).Value!;
            var acceptedRun = new SourceControlAcceptedRunBinding(
                actor.Issuer, actor.Subject, TenantId, root.ProjectId, root.RunId, root.SessionId,
                decision.SelectionHash, selection.Selection.ProjectRevision,
                selection.Selection.ProjectConfigurationRevision, selection.Selection.PlatformRuntimeRevision,
                selection.Selection.ContextRevision, fence);
            var pin = new SourceControlRepositoryPin("scribe-source-pin", acceptedRun, binding,
                new("octo", "scribe-fixture"),
                new(new SecretRef("fixture-api", "1"), SourceControlSecretPurposes.Api),
                null, null, 123, "main", false, DateTimeOffset.UtcNow);
            await sourceControl.PersistRepositoryPinAsync(actor, root, selection, pin, stateVersion, default);
            var outputBytes = Encoding.UTF8.GetBytes(result);
            var document = GitWorkspaceCapturePackage.Create(new GitWorkspaceCapture(
                "scribe-workspace", root.RunId, binding.Resource.ResourceId, binding.Resource.Generation,
                Guid.NewGuid(), new string('a', 40), new string('b', 40), "diff --git a/output.txt b/output.txt\n",
                [new("output.txt", "100644", GitWorkspaceCapturePackage.Hash(outputBytes),
                    outputBytes.LongLength, ImmutableArray.CreateRange(outputBytes))], "main"));
            var capture = await sourceControl.RegisterOutputCaptureAsync(
                actor, root, selection, pin, stateVersion, document, default);
            await sourceControl.AdmitOutputCaptureAsync(actor, root, selection, pin, stateVersion,
                capture.Proof.CaptureId, new ProducedRunCaptureJournalEntry(capture.Proof, 1), default);
            var childId = MafExecutionIds.CreateChildSessionId(root, planId, associationId);
            _ = await coordination.RegisterChildAsync(actor, root, childId, 4, 2, default,
                new ConfirmedWorkPlanItemAssociation(associationId, stateVersion, decision.SelectionHash));
            // Fixture starts after the accepted child's successful completion, not after Scribe dispatch.
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var complete = new NpgsqlCommand($"""
                UPDATE "{schema}".coordination_sessions SET lifecycle_state = 'completed', turn_state = 'idle'
                WHERE project_id = @project AND run_id = @run AND session_id = @child
                """, connection))
            {
                complete.Parameters.AddWithValue("project", root.ProjectId);
                complete.Parameters.AddWithValue("run", root.RunId);
                complete.Parameters.AddWithValue("child", childId);
                Assert.Equal(1, await complete.ExecuteNonQueryAsync());
            }
            var execution = new MafExecutionCheckpointStore(
                owner.Services.GetRequiredService<PostgresMafCheckpointStore>(),
                CoordinationEndpoints.CreateExecutionCheckpointBinding(root, actor, fence,
                    MafExecutionCheckpointStore.CurrentSdkVersion, "knowledge-auth-model"));
            _ = await execution.AppendAsync("completed-child", null,
                new MafExecutionCheckpoint(1, planId, stateVersion, MafExecutionProgress.Empty with
                {
                    WorkItems = MafExecutionProgress.Empty.WorkItems.Add(associationId, MafExecutionTaskStatus.Succeeded)
                })
                {
                    Results = ImmutableDictionary<string, string>.Empty.Add(associationId, result)
                }, default);
            using var dispatch = await SendJsonAsync(client, HttpMethod.Post, dispatchPath, token,
                new MafExecutionDispatchRequest(stateVersion, planId));
            await AssertStatusAsync(dispatch, HttpStatusCode.BadGateway);
            persistedCheckpoint = await execution.ReadLatestAsync(default);
            Assert.NotNull(persistedCheckpoint);
            persistedIntent = Assert.Single(persistedCheckpoint.State.ScribeIntents).Value;
            Assert.Equal(actor, persistedIntent.Actor);
            Assert.Equal(result, persistedCheckpoint.State.Results[associationId]);
            Assert.Empty(persistedCheckpoint.State.ScribeReceipts);
            Assert.Equal(MafExecutionTaskStatus.Running, persistedCheckpoint.State.Progress.NonModelSteps["scribe"]);
            Assert.Equal(1, ownerCalls);
        }
        var committed = Assert.Single((await database.Provider.SearchAsync(
            new KnowledgeRecordQuery(root.ProjectId, "actual-producer", IncludeInactive: true), default)).Items);
        Assert.Equal(KnowledgeTrustState.Pending, committed.TrustState);
        Assert.Equal(KnowledgeRecordKind.Memory, committed.Kind);
        Assert.Equal("workflow-output", committed.Type);
        Assert.Contains(result.Split('\n')[0], committed.Content, StringComparison.Ordinal);

        await using var restartedKnowledge = await CreateKnowledgeTestAppAsync(
            database, catalog, projects.Client, signingKey, additionalAudiences: ["https://api.test/"]);
        await using var restartedOwner = CreateOwner(() => restartedKnowledge.GetTestServer().CreateHandler());
        using var restartedClient = restartedOwner.CreateClient(new() { BaseAddress = new Uri("https://orchestrator.test/") });
        var restartedExecution = new MafExecutionCheckpointStore(
            restartedOwner.Services.GetRequiredService<PostgresMafCheckpointStore>(),
            CoordinationEndpoints.CreateExecutionCheckpointBinding(root, actor, fence,
                MafExecutionCheckpointStore.CurrentSdkVersion, "knowledge-auth-model"));
        Assert.Equal(persistedCheckpoint!.Info.CheckpointId,
            (await restartedExecution.ReadLatestAsync(default))!.Info.CheckpointId);
        using (var replay = await SendJsonAsync(restartedClient, HttpMethod.Post, dispatchPath, token,
                   new MafExecutionDispatchRequest(stateVersion, planId)))
        {
            await AssertStatusAsync(replay, HttpStatusCode.OK);
            Assert.True((await ReadJsonAsync<MafExecutionDispatchResponse>(replay)).IsComplete);
        }
        var recovered = await restartedExecution.ReadLatestAsync(default);
        Assert.NotNull(recovered);
        Assert.True(MafScribeGateContract.Matches(persistedIntent!, recovered.State.ScribeIntents["scribe"]));
        var receipt = Assert.Single(recovered.State.ScribeReceipts).Value;
        Assert.Equal(new MafScribeTerminalReceipt(
            associationId, committed.RecordId, committed.Revision, committed.RevisionId), receipt);
        Assert.Equal(MafExecutionTaskStatus.Succeeded, recovered.State.Progress.NonModelSteps["scribe"]);
        Assert.Equal(2, ownerCalls);
        using (var replay = await SendJsonAsync(restartedClient, HttpMethod.Post, dispatchPath, token,
                   new MafExecutionDispatchRequest(stateVersion, planId)))
        {
            await AssertStatusAsync(replay, HttpStatusCode.OK);
            Assert.True((await ReadJsonAsync<MafExecutionDispatchResponse>(replay)).IsComplete);
        }
        Assert.Equal(2, ownerCalls);
        Assert.DoesNotContain("Full child result", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);

        var gate = recovered.State.ScribeIntents["scribe"];
        var outputSet = MafExecutionOutputWitness.CreateCompletePlanOutputSet(checkpointPlan!, recovered, root);
        var missingSource = Assert.Throws<CoordinationException>(() => MafExecutionOwnerEvidenceContract.Serialize(
            new(actor, root, gate.AcceptedSelectionHash, fence, currentDecision!, recovered,
                checkpointPlan!, outputSet.OutputSet), TenantId, [], []));
        Assert.Equal("maf_execution_output_witness_evidence_invalid", missingSource.Code);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var witness = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{schema}\".maf_execution_output_witnesses", connection))
            Assert.Equal(1L, await witness.ExecuteScalarAsync());
        Assert.Throws<CoordinationException>(() => MafScribeGateContract.RequireReplay(
            gate, root, checkpointPlan!, recovered.State, fence, gate.AcceptedSelectionHash,
            actor with { Subject = "changed" }));
        Assert.Throws<CoordinationException>(() => MafScribeGateContract.RequireReplay(
            gate, root, checkpointPlan!, recovered.State with
            {
                Results = recovered.State.Results.SetItem(associationId, "Changed result")
            }, fence, gate.AcceptedSelectionHash, actor));
        Assert.Throws<CoordinationException>(() => MafScribeGateContract.RequireReplay(
            gate, new(root.ProjectId, "changed-run", root.SessionId), checkpointPlan!,
            recovered.State, fence, gate.AcceptedSelectionHash, actor));
        var backup = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, "scribe-gate-backup", TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, backup.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(projects.PrivilegedFixtureDataSource), TimeProvider.System)
            .RevokeMembershipAsync(membership.MembershipId, 1, "local-test-fixture");
        using (var revoked = await SendJsonAsync(restartedClient, HttpMethod.Post, dispatchPath, token,
                   new MafExecutionDispatchRequest(stateVersion, planId)))
            await AssertStatusAsync(revoked, HttpStatusCode.Forbidden);
        Assert.Equal(2, ownerCalls);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var counts = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{database.Options.Schema}".knowledge_records),
                   (SELECT count(*) FROM "{database.Options.Schema}".knowledge_revisions),
                   (SELECT count(*) FROM "{database.Options.Schema}".knowledge_write_idempotency)
            """, connection))
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
        }
        output.WriteLine($"Selected Scribe dispatch -> PostgreSQL intent -> Knowledge commit -> lost ack -> " +
            $"server restart -> compact checkpoint receipt: {receipt.RecordId}/{receipt.Revision}/{receipt.RevisionId}; " +
            "one record, one immutable revision, one idempotency row.");
    }

    private sealed class ScribeSandboxResourceProvider : ICoordinatorSandboxResourceProvider
    {
        public string ProviderId => "scribe-sandbox";
        public ImmutableArray<string> IsolationChoices => ["isolated-worktree"];
        public Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
            ProviderCandidate candidate, OrchestratorHost::Agentweaver.Orchestrator.EffectiveRunSelection selection,
            CancellationToken cancellationToken) => Task.FromResult(ProviderResult<ResourceNegotiation>.Success(
                new(new(ProviderSeam.Sandbox, ProviderId, "scribe-fixture", 1), candidate.RequiredCapabilities)));
    }

    private sealed class ScribeUnexpectedOwnerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The selected Scribe gate must not invoke unrelated owners.");
    }

    private sealed class ScribeCountingHandler(HttpMessageHandler inner, Action count) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return base.SendAsync(request, cancellationToken);
        }
    }
}
