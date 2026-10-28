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
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using GitHub.Copilot;
using EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        => await RunPublicConfirmedPlanRevalidatesPrePlanRootRegistrationBeforeEgressAsync(scenario);

    [LinuxOnlyFact]
    public async Task PublicNativeChildrenJoinBeforeIndependentReviewGate()
        => await RunPublicConfirmedPlanRevalidatesPrePlanRootRegistrationBeforeEgressAsync("native");

    private async Task RunPublicConfirmedPlanRevalidatesPrePlanRootRegistrationBeforeEgressAsync(string scenario)
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
                        // Decision persistence counts the two planned items and their registered children together.
                        MaxModelTurns = 2, MaxToolCalls = 2, MaxChildren = 4, MaxConcurrentChildren = 2,
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
        using var nativeEnvironment = scenario == "native"
            ? new TemporaryEnvironment(new Dictionary<string, string?>
            {
                ["Orchestrator__RuntimeUsage__BrokerOwnerAddress"] = "https://broker.test/",
                ["EventsAndSessions__RuntimeUsage__Enabled"] = "true",
                ["EventsAndSessions__SessionMaterial__ContainerUri"] =
                    "https://owned.blob.core.windows.net/session-material",
                ["AgentHost__ModelBindingsRevision"] = "native-model-bindings-v1",
                ["AgentHost__ModelBindings__platform-model__ModelId"] = "gpt-6.1-sol",
                ["AgentHost__ModelBindings__platform-model__SourceMode"] = "HostedCopilot",
                ["EventsAndSessions__Cost__Copilot__ResourceId"] = "native-copilot-cost",
                ["EventsAndSessions__Cost__Copilot__ResourceGeneration"] = "1",
                ["EventsAndSessions__Cost__Copilot__OptionsRevision"] =
                    PublishedCopilotRateCardFixture.OptionsRevision,
                ["EventsAndSessions__Cost__Copilot__OptionsSchemaVersion"] = "2",
                ["EventsAndSessions__Cost__Copilot__RateCard__Json"] =
                    PublishedCopilotRateCardFixture.RateCardJson
            })
            : null;
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, ownerSchema);
        await EventsAndSessionsMigrator.MigrateAsync(dataSource, eventsSchema);
        var routes = new ConcurrentDictionary<string, Func<HttpMessageHandler>>(StringComparer.Ordinal);
        routes["projects.test"] = projects.CreateHandler;
        var failures = new ConcurrentQueue<string>();
        await using var environment = await RuntimePlacementTestServer.StartAsync(
            _connectionString, signingKey, projects.CreateHandler,
            new(TenantId, project.ProjectId, RunId, "root-environment"),
            Assert.Single(Assert.Single(snapshot.Providers, p => p.Seam == ProviderSeam.Sandbox).Candidates),
            selectionDocument.RootElement.Clone(), () => new RuntimeServiceRouter(routes, failures));
        var environments = new ConcurrentDictionary<string, RuntimePlacementTestServer>(StringComparer.Ordinal);
        environments["root-environment"] = environment;
        var runtimeEnvironments = new ConcurrentDictionary<Guid, string>();
        if (scenario == "native")
            Assert.True(routes.TryAdd("environment.test",
                () => new NativeJoinEnvironmentRouter(environments, runtimeEnvironments)));
        else
            Assert.True(routes.TryAdd("environment.test", environment.CreateHandler));
        var sandbox = new ControlledSandboxResourceProvider(AgentSandboxProviderMetadata.ProviderId);
        await using var baseEvents = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => new RuntimeServiceRouter(routes, failures),
            nativeUsageHandler: scenario == "native"
                ? () => new RuntimeServiceRouter(routes, failures)
                : null,
            sessionMaterialObjects: scenario == "native" ? new RuntimeMaterialObjects() : null,
            simulateDirectTls: scenario == "native");
        await using var events = baseEvents.WithWebHostBuilder(builder =>
        {
            if (scenario == "native")
                builder.ConfigureTestServices(services => services.RemoveAll<ILoggerProvider>());
        });
        Assert.True(routes.TryAdd("events.test", () => events.Server.CreateHandler()));
        await using var baseFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => events.Server.CreateHandler(), sandboxProvider: sandbox, providerCatalog: catalog,
            policyOptions: policy,
            environmentHandler: () => new RuntimeServiceRouter(routes, failures),
            runtimeUsageHandler: () => new RuntimeServiceRouter(routes, failures));
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            if (scenario == "native")
                builder.ConfigureTestServices(services => services.RemoveAll<ILoggerProvider>());
            builder.ConfigureTestServices(services =>
            {
                ConfigureTestHttpClient(services, "runtime-suspend-environment",
                    () => new RuntimeServiceRouter(routes, failures));
                ConfigureTestHttpClient(services, nameof(MafBuildTestEnvironmentClient),
                    () => new RuntimeServiceRouter(routes, failures));
                ConfigureTestHttpClient(services, "maf-execution-host",
                    () => new RuntimeServiceRouter(routes, failures));
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test/"), AllowAutoRedirect = false
        });
        Assert.True(routes.TryAdd("orchestrator.test", () => factory.Server.CreateHandler()));
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
        if (scenario != "native")
            return;
        Assert.False(OperatingSystem.IsWindows(), "Real provider and Host mount checks require Linux.");
        await VerifyNativeChildrenJoinAsync(
            project.ProjectId, runToken, version, plan, checkpoint,
            snapshot, selectionDocument.RootElement.Clone(), signingKey, projects, connection, eventsSchema,
            routes, failures, environments, runtimeEnvironments, factory, client, binding, dataSource, ownerSchema);
    }

    private async Task VerifyNativeChildrenJoinAsync(
        string projectId, string runToken, long version, WorkPlan plan,
        MafExecutionCheckpointSnapshot initial, ProjectRunSelectionResponse selection, JsonElement selectionJson,
        SecurityKey signingKey, ProjectsConfigResourceServer projects, ControlledCopilotConnection connection,
        string eventsSchema, ConcurrentDictionary<string, Func<HttpMessageHandler>> routes,
        ConcurrentQueue<string> failures,
        ConcurrentDictionary<string, RuntimePlacementTestServer> environments,
        ConcurrentDictionary<Guid, string> runtimeEnvironments,
        WebApplicationFactory<OrchestratorHost::Program> orchestrator, HttpClient client,
        MafCheckpointBinding binding, NpgsqlDataSource dataSource, string ownerSchema)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var proofElapsed = System.Diagnostics.Stopwatch.StartNew();
        await using var brokerFactory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: settings =>
            {
                for (var i = 0; i < ProjectScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[i];
                settings["IdentityBroker__RuntimeBootstrap__OrchestratorOwnerAddress"] = "https://orchestrator.test/";
                settings["IdentityBroker__RuntimeBootstrap__EnvironmentOwnerAddress"] = "https://environment.test/";
                settings["IdentityBroker__RuntimeBootstrap__BootstrapLifetime"] = "00:01:00";
                settings["IdentityBroker__RuntimeBootstrap__SourceLifetime"] = "00:02:00";
                ConfigureRunBindingSettings(settings);
                connection.ConfigureSettings(settings);
            },
            configureServices: services =>
            {
                services.AddSingleton<TimeProvider>(TimeProvider.System);
                services.RemoveAll<ILoggerProvider>();
                ConfigureRunBindingTlsIdentity(services);
                connection.ConfigureServices(services, projects.CreateHandler);
                ConfigureTestHttpClient(services, nameof(RuntimeRegistrationHttpClient),
                    () => new RuntimeServiceRouter(routes, failures));
                ConfigureTestHttpClient(services, nameof(BrokerRuntimeBootstrapDeliveryClient),
                    () => new RuntimeServiceRouter(routes, failures));
            });
        using var broker = brokerFactory.CreateClient(new()
        {
            BaseAddress = new Uri("https://broker.test/"), AllowAutoRedirect = false
        });
        TraceNativeStage(failures, $"Native Broker started; elapsed={proofElapsed.Elapsed}.");
        Assert.True(routes.TryAdd("broker.test", () => brokerFactory.Server.CreateHandler()));
        var hosts = new List<RuntimeAgentHost>();
        var servers = new List<IHost>();
        var sdks = new List<ControlledCopilotRuntime>();
        var httpClients = new List<HttpClient>();
        var releases = new List<TaskCompletionSource>();
        var childEnvironments = new List<RuntimePlacementTestServer>();
        var sdksByAssociation = new Dictionary<string, ControlledCopilotRuntime>(StringComparer.Ordinal);
        var runtimeInstances = new HashSet<Guid>();
        var registrations = new List<RuntimeRegistration>();
        var sourceGrantReceipts = new ConcurrentDictionary<Guid, RuntimeGrantReceipt>();
        var refreshedSourceGrantRuntimeIds = new HashSet<Guid>();
        var configureHosts = new List<Task>();
        var dispatchTasks = new List<Task<HttpResponseMessage>>();
        var secondHostCatalogRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHostCatalogRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointStore = new MafExecutionCheckpointStore(
            orchestrator.Services.GetRequiredService<PostgresMafCheckpointStore>(), binding);
        var rootPath = $"/api/projects/{projectId}/runs/{RunId}/coordination/sessions/root";
        async Task<(long Revision, RuntimeCredentialState State, DateTimeOffset ExpiresAt,
            DateTimeOffset RecordedAt)?> ReadSourceGrantHeadAsync(
                Guid grantId, CancellationToken cancellationToken)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT h.current_revision, r.state, r.expires_at, r.recorded_at
                FROM identity_broker.runtime_grant_heads h
                JOIN identity_broker.runtime_grant_revisions r
                  ON r.grant_id = h.grant_id AND r.revision = h.current_revision
                WHERE h.grant_id = @grant_id
                """, connection);
            command.Parameters.AddWithValue("grant_id", grantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            return (reader.GetInt64(0), (RuntimeCredentialState)reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        var primaryFailure = await Record.ExceptionAsync(async () =>
        {
            async Task<HttpResponseMessage> SendPublicDispatchAsync(long expectedStateVersion)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, rootPath + "/actions/dispatch")
                {
                    Content = JsonContent.Create(
                        new MafExecutionDispatchRequest(expectedStateVersion, plan.Id),
                        options: CoordinationJsonOptions)
                };
                AddBearerAndTenant(request, runToken, TenantId);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                TraceNativeStage(failures, $"Public dispatch begin; elapsed={proofElapsed.Elapsed}.");
                var response = await client.SendAsync(request, token);
                TraceNativeStage(failures,
                    $"Public dispatch returned {(int)response.StatusCode}; elapsed={elapsed.Elapsed}.");
                return response;
            }

            Task<HttpResponseMessage> StartPublicDispatch(long? expectedStateVersion = null)
            {
                var task = SendPublicDispatchAsync(expectedStateVersion ?? version);
                dispatchTasks.Add(task);
                return task;
            }

            void TraceSourceGrantMetadata(
                string stage,
                RuntimeRegistration registration,
                RuntimeGrantReceipt receipt,
                (long Revision, RuntimeCredentialState State, DateTimeOffset ExpiresAt,
                    DateTimeOffset RecordedAt)? stored,
                DateTimeOffset observedAt,
                bool storedWasRead = true)
            {
                var storedText = stored is { } current
                    ? $"storedRevision={current.Revision}, storedState={current.State}, " +
                      $"storedRecordedAt={current.RecordedAt:O}, storedExpiresAt={current.ExpiresAt:O}"
                    : storedWasRead ? "storedHead=missing" : "storedHead=not-read";
                TraceNativeStage(failures,
                    $"{stage}: runtime={receipt.RuntimeInstanceId:D}, grant={receipt.GrantId:D}, " +
                    $"state={receipt.State}, revision={receipt.Revision}, recordedAt={receipt.RecordedAt:O}, " +
                    $"expiresAt={receipt.ExpiresAt:O}, registrationExpiresAt={registration.ExpiresAt:O}, " +
                    $"observedAt={observedAt:O}, {storedText}.");
            }

            async Task RefreshHostSourceGrantAsync(int index)
            {
                var registration = registrations[index];
                var runtimeInstanceId = registration.RuntimeInstanceId;
                if (refreshedSourceGrantRuntimeIds.Contains(runtimeInstanceId))
                    return;
                var receipt = sourceGrantReceipts[runtimeInstanceId];
                var observedAt = DateTimeOffset.UtcNow;
                var stored = await ReadSourceGrantHeadAsync(receipt.GrantId, token);
                TraceSourceGrantMetadata("Before Host source refresh", registration, receipt, stored, observedAt);
                Assert.NotNull(stored);
                Assert.Equal(receipt.Revision, stored.Value.Revision);
                Assert.Equal(receipt.State, stored.Value.State);
                Assert.Equal(receipt.ExpiresAt, stored.Value.ExpiresAt);
                Assert.Equal(receipt.RecordedAt, stored.Value.RecordedAt);
                Assert.Equal(RuntimeCredentialState.Active, receipt.State);
                Assert.True(receipt.ExpiresAt > observedAt,
                    "The existing source grant must still be live when the Host refresh begins.");

                var bearer = new SecretCredential(runToken, registration.ExpiresAt);
                try
                {
                    var proof = new RuntimeHostSessionProof(
                        1, registration, receipt.GrantId, receipt.Revision, RuntimeCredentialPurpose.Observe);
                    var operationId = Guid.NewGuid();
                    var refreshStartedAt = DateTimeOffset.UtcNow;
                    var refreshed = await hosts[index].RefreshAsync(
                        new RuntimeHostRefreshRequest(proof, operationId),
                        new RuntimeActorAuthorization(bearer, TenantId), token);
                    var refreshCompletedAt = DateTimeOffset.UtcNow;
                    Assert.Equal(runtimeInstanceId, refreshed.RuntimeInstanceId);
                    Assert.Equal(operationId, refreshed.OperationId);
                    Assert.Equal(receipt.Revision, refreshed.PreviousRevision);
                    Assert.Equal(receipt.GrantId, refreshed.SourceGrant.GrantId);
                    Assert.Equal(receipt.Revision + 1, refreshed.SourceGrant.Revision);
                    Assert.Equal(RuntimeCredentialState.Active, refreshed.SourceGrant.State);
                    Assert.InRange(refreshed.SourceGrant.ExpiresAt,
                        refreshStartedAt.AddMinutes(2), refreshCompletedAt.AddMinutes(2));
                    Assert.True(refreshed.SourceGrant.ExpiresAt <= registration.ExpiresAt);
                    Assert.True(refreshed.EffectiveSessionExpiresAt <= registration.ExpiresAt);
                    sourceGrantReceipts[runtimeInstanceId] = refreshed.SourceGrant;
                    var refreshedStored = await ReadSourceGrantHeadAsync(refreshed.SourceGrant.GrantId, token);
                    TraceSourceGrantMetadata(
                        "After Host source refresh", registration, refreshed.SourceGrant,
                        refreshedStored, DateTimeOffset.UtcNow);
                    Assert.NotNull(refreshedStored);
                    Assert.Equal(refreshed.SourceGrant.Revision, refreshedStored.Value.Revision);
                    Assert.Equal(refreshed.SourceGrant.State, refreshedStored.Value.State);
                    Assert.Equal(refreshed.SourceGrant.ExpiresAt, refreshedStored.Value.ExpiresAt);
                    Assert.Equal(refreshed.SourceGrant.RecordedAt, refreshedStored.Value.RecordedAt);
                    refreshedSourceGrantRuntimeIds.Add(runtimeInstanceId);
                    TraceNativeStage(failures,
                        $"Host {runtimeInstanceId:D} source refresh completed in " +
                        $"{refreshCompletedAt - refreshStartedAt}; effectiveSessionExpiresAt=" +
                        $"{refreshed.EffectiveSessionExpiresAt:O}.");
                }
                finally
                {
                    bearer.Invalidate();
                }
            }

            async Task TraceDispatchFailureAsync(HttpResponseMessage response)
            {
                if (response.IsSuccessStatusCode)
                    return;
                foreach (var registration in registrations)
                {
                    if (!sourceGrantReceipts.TryGetValue(registration.RuntimeInstanceId, out var receipt))
                        continue;
                    var stored = await ReadSourceGrantHeadAsync(receipt.GrantId, token);
                    TraceSourceGrantMetadata(
                        $"After dispatch {(int)response.StatusCode}", registration, receipt, stored,
                        DateTimeOffset.UtcNow);
                }
                await using var diagnosticConnection = await dataSource.OpenConnectionAsync(token);
                await using var diagnostic = new NpgsqlCommand($"""
                    SELECT r.runtime_instance_id, r.revision, r.state, r.expires_at
                    FROM "{ownerSchema}".runtime_registration_heads h
                    JOIN "{ownerSchema}".runtime_registration_revisions r
                      ON r.runtime_instance_id = h.runtime_instance_id AND r.revision = h.current_revision
                    WHERE r.runtime_instance_id = ANY(@runtimes)
                    """, diagnosticConnection);
                diagnostic.Parameters.AddWithValue("runtimes", runtimeInstances.ToArray());
                await using var rows = await diagnostic.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token))
                    TraceNativeStage(failures,
                        $"After dispatch {(int)response.StatusCode}: runtime={rows.GetGuid(0):D}, " +
                        $"revision={rows.GetInt64(1)}, state={(RuntimeRegistrationState)rows.GetInt32(2)}, " +
                        $"expiresAt={rows.GetFieldValue<DateTimeOffset>(3):O}, observedAt={DateTimeOffset.UtcNow:O}.");
                var diagnosticStore = new RuntimeRegistrationStore(dataSource, ownerSchema, TimeProvider.System);
                foreach (var runtimeId in runtimeInstances)
                {
                    var revalidationCalled = false;
                    var readFailure = await Record.ExceptionAsync(async () =>
                        await diagnosticStore.ReadAsync(runtimeId, token, (_, _) =>
                        {
                            revalidationCalled = true;
                            return Task.CompletedTask;
                        }));
                    TraceNativeStage(failures,
                        $"Locked store read runtime={runtimeId:D}, revalidationCalled={revalidationCalled}: {readFailure}");
                }
            }

            async Task AssertDispatchSucceededAsync(HttpResponseMessage response)
            {
                await TraceDispatchFailureAsync(response);
                await AssertStatusAsync(response, HttpStatusCode.OK, string.Join('\n', failures));
            }

            async Task ConfigureHostAsync(
                string environmentId,
                RuntimeRegistration registration,
                RuntimeAgentHost host,
                System.Diagnostics.Stopwatch hostElapsed,
                DateTimeOffset leaseCreatedAt)
            {
                var configureElapsed = System.Diagnostics.Stopwatch.StartNew();
                TraceNativeStage(failures,
                    $"Host {environmentId} RuntimeAgentHost.ConfigureAsync begin; " +
                    $"hostElapsed={hostElapsed.Elapsed}, leaseAge={DateTimeOffset.UtcNow - leaseCreatedAt}, " +
                    $"registrationExpiresAt={registration.ExpiresAt:O}.");
                var bearer = new SecretCredential(runToken, registration.ExpiresAt);
                try
                {
                    var readiness = await host.ConfigureAsync(
                        new(1, registration, Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(new { })),
                        new(bearer, TenantId), token);
                    Assert.Contains(readiness.StartupPhases, phase => phase.Phase == SandboxStartupPhase.Ready);
                    sourceGrantReceipts[registration.RuntimeInstanceId] = readiness.SourceGrant;
                    TraceNativeStage(failures,
                        $"Host {environmentId} RuntimeAgentHost.ConfigureAsync reached Ready; " +
                        $"configureElapsed={configureElapsed.Elapsed}, hostElapsed={hostElapsed.Elapsed}.");
                    TraceSourceGrantMetadata(
                        $"Host {environmentId} reached Ready", registration, readiness.SourceGrant,
                        null, DateTimeOffset.UtcNow, storedWasRead: false);
                }
                catch (RuntimeStartupException error)
                {
                    var budgetFailure = error.Failure;
                    var budgetDetails = budgetFailure is null
                        ? "no startup budget evidence"
                        : $"phase={budgetFailure.Phase}({(int)budgetFailure.Phase}), " +
                          $"effectivePhaseBudgetSeconds={budgetFailure.BudgetSeconds}, " +
                          $"deadline={budgetFailure.Deadline:O}, observedAt={budgetFailure.ObservedAt:O}";
                    TraceNativeStage(failures,
                        $"Host {environmentId} RuntimeAgentHost.ConfigureAsync failed after " +
                        $"{configureElapsed.Elapsed}: {error.Code}; {budgetDetails}.");
                    throw new InvalidOperationException(
                        $"{error.Code}: {JsonSerializer.Serialize(error.Failure)}", error);
                }
                finally
                {
                    TraceNativeStage(failures,
                        $"Host {environmentId} RuntimeAgentHost.ConfigureAsync ended; " +
                        $"configureElapsed={configureElapsed.Elapsed}, hostElapsed={hostElapsed.Elapsed}.");
                    bearer.Invalidate();
                }
            }

            Assert.Equal(2, initial.State.PendingDispatches.Count);
            var orderedIntents = initial.State.PendingDispatches.Values
                .OrderBy(x => x.AssociationId, StringComparer.Ordinal).ToArray();
            var workspaceMounts = new[]
            {
                "/workspace/agentweaver/native-join-workspace-1",
                "/workspace/agentweaver/native-join-workspace-2"
            };
            var mountIndex = 0;
            foreach (var intent in orderedIntents)
            {
                var mount = workspaceMounts[mountIndex++];
                Assert.True(Directory.Exists(mount), $"Expected an actual Linux workspace mount at {mount}.");
                var endpoint = $"https://native-{intent.AssociationId}.test:8443/runtime/v1/configure";
                var endpointHost = new Uri(endpoint).Host;
                var environmentId = $"native-{intent.AssociationId}";
                var hostElapsed = System.Diagnostics.Stopwatch.StartNew();
                var phaseElapsed = System.Diagnostics.Stopwatch.StartNew();
                var bootstrapReceived = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                TraceNativeStage(failures,
                    $"Host {environmentId} Sandbox provisioning begin; proofElapsed={proofElapsed.Elapsed}.");
                var childEnvironment = await RuntimePlacementTestServer.StartAsync(
                    _connectionString, signingKey, projects.CreateHandler,
                    new(TenantId, projectId, RunId, environmentId),
                    Assert.Single(Assert.Single(selection.Providers, p => p.Seam == ProviderSeam.Sandbox).Candidates),
                    selectionJson, () => new RuntimeServiceRouter(routes, failures,
                        afterResponse: (request, response, _) =>
                        {
                            if (request.RequestUri is { } requestUri &&
                                requestUri.Host == endpointHost &&
                                requestUri.AbsolutePath == "/runtime/v1/configure" &&
                                response.IsSuccessStatusCode)
                            {
                                TraceNativeStage(failures,
                                    $"Host {environmentId} bootstrap receiver accepted delivery.");
                                bootstrapReceived.TrySetResult();
                            }
                            return Task.CompletedTask;
                        }),
                        endpoint, mount, DateTimeOffset.UtcNow.AddSeconds(-2),
                        startupBudgets: new AgentSandboxStartupBudgets(30, 30, 30, 60, 30, 120));
                        var leaseCreatedAt = childEnvironment.Lease.CreatedAt
                            ?? throw new InvalidOperationException("The native Sandbox lease has no creation timestamp.");
                        TraceNativeStage(failures,
                            $"Host {environmentId} Sandbox provisioned; phaseElapsed={phaseElapsed.Elapsed}, " +
                    $"hostElapsed={hostElapsed.Elapsed}, leaseCreatedAt={childEnvironment.Lease.CreatedAt:O}, " +
                    $"leaseExpiresAt={childEnvironment.Lease.LeaseExpiresAt:O}.");
                Assert.True(environments.TryAdd(environmentId, childEnvironment));
                childEnvironments.Add(childEnvironment);
                phaseElapsed.Restart();
                TraceNativeStage(failures, $"Host {environmentId} registration begin; hostElapsed={hostElapsed.Elapsed}.");
                using var registered = await SendJsonAsync(client, HttpMethod.Post,
                    $"/internal/projects/{projectId}/runs/{RunId}/coordination/sessions/{intent.ChildSessionId}/runtime-registrations",
                    runToken, new RegisterRuntimeRequest(environmentId, "runtime-profile"));
                await AssertStatusAsync(registered, HttpStatusCode.OK, string.Join('\n', failures));
                var registration = await ReadJsonAsync<RuntimeRegistration>(registered);
                Assert.Equal(childEnvironment.Lease.LeaseExpiresAt, registration.ExpiresAt);
                TraceNativeStage(failures,
                    $"Host {environmentId} registration completed; phaseElapsed={phaseElapsed.Elapsed}, " +
                    $"hostElapsed={hostElapsed.Elapsed}, runtime={registration.RuntimeInstanceId:D}, state={registration.State}, " +
                    $"revision={registration.Revision}, expiresAt={registration.ExpiresAt:O}, " +
                    $"leaseExpiresAt={childEnvironment.Lease.LeaseExpiresAt:O}.");
                runtimeInstances.Add(registration.RuntimeInstanceId);
                registrations.Add(registration);
                Assert.True(runtimeEnvironments.TryAdd(registration.RuntimeInstanceId, environmentId));
                var sdk = new ControlledCopilotRuntime(TimeSpan.FromMinutes(5))
                {
                    EmitUsageAfterCreate = false,
                    EmitUsageWithTurn = true,
                    EmitNativeCompletionReceipt = true,
                    PersistNativeSessionState = true,
                    ExpectedAvailableToolsCount = 7,
                    SdkCredential = connection.AccessToken,
                    ModelId = "gpt-6.1-sol",
                    ExpectedPrompt = plan.Items.Single(x => x.Id == intent.AssociationId).Task,
                    AssistantResponse = $"Completed {intent.AssociationId} with genuine native accounting."
                };
                if (mount == workspaceMounts[1])
                    sdk.BeforeModelCatalogResponse = async cancellationToken =>
                    {
                        secondHostCatalogRequested.TrySetResult();
                        await secondHostCatalogRelease.Task.WaitAsync(cancellationToken);
                    };
                sdk.RequestObserved = method => TraceNativeStage(failures,
                    $"Host {environmentId} SDK method {method} observed; hostElapsed={hostElapsed.Elapsed}.");
                sdk.RequestCompleted = (method, elapsed) => TraceNativeStage(failures,
                    $"Host {environmentId} SDK method {method} response completed; sdkElapsed={elapsed}.");
                sdks.Add(sdk);
                sdksByAssociation.Add(intent.AssociationId, sdk);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Add(release);
                sdk.BeforeTurnResponse = ct => release.Task.WaitAsync(ct);
                var http = new HttpClient(new RuntimeServiceRouter(routes, failures,
                    afterResponse: async (request, response, cancellationToken) =>
                    {
                        if (request.RequestUri is not
                            { Host: "broker.test", AbsolutePath: "/internal/runtime/source/verify" } ||
                            !sourceGrantReceipts.TryGetValue(registration.RuntimeInstanceId, out var receipt))
                            return;
                        var observedAt = DateTimeOffset.UtcNow;
                        var stored = response.IsSuccessStatusCode
                            ? null
                            : await ReadSourceGrantHeadAsync(receipt.GrantId, cancellationToken);
                        TraceSourceGrantMetadata(
                            $"Host {environmentId} source verify HTTP {(int)response.StatusCode}",
                            registration, receipt, stored, observedAt,
                            storedWasRead: !response.IsSuccessStatusCode);
                    }));
                httpClients.Add(http);
                var sessions = new RuntimeCopilotSessionFactory(sdk.Connection,
                    Path.Combine(mount, "native-state"),
                    new RuntimeModelBindingsResolver("native-model-bindings-v1",
                        new Dictionary<string, RuntimeModelBinding>
                        {
                            ["platform-model"] = new(sdk.ModelId, ModelSourceMode.HostedCopilot)
                        }), workingDirectory: mount);
                var host = new RuntimeAgentHost(
                    new(registration.Binding.ConfigureEndpoint, new("https://broker.test/"),
                        new("https://orchestrator.test/"), new("https://environment.test/"),
                        new("https://events.test/"), new("https://projects.test/"), registration.Binding.Image!, 2),
                    http, new RuntimeRegistrationHttpClient(http, new("https://orchestrator.test/")),
                    sessions, TimeProvider.System);
                hosts.Add(host);
                phaseElapsed.Restart();
                TraceNativeStage(failures, $"Host {environmentId} TestServer startup begin; hostElapsed={hostElapsed.Elapsed}.");
                var server = await new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        AddJwtBearer(services, signingKey);
                        services.AddAuthorization();
                        services.ConfigureHttpJsonOptions(options =>
                            options.SerializerOptions.Converters.Add(
                                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                            endpoints.MapRuntimeAgentHost(host, TimeProvider.System));
                    });
                }).StartAsync(token);
                servers.Add(server);
                TraceNativeStage(failures,
                    $"Host {environmentId} TestServer started; phaseElapsed={phaseElapsed.Elapsed}, " +
                    $"hostElapsed={hostElapsed.Elapsed}.");
                Assert.True(routes.TryAdd(registration.Binding.ConfigureEndpoint.Host,
                    () => server.GetTestServer().CreateHandler()));
                phaseElapsed.Restart();
                TraceNativeStage(failures,
                    $"Host {environmentId} bootstrap delivery begin; hostElapsed={hostElapsed.Elapsed}.");
                var deliveryTask = SendJsonAsync(broker, HttpMethod.Post,
                    "/internal/runtime/bootstrap/request", runToken,
                    new RuntimeBootstrapRequest(registration.RuntimeInstanceId, Guid.NewGuid(),
                        RuntimeContractValidation.Hash("{}"u8)));
                await Task.WhenAny(bootstrapReceived.Task, deliveryTask);
                var configureStarted = bootstrapReceived.Task.IsCompletedSuccessfully;
                if (configureStarted)
                    configureHosts.Add(ConfigureHostAsync(
                        environmentId, registration, host, hostElapsed, leaseCreatedAt));
                using (var delivery = await deliveryTask)
                    await AssertStatusAsync(delivery, HttpStatusCode.OK, string.Join('\n', failures));
                Assert.True(bootstrapReceived.Task.IsCompletedSuccessfully,
                    $"Host {environmentId} did not accept its bootstrap delivery.");
                TraceNativeStage(failures,
                    $"Host {environmentId} bootstrap delivered; phaseElapsed={phaseElapsed.Elapsed}, " +
                    $"hostElapsed={hostElapsed.Elapsed}, registrationExpiresAt={registration.ExpiresAt:O}.");
                if (!configureStarted)
                    configureHosts.Add(ConfigureHostAsync(
                        environmentId, registration, host, hostElapsed, leaseCreatedAt));
            }
            Assert.Equal(2, hosts.Count);
            Assert.Equal(2, runtimeInstances.Count);
            Assert.Equal(2, configureHosts.Count);
            var firstIntent = orderedIntents[0];
            var secondIntent = orderedIntents[1];
            var firstStepId = plan.Items.Single(item => item.Id == firstIntent.AssociationId).WorkflowStepId;
            var secondStepId = plan.Items.Single(item => item.Id == secondIntent.AssociationId).WorkflowStepId;

            await configureHosts[0];
            await secondHostCatalogRequested.Task.WaitAsync(token);
            await RefreshHostSourceGrantAsync(0);
            TraceNativeStage(failures,
                $"Starting public dispatch with the first Host Ready and the second ConfigureAsync in flight; " +
                $"first={firstIntent.AssociationId}, second={secondIntent.AssociationId}.");

            var dispatchTask = StartPublicDispatch();
            var firstTurnReceived = sdks[0].TurnReceived.Task;
            var firstDispatchReturnedBeforeTurn =
                await Task.WhenAny(dispatchTask, firstTurnReceived) == dispatchTask;
            if (firstDispatchReturnedBeforeTurn)
            {
                using var earlyDispatch = await dispatchTask.WaitAsync(token);
                await AssertDispatchSucceededAsync(earlyDispatch);
                var earlyResponse = await ReadJsonAsync<MafExecutionDispatchResponse>(earlyDispatch);
                Assert.Contains(firstStepId, earlyResponse.UnavailableExecutorStepIds);
                Assert.Empty(earlyResponse.CompletedResults);
                TraceNativeStage(failures,
                    $"Public dispatch deferred first step {firstStepId} through the existing unavailable response.");
                secondHostCatalogRelease.TrySetResult();
                await Task.WhenAll(configureHosts);
                await RefreshHostSourceGrantAsync(1);
                dispatchTask = StartPublicDispatch();
                TraceNativeStage(failures,
                    $"Public dispatch retry began after both Hosts reached Ready; elapsed={proofElapsed.Elapsed}.");
                if (await Task.WhenAny(dispatchTask, firstTurnReceived) == dispatchTask)
                {
                    using var retryResponse = await dispatchTask.WaitAsync(token);
                    await AssertDispatchSucceededAsync(retryResponse);
                    var retryResult = await ReadJsonAsync<MafExecutionDispatchResponse>(retryResponse);
                    Assert.Contains(firstStepId, retryResult.UnavailableExecutorStepIds);
                    Assert.Fail($"The public retry still reported first step {firstStepId} unavailable.");
                }
            }

            await firstTurnReceived.WaitAsync(token);
            secondHostCatalogRelease.TrySetResult();
            await Task.WhenAll(configureHosts);
            Assert.All(configureHosts, task => Assert.True(task.IsCompletedSuccessfully));
            await RefreshHostSourceGrantAsync(1);
            if (firstDispatchReturnedBeforeTurn)
                await Task.WhenAll(sdks.Select(sdk => sdk.TurnReceived.Task)).WaitAsync(token);
            TraceNativeStage(failures,
                $"Both genuine Hosts reached Ready; elapsed={proofElapsed.Elapsed}, " +
                $"firstTurnReceived={sdks[0].TurnReceived.Task.IsCompletedSuccessfully}, " +
                $"secondTurnReceived={sdks[1].TurnReceived.Task.IsCompletedSuccessfully}.");
            var held = await checkpointStore.ReadLatestAsync(token);
            Assert.NotNull(held);
            Assert.Empty(held.State.Results);
            Assert.Empty(held.State.ReviewIntents);
            Assert.Equal(2, held.State.PendingDispatches.Count);

            releases[0].TrySetResult();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            long accounted = 0;
            await using var accountingWaitConnection = await dataSource.OpenConnectionAsync(token);
            while (wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                await using var count = new NpgsqlCommand($"""
                    SELECT count(*) FROM "{eventsSchema}".usage_ledger ledger
                    JOIN "{eventsSchema}".usage_source_receipts receipts USING (event_id)
                    WHERE receipts.source_receipt_json::jsonb -> 'usage' ->> 'sdkEventId' = @event
                    """, accountingWaitConnection);
                count.Parameters.AddWithValue("event", sdks[0].NativeUsageEventId.ToString("D"));
                accounted = (long)(await count.ExecuteScalarAsync(token))!;
                if (accounted == 1)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
            Assert.Equal(1L, accounted);
            var secondDeliveryOrDispatchCompletion = await Task.WhenAny(
                sdks[1].TurnReceived.Task, dispatchTask);
            if (secondDeliveryOrDispatchCompletion == dispatchTask)
            {
                using var firstDispatch = await dispatchTask.WaitAsync(token);
                await AssertDispatchSucceededAsync(firstDispatch);
                var firstResponse = await ReadJsonAsync<MafExecutionDispatchResponse>(firstDispatch);
                Assert.Contains(secondStepId, firstResponse.UnavailableExecutorStepIds);
                Assert.Single(firstResponse.CompletedResults);
                Assert.Contains(firstIntent.AssociationId, firstResponse.CompletedResults.Keys);
                var partiallyJoined = await checkpointStore.ReadLatestAsync(token);
                Assert.NotNull(partiallyJoined);
                Assert.Single(partiallyJoined.State.Results);
                Assert.Single(partiallyJoined.State.PendingDispatches);
                Assert.Empty(partiallyJoined.State.ReviewIntents);
                Assert.Contains(sdks[0].AssistantResponse,
                    partiallyJoined.State.Results[firstIntent.AssociationId]);
                TraceNativeStage(failures,
                    $"Public dispatch left second step {secondStepId} pending after its not-ready response; " +
                    $"one result joined, elapsed={proofElapsed.Elapsed}.");
                dispatchTask = StartPublicDispatch();
                TraceNativeStage(failures,
                    $"Public dispatch retry began for pending second step {secondStepId}; " +
                    $"elapsed={proofElapsed.Elapsed}.");
                if (await Task.WhenAny(dispatchTask, sdks[1].TurnReceived.Task) == dispatchTask)
                {
                    using var retryResponse = await dispatchTask.WaitAsync(token);
                    await AssertDispatchSucceededAsync(retryResponse);
                    var retryResult = await ReadJsonAsync<MafExecutionDispatchResponse>(retryResponse);
                    Assert.Contains(secondStepId, retryResult.UnavailableExecutorStepIds);
                    Assert.Fail($"The public retry still reported second step {secondStepId} unavailable.");
                }
                await sdks[1].TurnReceived.Task.WaitAsync(token);
                var pendingRetry = await checkpointStore.ReadLatestAsync(token);
                Assert.NotNull(pendingRetry);
                Assert.Single(pendingRetry.State.Results);
                Assert.Single(pendingRetry.State.PendingDispatches);
                Assert.Empty(pendingRetry.State.ReviewIntents);
            }
            else
            {
                await sdks[1].TurnReceived.Task.WaitAsync(token);
                var partiallyAccounted = await checkpointStore.ReadLatestAsync(token);
                Assert.NotNull(partiallyAccounted);
                Assert.Empty(partiallyAccounted.State.Results);
                Assert.Equal(2, partiallyAccounted.State.PendingDispatches.Count);
                Assert.Empty(partiallyAccounted.State.ReviewIntents);
            }

            releases[1].TrySetResult();
            using var dispatched = await dispatchTask.WaitAsync(token);
            await AssertDispatchSucceededAsync(dispatched);
            var dispatchReceipt = await ReadJsonAsync<MafExecutionDispatchResponse>(dispatched);
            var joined = await checkpointStore.ReadLatestAsync(token);
            Assert.NotNull(joined);
            Assert.Equal(2, joined.State.Results.Count);
            Assert.Empty(joined.State.PendingDispatches);
            var gateIntent = Assert.Single(joined.State.ReviewIntents);
            TraceNativeStage(failures,
                $"Native results joined and the selected review gate is pending; " +
                $"results={joined.State.Results.Count}, gates={joined.State.ReviewIntents.Count}, " +
                $"elapsed={proofElapsed.Elapsed}.");
            foreach (var (associationId, sdk) in sdksByAssociation)
            {
                Assert.Contains(sdk.AssistantResponse, joined.State.Results[associationId]);
                Assert.Single(sdk.Requests, request => request.Method == "session.create");
                Assert.Single(sdk.Requests, request => request.Method == "session.send");
                Assert.NotEqual(Guid.Empty, sdk.NativeUsageEventId);
                Assert.NotEqual(Guid.Empty, sdk.NativeCompletionReceiptEventId);
            }

            using var pendingDecisionResponse = await SendAsync(
                client, HttpMethod.Get, rootPath + "/decisions", runToken, [TenantId]);
            await AssertStatusAsync(pendingDecisionResponse, HttpStatusCode.OK);
            var pendingDecision = await ReadJsonAsync<CoordinatorDecisionStateView>(pendingDecisionResponse);
            Assert.Equal(dispatchReceipt.DecisionStateVersion, pendingDecision.StateVersion);
            Assert.False(pendingDecision.CanDispatch);
            Assert.NotNull(pendingDecision.PendingGate);
            Assert.Equal(gateIntent.Value.ApprovalRequestId, pendingDecision.PendingGate?.RequestId);

            using var retry = await StartPublicDispatch(pendingDecision.StateVersion).WaitAsync(token);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Contains("maf_execution_dispatch_stale", await retry.Content.ReadAsStringAsync());
            using var retryDecisionResponse = await SendAsync(
                client, HttpMethod.Get, rootPath + "/decisions", runToken, [TenantId]);
            await AssertStatusAsync(retryDecisionResponse, HttpStatusCode.OK);
            var retryDecision = await ReadJsonAsync<CoordinatorDecisionStateView>(retryDecisionResponse);
            Assert.Equal(pendingDecision.StateVersion, retryDecision.StateVersion);
            Assert.False(retryDecision.CanDispatch);
            Assert.Equal(gateIntent.Value.ApprovalRequestId, retryDecision.PendingGate?.RequestId);

            var retriedCheckpoint = await checkpointStore.ReadLatestAsync(token);
            Assert.NotNull(retriedCheckpoint);
            Assert.Equal(joined.State.Revision, retriedCheckpoint.State.Revision);
            Assert.Equal(joined.State.Results.Count, retriedCheckpoint.State.Results.Count);
            foreach (var (associationId, result) in joined.State.Results)
                Assert.Equal(result, retriedCheckpoint.State.Results[associationId]);
            Assert.Empty(retriedCheckpoint.State.PendingDispatches);
            Assert.Equal(gateIntent, Assert.Single(retriedCheckpoint.State.ReviewIntents));
            Assert.All(sdks, sdk => Assert.Single(sdk.Requests, request => request.Method == "session.send"));
            await using (var accountingConnection = await dataSource.OpenConnectionAsync(token))
            await using (var accounting = new NpgsqlCommand($"""
                SELECT ledger.price_disposition,
                       receipts.source_receipt_json::jsonb -> 'usage' ->> 'sdkEventId'
                FROM "{eventsSchema}".usage_ledger ledger
                JOIN "{eventsSchema}".usage_source_receipts receipts USING (event_id)
                ORDER BY ledger.event_id
                """, accountingConnection))
            await using (var reader = await accounting.ExecuteReaderAsync(token))
            {
                var persisted = new List<string>();
                while (await reader.ReadAsync(token))
                {
                    Assert.False(reader.IsDBNull(0));
                    persisted.Add(reader.GetString(1));
                }
                Assert.Equal(2, persisted.Count);
                Assert.Equal(2, persisted.Distinct(StringComparer.Ordinal).Count());
                Assert.All(sdksByAssociation.Values, sdk =>
                    Assert.Contains(sdk.NativeUsageEventId.ToString("D"), persisted));
            }

            TraceNativeStage(failures,
                $"Public retry was refused while the selected gate remained pending; " +
                $"joinedResults={retriedCheckpoint.State.Results.Count}, " +
                $"checkpointRevision={retriedCheckpoint.State.Revision}, elapsed={proofElapsed.Elapsed}.");

            using var gateAnswerResponse = await SendJsonAsync(
                client, HttpMethod.Post,
                $"{rootPath}/decisions/gates/{gateIntent.Value.ApprovalRequestId}/answer",
                runToken,
                new AnswerCoordinatorGateRequest(
                    retryDecision.StateVersion, "native-review-gate-answer", "approve", null));
            await AssertStatusAsync(gateAnswerResponse, HttpStatusCode.OK, string.Join('\n', failures));
            var gateAnswer = await ReadJsonAsync<CoordinatorDecisionOperationResponse>(gateAnswerResponse);
            Assert.True(gateAnswer.Accepted, await gateAnswerResponse.Content.ReadAsStringAsync());
            Assert.Empty(gateAnswer.Issues);

            using var settledDecisionResponse = await SendAsync(
                client, HttpMethod.Get, rootPath + "/decisions", runToken, [TenantId]);
            await AssertStatusAsync(settledDecisionResponse, HttpStatusCode.OK);
            var settledDecision = await ReadJsonAsync<CoordinatorDecisionStateView>(settledDecisionResponse);
            Assert.Equal(gateAnswer.StateVersion, settledDecision.StateVersion);
            Assert.Null(settledDecision.PendingGate);
            Assert.True(settledDecision.CanDispatch);
            TraceNativeStage(failures,
                $"Fixture-owned selected gate was approved through the public route; " +
                $"stateVersion={gateAnswer.StateVersion}, CanDispatch={settledDecision.CanDispatch}.");
        });
        var lifecycleFailures = new List<Exception>();
        if (primaryFailure is not null)
            lifecycleFailures.Add(primaryFailure);
        if ((configureHosts.Any(task => !task.IsCompleted) || dispatchTasks.Any(task => !task.IsCompleted)) &&
            Record.Exception(timeout.Cancel) is { } cancellationFailure)
            lifecycleFailures.Add(new InvalidOperationException(
                "Cancellation of in-flight native proof work failed.", cancellationFailure));
        foreach (var release in releases)
            release.TrySetResult();
        secondHostCatalogRelease.TrySetResult();
        foreach (var configureTask in configureHosts)
        {
            if (await Record.ExceptionAsync(async () => await configureTask) is { } configureFailure &&
                !ReferenceEquals(configureFailure, primaryFailure) &&
                !(configureFailure is OperationCanceledException && timeout.IsCancellationRequested))
                lifecycleFailures.Add(new InvalidOperationException(
                    "A Host configuration task failed while draining the native proof.", configureFailure));
        }
        foreach (var dispatchTask in dispatchTasks)
        {
            if (await Record.ExceptionAsync(async () =>
                {
                    using var response = await dispatchTask;
                }) is { } dispatchFailure && !ReferenceEquals(dispatchFailure, primaryFailure) &&
                !(dispatchFailure is OperationCanceledException && timeout.IsCancellationRequested))
                lifecycleFailures.Add(new InvalidOperationException(
                    "A public dispatch task failed while draining the native proof.", dispatchFailure));
        }
        async Task CleanUpAsync(string resource, Func<Task> cleanup)
        {
            if (await Record.ExceptionAsync(cleanup) is { } failure)
                lifecycleFailures.Add(new InvalidOperationException($"{resource} cleanup failed.", failure));
        }
        for (var index = 0; index < hosts.Count; index++)
        {
            var hostIndex = index;
            await CleanUpAsync($"RuntimeAgentHost {hostIndex}", async () =>
            {
                try
                {
                    await hosts[hostIndex].DisposeAsync();
                }
                catch (RuntimeAuthorizationException error) when (error.Code == "runtime_owner_denied")
                {
                    TraceNativeStage(failures,
                        $"Host {hostIndex} source revoke was denied after terminal owner context.");
                }
                var runtimeInstanceId = registrations[hostIndex].RuntimeInstanceId;
                if (!sourceGrantReceipts.TryGetValue(runtimeInstanceId, out var receipt))
                {
                    var configureCompletedSuccessfully = hostIndex < configureHosts.Count &&
                        configureHosts[hostIndex].IsCompletedSuccessfully;
                    Assert.False(configureCompletedSuccessfully,
                        $"Host {hostIndex} completed configuration without returning a source-grant receipt.");
                    TraceNativeStage(failures,
                        $"Host {hostIndex} had no source-grant receipt because configuration did not complete.");
                    return;
                }
                var revoked = await ReadSourceGrantHeadAsync(receipt.GrantId, CancellationToken.None);
                Assert.NotNull(revoked);
                Assert.Equal(RuntimeCredentialState.Revoked, revoked.Value.State);
                Assert.Equal(receipt.Revision + 1, revoked.Value.Revision);
                TraceNativeStage(failures,
                    $"Host {hostIndex} source grant {receipt.GrantId:D} is revoked at revision " +
                    $"{revoked.Value.Revision}.");
            });
        }
        foreach (var server in servers)
            if (Record.Exception(server.Dispose) is { } failure)
                lifecycleFailures.Add(new InvalidOperationException("TestServer cleanup failed.", failure));
        foreach (var sdk in sdks)
            await CleanUpAsync("Controlled SDK", () => sdk.DisposeAsync().AsTask());
        foreach (var http in httpClients)
            if (Record.Exception(http.Dispose) is { } failure)
                lifecycleFailures.Add(new InvalidOperationException("HTTP client cleanup failed.", failure));
        foreach (var childEnvironment in childEnvironments)
            await CleanUpAsync("Runtime placement environment",
                () => childEnvironment.DisposeAsync().AsTask());
        if (lifecycleFailures.Count > 0)
            Assert.Fail(new AggregateException("Native runtime proof and cleanup failed.", lifecycleFailures)
                + "\n" + string.Join('\n', failures));
    }

    private sealed class NativeJoinEnvironmentRouter(
        ConcurrentDictionary<string, RuntimePlacementTestServer> environments,
        ConcurrentDictionary<Guid, string> runtimeEnvironments) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var environmentId = environments.Keys.FirstOrDefault(id =>
                request.RequestUri!.AbsolutePath.Contains($"/environments/{id}/", StringComparison.Ordinal));
            if (environmentId is null && request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                if (body.RootElement.TryGetProperty("runtimeInstanceId", out var runtimeId))
                    runtimeEnvironments.TryGetValue(runtimeId.GetGuid(), out environmentId);
                else if (body.RootElement.TryGetProperty("fence", out var fence) &&
                    fence.TryGetProperty("owner", out var owner))
                    environmentId = owner.GetProperty("environmentId").GetString();
            }
            using var invoker = new HttpMessageInvoker(environments[environmentId ?? "root-environment"].CreateHandler());
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}
