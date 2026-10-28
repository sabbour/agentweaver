extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
    public async Task PlacementEgressRequiresCompletePinnedEvidenceWithoutClaimingHostReadiness()
    {
        var seed = RuntimeCopilotSessionTests.Registration();
        await using var host = new RuntimeAgentHostTests.HostFixture(workflowBound: true,
            registrationOverride: seed with
            {
                Binding = seed.Binding with
                {
                    EnvironmentCurrentFencingGeneration = 1, EnvironmentProviderFencingGeneration = 1,
                    EnvironmentLifecycleGeneration = 1, EnvironmentLeaseRevision = 1,
                    PlacementProviderId = "sandbox-test",
                    ConfigureEndpoint = new("https://runtime.test/runtime/v1/configure"),
                    Image = new("sha256:" + new string('a', 64), "linux/amd64", 123456789)
                }
            });
        var registration = host.Registration;
        var binding = registration.Binding;
        var readiness = host.Readiness;
        RuntimeEgressVerificationRequest Require(
            RuntimeRegistration current, EnvironmentRuntimeReadinessContext placement) =>
            MafExecutionEgressGuard.RequireReadiness(current, placement, binding.ProjectId, binding.RunId,
                binding.SessionId, binding.ContextRevision, binding.AcceptedSelectionHash.ToUpperInvariant(),
                binding.ExecutionFence, host.Time.GetUtcNow());

        Assert.Equal(SandboxObservedState.Pending, readiness.Observation.State);
        Assert.Equal(readiness.Observation.VerifiedNetworkGeneration, Require(registration, readiness).PolicyGeneration);
        Assert.DoesNotContain(readiness.Observation.StartupPhases,
            phase => phase.Phase is SandboxStartupPhase.Configured or SandboxStartupPhase.Ready);
        var ready = readiness with
        {
            Observation = readiness.Observation with
            {
                State = SandboxObservedState.Ready,
                StartupPhases = readiness.Observation.StartupPhases
                    .Add(new(SandboxStartupPhase.Configured, 1, host.Time.GetUtcNow()))
                    .Add(new(SandboxStartupPhase.Ready, 1, host.Time.GetUtcNow()))
            }
        };
        Assert.Equal(Require(registration, readiness), Require(registration, ready));
        foreach (var observation in new[]
        {
            readiness.Observation with { State = SandboxObservedState.Absent },
            readiness.Observation with { State = SandboxObservedState.Failed },
            readiness.Observation with { State = SandboxObservedState.Finished },
            readiness.Observation with { VmIsolationVerified = false },
            readiness.Observation with { WorkspaceAttachmentVerified = false },
            readiness.Observation with { VerifiedNetworkGeneration = null },
            readiness.Observation with { FencingGeneration = binding.ExecutionFence + 1 },
            readiness.Observation with { StartupPhases = default },
            readiness.Observation with { StartupPhases = readiness.Observation.StartupPhases.Add(null!) },
            readiness.Observation with { StartupPhases = readiness.Observation.StartupPhases.RemoveAt(0) },
            readiness.Observation with { State = SandboxObservedState.Ready },
            readiness.Observation with
            {
                StartupPhases = readiness.Observation.StartupPhases.Add(readiness.Observation.StartupPhases[0])
            },
            ready.Observation with { StartupPhases = ready.Observation.StartupPhases.RemoveAt(3) },
            readiness.Observation with
            {
                StartupPhases = readiness.Observation.StartupPhases.SetItem(1,
                    new SandboxStartupPhaseObservation(SandboxStartupPhase.ImageReady, 1,
                        readiness.Observation.StartupPhases[1].ObservedAt,
                        "sha256:" + new string('b', 64), binding.Image!.CompressedPullBytes))
            }
        })
            Assert.Equal("maf_execution_egress_stale",
                Assert.Throws<OrchestratorHost::Agentweaver.Orchestrator.CoordinationException>(() =>
                    Require(registration, readiness with { Observation = observation })).Code);
        foreach (var changed in new[]
        {
            registration with { ExpiresAt = host.Time.GetUtcNow() },
            registration with { Binding = binding with { ExecutionFence = binding.ExecutionFence + 1 } },
            registration with { Binding = binding with { AcceptedSelectionHash = new string('b', 64) } },
            registration with { Binding = binding with { EnvironmentLeaseRevision = binding.EnvironmentLeaseRevision + 1 } }
        })
            Assert.Equal("maf_execution_egress_stale",
                Assert.Throws<OrchestratorHost::Agentweaver.Orchestrator.CoordinationException>(() =>
                    Require(changed, readiness)).Code);
        foreach (var changed in new[]
        {
            readiness with { Placement = readiness.Placement with { LeaseExpiresAt = host.Time.GetUtcNow() } },
            readiness with
            {
                Placement = readiness.Placement with
                {
                    RuntimeOwnerContext = readiness.Placement.RuntimeOwnerContext! with
                    {
                        ExecutionFence = binding.ExecutionFence + 1
                    }
                }
            },
            readiness with
            {
                Placement = readiness.Placement with
                {
                    RuntimeOwnerContext = readiness.Placement.RuntimeOwnerContext! with
                    {
                        AcceptedSelectionHash = new string('b', 64)
                    }
                }
            }
        })
            Assert.Equal("maf_execution_egress_stale",
                Assert.Throws<OrchestratorHost::Agentweaver.Orchestrator.CoordinationException>(() =>
                    Require(registration, changed)).Code);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("candidate")]
    [InlineData("pending")]
    [InlineData("lease")]
    public async Task PublicConfirmedPlanRevalidatesPrePlanRootRegistrationBeforeEgress(string scenario)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        var policy = new AgtPolicyProviderOptions("native-join-policy", 1, "native-join-policy-v1",
            ["""
             apiVersion: governance.toolkit/v1
             version: "1.0"
             name: native-join-policy
             scope: global
             default_action: deny
             rules:
               - name: allow-accepted-child-spawn
                 condition: "action_id == 'maf.child.spawn'"
                 action: allow
                 priority: 100
               - name: allow-current-model-turn
                 condition: "action_id == 'model.turn'"
                 action: allow
                 priority: 100
             """]);
        var catalog = CreateSourceControlProviderCatalog(policy);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, signingKey, providerCatalog: catalog);
        var adminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "native-join-admin", null, null, ["platform_admin"]);
        var admin = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, admin, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        using var created = await SendJsonAsync(projects.Client, HttpMethod.Post, "/api/projects/", adminToken,
            new CreateProjectRequest { Name = "Native child join" });
        await AssertStatusAsync(created, HttpStatusCode.Created);
        var project = await ReadJsonAsync<ProjectSummary>(created);
        using var connection = new ControlledCopilotConnection();
        var linked = await LinkRuntimeCopilotConnectionAsync(projects, adminToken, connection);
        var selectionToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "native-join-runner", null, null, ["orchestrator"]);
        var subject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(selectionToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);
        using var configured = await SendJsonAsync(
            projects.Client, HttpMethod.Put, $"/api/projects/{project.ProjectId}/configuration", adminToken,
            new UpdateProjectConfigurationRequest
            {
                ExpectedRevision = project.ConfigurationRevision,
                Configuration = new ProjectConfiguration
                {
                    AgentCharters =
                    [
                        new ProjectAgentCharter("test-agent", "Test agent", "implementer", "Do the accepted work.")
                    ],
                    Casting = [new ProjectAgentCast("test-agent", "implementer", 0)]
                }
            });
        await AssertStatusAsync(configured, HttpStatusCode.OK);
        var configuration = await ReadJsonAsync<VersionedProjectConfiguration>(configured);
        using var defaults = await SendJsonAsync(
            projects.Client, HttpMethod.Put, "/api/platform/runtime-defaults/", adminToken,
            new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings(
                        "platform-model", SourceMode: ModelSourceMode.HostedCopilot, ConnectionId: linked.ConnectionId),
                    ModelTaskDefaults = [],
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 2, MaxToolCalls = 2, MaxChildren = 2, MaxConcurrentChildren = 2,
                        MaxWallTimeSeconds = 3600
                    }
                }
            });
        await AssertStatusAsync(defaults, HttpStatusCode.OK);
        using var accepted = await SendJsonAsync(
            projects.Client, HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection", selectionToken,
            new AcceptRunSelectionRequest
            {
                ExpectedProjectConfigRevision = configuration.Revision,
                ExpectedPlatformRuntimeRevision = 1,
                Context = new RunSelectionContext
                {
                    Revision = "native-join-v1",
                    AvailableModelSelectionReferences = ImmutableHashSet.Create(StringComparer.Ordinal, "platform-model"),
                    ProviderRequirements =
                    [
                        CreateSourceControlSandboxRequirement(), CreateSourceControlNetworkPolicyRequirement(),
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.Policy,
                            RequiredAdapterVersion = AgtPolicyProvider.AdapterVersion.ToString(),
                            RequiredOptionsSchemaVersion = AgtPolicyProvider.OptionsSchemaVersion,
                            RequiredCapabilities = PolicyProviderCapabilities.All
                        }
                    ]
                }
            });
        await AssertStatusAsync(accepted, HttpStatusCode.OK);
        var snapshot = await accepted.Content.ReadFromJsonAsync<ProjectRunSelectionResponse>(CoordinationJsonOptions);
        Assert.NotNull(snapshot);
        using var selectionDocument = JsonDocument.Parse(await accepted.Content.ReadAsByteArrayAsync());
        await CreateRunBindingGrantAsync(subject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "native-join-runner", project.ProjectId, RunId, ["orchestrator"]);
        var ownerSchema = "native_join_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "native_join_events_" + Guid.NewGuid().ToString("N");
        using var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__Orchestrator"] = _connectionString,
            ["Orchestrator__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["Orchestrator__Schema"] = ownerSchema,
            ["Orchestrator__RuntimeRegistration__EnvironmentOwnerAddress"] = "https://environment.test/",
            ["ConnectionStrings__EventsAndSessions"] = _connectionString,
            ["EventsAndSessions__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["EventsAndSessions__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["EventsAndSessions__Provider__ResourceId"] = "postgres-test",
            ["EventsAndSessions__Provider__DatabaseName"] = new NpgsqlConnectionStringBuilder(_connectionString).Database,
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
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, ownerSchema);
        await EventsAndSessionsMigrator.MigrateAsync(dataSource, eventsSchema);
        var routes = new Dictionary<string, Func<HttpMessageHandler>>(StringComparer.Ordinal)
        {
            ["projects.test"] = projects.CreateHandler
        };
        var failures = new ConcurrentQueue<string>();
        await using var environment = await RuntimePlacementTestServer.StartAsync(
            _connectionString, signingKey, projects.CreateHandler,
            new(TenantId, project.ProjectId, RunId, "root-environment"),
            Assert.Single(Assert.Single(snapshot.Providers, p => p.Seam == ProviderSeam.Sandbox).Candidates),
            selectionDocument.RootElement.Clone(), () => new RuntimeServiceRouter(routes, failures));
        routes.Add("environment.test", environment.CreateHandler);
        var sandbox = new ControlledSandboxResourceProvider(AgentSandboxProviderMetadata.ProviderId);
        await using var events = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => new RuntimeServiceRouter(routes, failures));
        routes.Add("events.test", () => events.Server.CreateHandler());
        await using var baseFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => events.Server.CreateHandler(), sandboxProvider: sandbox, providerCatalog: catalog,
            policyOptions: policy,
            environmentHandler: () => new RuntimeServiceRouter(routes, failures));
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                ConfigureTestHttpClient(services, "runtime-suspend-environment",
                    () => new RuntimeServiceRouter(routes, failures));
                ConfigureTestHttpClient(services, nameof(MafBuildTestEnvironmentClient),
                    () => new RuntimeServiceRouter(routes, failures));
                ConfigureTestHttpClient(services, "maf-execution-host",
                    () => new RuntimeServiceRouter(routes, failures));
            }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test/"), AllowAutoRedirect = false
        });
        routes.Add("orchestrator.test", () => factory.Server.CreateHandler());
        var rootPath = $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root";
        var registrationPath = $"/internal/projects/{project.ProjectId}/runs/{RunId}" +
            "/coordination/sessions/root/runtime-registrations";
        using var root = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/root", runToken, new AcceptRootRequest("root"));
        await AssertStatusAsync(root, HttpStatusCode.Created);
        using var rootStatusResponse = await SendAsync(client, HttpMethod.Get, rootPath + "/status", runToken, [TenantId]);
        await AssertStatusAsync(rootStatusResponse, HttpStatusCode.OK);
        var rootStatus = await ReadJsonAsync<SessionStatusSnapshot>(rootStatusResponse);
        using var boundary = await SendJsonAsync(client, HttpMethod.Post, rootPath + "/turn-boundary", runToken,
            new TurnBoundaryRequest(rootStatus.ExecutionFence, rootStatus.StateVersion));
        await AssertStatusAsync(boundary, HttpStatusCode.OK);
        using var registered = await SendJsonAsync(client, HttpMethod.Post, registrationPath, runToken,
            new RegisterRuntimeRequest("root-environment", "runtime-profile"));
        await AssertStatusAsync(registered, HttpStatusCode.OK, string.Join('\n', failures));
        var registration = await ReadJsonAsync<RuntimeRegistration>(registered);
        long version = 1;
        async Task DecideAsync(string path, object body)
        {
            using var response = await SendJsonAsync(client, HttpMethod.Post, rootPath + path, runToken, body);
            await AssertStatusAsync(response, HttpStatusCode.OK);
            var decision = await ReadJsonAsync<CoordinatorDecisionOperationResponse>(response);
            Assert.True(decision.Accepted, await response.Content.ReadAsStringAsync());
            Assert.Empty(decision.Issues);
            version = decision.StateVersion;
        }
        await DecideAsync("/actions/propose_outcome_spec",
            new ProposeCoordinatorOutcomeRequest(version, "outcome", "outcome-gate",
                new CoordinatorOutcomeSpecification("outcome", "Complete two independent tasks.",
                    "Both results are reviewed.", "Use the accepted workflow.", "Keep owner authority.", [])));
        await DecideAsync("/decisions/gates/outcome-gate/answer",
            new AnswerCoordinatorGateRequest(version, "outcome-answer", "approve", null));
        var workflow = new WorkflowDefinition("native-join", "1.0.0", "native-join-v1",
            WorkflowDefinitionOrigin.Generated, 2,
            [
                new("implement", "Complete the accepted tasks.", WorkflowStepMode.Open, 0, new(2, 2),
                    [], ["implementer"], ["implementation"], ["isolated-worktree"], [], null, null),
                new("review", "Review both completed results.", WorkflowStepMode.Platform, 1, new(0, 1),
                    ["implement"], [], [], [], [], null, WorkflowPlatformGate.IndependentReview)
            ]);
        await DecideAsync("/actions/select_workflow",
            new SelectCoordinatorWorkflowRequest(version, "workflow", "workflow-gate", workflow.Id, workflow));
        await DecideAsync("/decisions/gates/workflow-gate/answer",
            new AnswerCoordinatorGateRequest(version, "workflow-answer", "approve", null));
        var plan = new WorkPlan("native-plan", workflow.Id, workflow.Revision, workflow.CatalogVersion,
            new[] { "first", "second" }.Select(id => new WorkPlanItem(
                id, "implement", $"Complete {id}.", $"Return the completed {id} result.", "implementer",
                "test-agent", "implementation", "platform-model", AgentSandboxProviderMetadata.ProviderId,
                "isolated-worktree", [], [$"results/{id}.txt"])).ToImmutableArray());
        await DecideAsync("/actions/propose_work_plan",
            new ProposeCoordinatorWorkPlanRequest(version, "plan", "plan-gate", plan));
        if (scenario != "candidate")
            await DecideAsync("/decisions/gates/plan-gate/answer",
                new AnswerCoordinatorGateRequest(version, "plan-answer", "approve", null));
        if (scenario == "pending")
            await DecideAsync("/actions/revise_work_plan",
                new ReviseCoordinatorWorkPlanRequest(version, "revision", "revision-gate",
                    plan with
                    {
                        Items = plan.Items.SetItem(0, plan.Items[0] with
                        {
                            Task = "Complete an additional deployment task.",
                            DeclaredOutputs = ["deployment/new.txt"]
                        })
                    }));
        if (scenario == "lease")
            environment.ExpireCurrentLease();
        using var current = await SendAsync(client, HttpMethod.Get, registrationPath, runToken, [TenantId]);
        if (scenario is "candidate" or "pending" or "lease")
        {
            Assert.False(current.IsSuccessStatusCode);
            using var denied = await SendJsonAsync(client, HttpMethod.Post, rootPath + "/actions/dispatch", runToken,
                new MafExecutionDispatchRequest(version, plan.Id));
            Assert.False(denied.IsSuccessStatusCode);
            await using var db = await dataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{ownerSchema}\".coordination_sessions WHERE parent_session_id IS NOT NULL", db);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            return;
        }
        await AssertStatusAsync(current, HttpStatusCode.OK, string.Join('\n', failures));
        Assert.Equal(registration, await ReadJsonAsync<RuntimeRegistration>(current));
        using var environmentClient = new HttpClient(environment.CreateHandler())
        {
            BaseAddress = new Uri("https://environment.test/")
        };
        using var readinessResponse = await SendAsync(environmentClient, HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{RunId}/environments/root-environment" +
            $"/coordination/sessions/root/runtime-bootstrap/profiles/{registration.Binding.ProfileId}/readiness",
            runToken, [TenantId]);
        await AssertStatusAsync(readinessResponse, HttpStatusCode.OK);
        var readiness = await ReadJsonAsync<EnvironmentRuntimeReadinessContext>(readinessResponse);
        Assert.Equal(registration.Binding.EnvironmentLeaseRevision, readiness.Placement.LeaseRevision);
        Assert.Equal(registration.Binding.EnvironmentLifecycleGeneration, readiness.Placement.LifecycleGeneration);
        Assert.Equal(registration.Binding.EnvironmentCurrentFencingGeneration, readiness.Placement.CurrentFencingGeneration);
        Assert.Equal(registration.Binding.EnvironmentProviderFencingGeneration, readiness.Placement.ProviderFencingGeneration);
        Assert.True(readiness.Observation.VmIsolationVerified);
        Assert.True(readiness.Observation.WorkspaceAttachmentVerified);
        Assert.Null(readiness.Observation.StartupFailure);
        var egressRequest = MafExecutionEgressGuard.RequireReadiness(
            registration, readiness, project.ProjectId, RunId, "root", registration.Binding.ContextRevision,
            registration.Binding.AcceptedSelectionHash, registration.Binding.ExecutionFence, DateTimeOffset.UtcNow);
        using var verified = await SendJsonAsync(environmentClient, HttpMethod.Post,
            "/api/network-egress/verify", runToken, egressRequest);
        await AssertStatusAsync(verified, HttpStatusCode.OK);
        using var deniedApply = await SendJsonAsync(environmentClient, HttpMethod.Post,
            "/api/network-egress/apply", runToken, new
            {
                Fence = environment.Lease.Fence, PolicyGeneration = 2,
                ExpectedPreviousPolicyGeneration = 1, IdempotencyKey = "bound-run-must-not-control-egress"
            });
        await AssertStatusAsync(deniedApply, HttpStatusCode.Forbidden);
        using var deniedReconcile = await SendJsonAsync(environmentClient, HttpMethod.Post,
            "/api/network-egress/reconcile", runToken, new
            {
                Fence = environment.Lease.Fence, OperationId = Guid.NewGuid()
            });
        await AssertStatusAsync(deniedReconcile, HttpStatusCode.Forbidden);
        using var stillVerified = await SendJsonAsync(environmentClient, HttpMethod.Post,
            "/api/network-egress/verify", runToken, egressRequest);
        await AssertStatusAsync(stillVerified, HttpStatusCode.OK);
        using var dispatch = await SendJsonAsync(client, HttpMethod.Post, rootPath + "/actions/dispatch", runToken,
            new MafExecutionDispatchRequest(version, plan.Id));
        // Spawn commits before delivery; unconfigured children do not yet have active runtime owner turns.
        await AssertStatusAsync(dispatch, HttpStatusCode.Conflict, string.Join('\n', failures));
        Assert.Contains("runtime_owner_context_unavailable", await dispatch.Content.ReadAsStringAsync());
        var binding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
            new(project.ProjectId, RunId, "root"),
            new CoordinationActor(registration.Binding.ActorIssuer, subject),
            registration.Binding.ExecutionFence, MafExecutionCheckpointStore.CurrentSdkVersion, "platform-model");
        var checkpoint = await new MafExecutionCheckpointStore(
            factory.Services.GetRequiredService<PostgresMafCheckpointStore>(), binding).ReadLatestAsync(default);
        Assert.NotNull(checkpoint);
        Assert.Equal(2, checkpoint.State.PendingDispatches.Count);
        Assert.Empty(checkpoint.State.Results);
        Assert.Empty(checkpoint.State.ReviewIntents);
        foreach (var intent in checkpoint.State.PendingDispatches.Values)
        {
            Assert.NotNull(intent.EgressReceipt);
            Assert.Equal(environment.Lease.Fence, intent.EgressReceipt.Fence);
            Assert.Equal(registration.Binding.ContextRevision, intent.EgressReceipt.ContextRevision);
            Assert.True(intent.EgressReceipt.PolicyGeneration > 0);
            var childPath = $"/api/projects/{project.ProjectId}/runs/{RunId}" +
                $"/coordination/sessions/{intent.ChildSessionId}";
            using var childStatusResponse = await SendAsync(
                client, HttpMethod.Get, childPath + "/status", runToken, [TenantId]);
            await AssertStatusAsync(childStatusResponse, HttpStatusCode.OK);
            var childStatus = await ReadJsonAsync<SessionStatusSnapshot>(childStatusResponse);
            using var childTurn = await SendJsonAsync(client, HttpMethod.Post,
                childPath + "/turn-boundary", runToken,
                new TurnBoundaryRequest(childStatus.ExecutionFence, childStatus.StateVersion));
            await AssertStatusAsync(childTurn, HttpStatusCode.OK);
            using var childOwner = await SendAsync(client, HttpMethod.Get,
                $"/internal/projects/{project.ProjectId}/runs/{RunId}" +
                $"/coordination/sessions/{intent.ChildSessionId}/runtime-owner-context",
                runToken, [TenantId]);
            await AssertStatusAsync(childOwner, HttpStatusCode.OK);
        }
    }
}
