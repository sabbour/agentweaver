extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using EventsHost::Agentweaver.EventsAndSessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
    [Theory]
    [InlineData("completed")]
    [InlineData("lost-acknowledgment")]
    [InlineData("failed")]
    [InlineData("revoked-authority")]
    [InlineData("foreign-binding")]
    [InlineData("stale-decision")]
    public async Task PublicBuildTestDispatchRetainsItsIntentAndRejectsChangedAuthority(string scenario)
    {
        var sourceControlSecrets = new RecordingSecretRedemption();
        await RestartBrokerForSourceControlAsync(sourceControlSecrets);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password, X509KeyStorageFlags.EphemeralKeySet);
        var signingKey = new X509SecurityKey(certificate);
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal, SandboxCapabilities.BuildTestCommandPod);
        var registration = new ProviderRegistration(
            new ProviderDescriptor(ProviderSeam.Sandbox, "sandbox-platform", new Version(1, 0, 0), 1,
                ProviderHostingPattern.KubernetesController, capabilities),
            true, "options-v1", 1);
        var sourceControlCapabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead, SourceControlCapabilities.RepositoryCheckout);
        var sourceControlRegistration = new ProviderRegistration(
            GitHubSourceControlAdapter.CreateDescriptor(), true, "options-v1",
            GitHubSourceControlAdapter.CurrentOptionsSchemaVersion);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration, sourceControlRegistration],
            [
                new ProviderSelection(ProviderSeam.Sandbox, "sandbox-platform"),
                new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)
            ], []).Value);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, signingKey, providerCatalog: catalog);

        var adminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "public-build-test-admin", null, null, ["platform_admin"]);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, adminMembership.MembershipId,
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        using var createProject = await SendJsonAsync(projects.Client, HttpMethod.Post, "/api/projects/",
            adminToken, new CreateProjectRequest { Name = "Public BuildTest dispatch" });
        await AssertStatusAsync(createProject, HttpStatusCode.Created);
        var project = await ReadJsonAsync<ProjectSummary>(createProject);
        using var copilotConnection = new ControlledCopilotConnection();
        var linkedConnection = await LinkRuntimeCopilotConnectionAsync(projects, adminToken, copilotConnection);

        var bootstrapToken = await IssueTokenAsync(
            "projects.bootstrap", [TenantId], "public-build-test-runner", null, null, ["orchestrator"]);
        var runnerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(bootstrapToken).Claims, "sub");
        var runnerMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, runnerSubject, TenantId);
        var runnerRole = await AssignRoleAsync(projects.PrivilegedFixtureDataSource, runnerMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Orchestrator);
        await CreateRunBindingGrantAsync(runnerSubject, project.ProjectId, RunId);
        var runToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator", [TenantId], "public-build-test-runner",
            project.ProjectId, RunId, ["orchestrator"]);
        Assert.Equal(runnerSubject, SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(runToken).Claims, "sub"));
        await CreateSourceControlSecretGrantAsync(runnerSubject, project.ProjectId, RunId,
            new SecretRef("github-api", "api-v1"), SourceControlSecretPurposes.Api);
        await CreateSourceControlSecretGrantAsync(runnerSubject, project.ProjectId, RunId,
            new SecretRef("github-checkout", "checkout-v1"), SourceControlSecretPurposes.Checkout);

        using var updateProject = await SendJsonAsync(
            projects.Client, HttpMethod.Put, $"/api/projects/{project.ProjectId}/configuration", adminToken,
            new UpdateProjectConfigurationRequest
            {
                ExpectedRevision = project.ConfigurationRevision,
                Configuration = new ProjectConfiguration
                {
                    AgentCharters =
                    [
                        new ProjectAgentCharter("test-agent", "Test agent", "implementer", "Build the approved change.")
                    ],
                    Casting = [new ProjectAgentCast("test-agent", "implementer", 0)],
                    SourceControl = new SourceControlProjectSettings(
                        new SourceControlRepositoryIdentity("octo", "agentweaver"),
                        new SecretRef("github-api", "api-v1"),
                        checkoutSecretReference: new SecretRef("github-checkout", "checkout-v1"))
                }
            });
        await AssertStatusAsync(updateProject, HttpStatusCode.OK);
        var projectConfiguration = await ReadJsonAsync<VersionedProjectConfiguration>(updateProject);
        using var updateDefaults = await SendJsonAsync(
            projects.Client, HttpMethod.Put, "/api/platform/runtime-defaults/", adminToken,
            new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings(
                        "platform-model", SourceMode: ModelSourceMode.HostedCopilot,
                        ConnectionId: linkedConnection.ConnectionId),
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 2, MaxToolCalls = 2, MaxChildren = 2, MaxConcurrentChildren = 1,
                        MaxWallTimeSeconds = 3600, MaxPromptTokens = 2000
                    }
                }
            });
        await AssertStatusAsync(updateDefaults, HttpStatusCode.OK);
        using var acceptSelection = await SendJsonAsync(
            projects.Client, HttpMethod.Put, $"/api/projects/{project.ProjectId}/runs/{RunId}/selection", runToken,
            new AcceptRunSelectionRequest
            {
                ExpectedProjectConfigRevision = projectConfiguration.Revision,
                ExpectedPlatformRuntimeRevision = 1,
                Context = new RunSelectionContext
                {
                    Revision = "public-build-test-catalog-v1",
                    AvailableModelSelectionReferences = ImmutableHashSet.Create(StringComparer.Ordinal, "platform-model"),
                    ProviderRequirements =
                    [
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.Sandbox, RequiredAdapterVersion = "1.0.0",
                            RequiredOptionsSchemaVersion = 1, RequiredCapabilities = capabilities
                        },
                        new ProviderRequirement
                        {
                            Seam = ProviderSeam.SourceControl, RequiredAdapterVersion = "1.0.0",
                            RequiredOptionsSchemaVersion = GitHubSourceControlAdapter.CurrentOptionsSchemaVersion,
                            RequiredCapabilities = sourceControlCapabilities
                        }
                    ]
                }
            });
        await AssertStatusAsync(acceptSelection, HttpStatusCode.OK);
        var acceptedSelection = await acceptSelection.Content.ReadAsStringAsync();

        var ownerSchema = "public_build_test_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "public_build_events_" + Guid.NewGuid().ToString("N");
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
            ["Identity__Audience"] = "https://api.test",
            ["Identity__SecretRedemption__Audience"] = "https://broker-redemption.test",
            ["Identity__SecretRedemption__OwnerBaseAddress"] = "https://broker.test.local/"
        });
        await using (var migrations = NpgsqlDataSource.Create(_connectionString))
        {
            await CoordinationOwnerMigrator.MigrateAsync(migrations, ownerSchema);
            await EventsAndSessionsMigrator.MigrateAsync(migrations, eventsSchema);
        }

        var command = new WorkflowBuildTestCommand(
            "profile-1", "/usr/bin/dotnet", ["test", "a b", "$(literal)", "--"], ".",
            [new("results", "artifacts/results.xml", true, 1024)]);
        var artifactBytes = "xml"u8.ToArray();
        using var workspaceFiles = new SourceControlTemporaryDirectory();
        var originPath = Path.Combine(workspaceFiles.Path, "checkout-origin");
        var baseSha = await CreateGitRepositoryAsync(originPath);
        await RunFixtureGitAsync(originPath, ["rm", "--", "README.md"]);
        await RunFixtureGitAsync(originPath, ["commit", "-m", "Use an empty BuildTest fixture baseline"]);
        baseSha = (await RunFixtureGitAsync(originPath, ["rev-parse", "--verify", "HEAD^{commit}"])).Trim();
        Assert.Equal(40, baseSha.Length);
        Assert.Empty((await RunFixtureGitAsync(originPath, ["ls-tree", "--full-tree", "-r", "--name-only", baseSha])).Trim());
        var gitWorkspaceManager = new GitWorkspaceManager(
            Path.Combine(workspaceFiles.Path, "owned-workspaces"), new LocalGitRepositoryRemote(new Uri(originPath)));
        var sourceControlGitHub = new ControlledGitHubApi(sourceControlSecrets.Value);
        var captureObjects = new InMemoryObjectStore();
        const string workspaceId = "public-build-test";
        const string workspaceBranch = "agent/public-build-test";
        var sourceControlPath = $"/api/projects/{project.ProjectId}/runs/{RunId}/source-control/sessions/root";
        string? workspacePath = null;
        var executionOptions = new SandboxBuildTestAcceptedExecutionOptions(
            "profile-1", $"build-test@sha256:{new string('a', 64)}", "linux/amd64",
            ["/usr/bin/dotnet"], "1000m", "1Gi", "1Gi", 120, 4096, 1024, "offline",
            $"collector@sha256:{new string('b', 64)}", "linux/amd64",
            SandboxBuildTestLimits.OutputCollectorExecutable, [SandboxBuildTestLimits.OutputCollectorAssembly],
            SandboxBuildTestLimits.OutputCollectorMode, SandboxBuildTestLimits.OutputCollectorContainerName);
        var sandboxProvider = new ControlledSandboxResourceProvider();
        using var optionsDocument = JsonDocument.Parse("{}");
        var expectedBinding = new SandboxBuildTestExpectedBinding(
            new(new(TenantId, project.ProjectId, RunId, "environment-1"), 1), Guid.NewGuid(),
            new(ProviderSeam.Sandbox, sandboxProvider.ProviderId, sandboxProvider.ResourceId, 7), 7,
            new(project.ProjectId, "volume-1", 1), 2, 3)
        {
            SandboxProviderBinding = new(sandboxProvider.ProviderId, "1.0.0", 1, "options-v1",
                optionsDocument.RootElement.Clone(), optionsDocument.RootElement.Clone())
        };
        var preparation = new SandboxBuildTestBindingPreparation(
            "root", command.ExecutionProfileReference, "options-v1",
            SandboxBuildTestBindingPreparation.ComputeExecutionOptionsSha256(executionOptions),
            expectedBinding, executionOptions);
        var prepareCount = 0;
        var startCount = 0;
        var reconcileCount = 0;
        var outputCaptureCount = 0;
        SandboxBuildTestAcceptedCommand? acceptedCommand = null;
        SandboxBuildTestOperationSnapshot? storedOperation = null;
        HttpClient? ownerClient = null;

        HttpResponseMessage OwnerResponse(HttpRequestMessage request, HttpStatusCode status, object? payload) =>
            new(status)
            {
                RequestMessage = request,
                Content = JsonContent.Create(payload, options: CoordinationJsonOptions),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            };

        async Task<HttpResponseMessage> RespondToEnvironmentAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("environment.test", request.RequestUri?.Host);
            Assert.Equal(runToken, request.Headers.Authorization?.Parameter);
            Assert.Equal(TenantId, Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/binding-preparation", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("?sessionId=root&executionProfileReference=profile-1", request.RequestUri.Query);
                prepareCount++;
                if (scenario == "revoked-authority")
                    await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, runnerRole.AssignmentId, runnerRole.Revision);
                var selected = scenario == "foreign-binding"
                    ? preparation with
                    {
                        ExpectedBinding = expectedBinding with
                        {
                            SandboxResource = expectedBinding.SandboxResource with { ResourceId = "foreign-resource" }
                        }
                    }
                    : preparation;
                return OwnerResponse(request, HttpStatusCode.OK, selected);
            }
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("/commands/", path);
                if (storedOperation is not null)
                    Assert.EndsWith(storedOperation.OperationId.ToString("D"), path);
                return OwnerResponse(request, storedOperation is null ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    storedOperation is null ? null : new SandboxBuildTestOperationResult(storedOperation, true));
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            var apiRequest = await request.Content!.ReadFromJsonAsync<SandboxBuildTestApiRequest>(
                CoordinationJsonOptions, cancellationToken);
            Assert.NotNull(apiRequest);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(expectedBinding, CoordinationJsonOptions),
                JsonSerializer.SerializeToElement(apiRequest.ExpectedBinding, CoordinationJsonOptions)));
            var reference = apiRequest.Checkpoint;
            var currentOwnerClient = ownerClient
                ?? throw new InvalidOperationException("The public owner client is not initialized.");
            using var readCommand = await SendAsync(
                currentOwnerClient,
                HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root/build-test/commands/" +
                $"{reference.CheckpointId}/{reference.StepId}?workPlanId={reference.WorkPlanId}" +
                $"&checkpointRevision={reference.CheckpointRevision}&decisionStateVersion={reference.DecisionStateVersion}" +
                $"&executionFence={reference.ExecutionFence}&acceptedSelectionHash={reference.AcceptedSelectionHash}",
                runToken, [TenantId]);
            await AssertStatusAsync(readCommand, HttpStatusCode.OK);
            var currentCommand = await ReadJsonAsync<SandboxBuildTestAcceptedCommand>(readCommand);
            Assert.Equal(command.Arguments.ToArray(), currentCommand.Arguments.ToArray());
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(command.Outputs, CoordinationJsonOptions),
                JsonSerializer.SerializeToElement(currentCommand.Outputs, CoordinationJsonOptions)));
            Assert.Equal(reference, currentCommand.Checkpoint);
            if (path.EndsWith("/reconcile", StringComparison.Ordinal))
            {
                reconcileCount++;
                Assert.NotNull(acceptedCommand);
                Assert.Equal(acceptedCommand.OperationId, currentCommand.OperationId);
                Assert.Equal(acceptedCommand.ImmutableHash, currentCommand.ImmutableHash);
            }
            else
            {
                Assert.EndsWith("/commands", path);
                startCount++;
                Assert.Null(acceptedCommand);
                acceptedCommand = currentCommand;
            }
            storedOperation = CreatePublicBuildTestOperation(currentCommand, apiRequest, scenario == "failed", artifactBytes);
            if (scenario == "lost-acknowledgment" && reconcileCount == 0)
            {
                storedOperation = storedOperation with
                {
                    Status = SandboxBuildTestOperationStatus.Running,
                    Terminal = null, CollectorPod = null, CollectorTerminal = null,
                    CollectorManifestSha256 = null, OutputEvidence = []
                };
                throw new HttpRequestException("The controlled owner accepted the command but lost its acknowledgment.");
            }
            if (storedOperation.Status == SandboxBuildTestOperationStatus.Completed)
            {
                Assert.Equal(0, outputCaptureCount);
                var preparedWorkspace = workspacePath
                    ?? throw new InvalidOperationException("The public SourceControl workspace is not prepared.");
                var artifactPath = Path.Combine(preparedWorkspace, "artifacts", "results.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
                await File.WriteAllBytesAsync(artifactPath, artifactBytes, cancellationToken);
                var materializedBytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
                Assert.Equal(artifactBytes, materializedBytes);
                storedOperation = CreatePublicBuildTestOperation(currentCommand, apiRequest, false, materializedBytes);
                using var captureResponse = await SendJsonAsync(
                    currentOwnerClient, HttpMethod.Post, sourceControlPath + $"/workspaces/{workspaceId}/output-captures",
                    runToken, new PrepareSourceControlWorkspaceRevisionRequest(baseSha, workspaceBranch));
                await AssertStatusAsync(captureResponse, HttpStatusCode.Created);
                var capture = await ReadJsonAsync<SourceControlOutputCaptureSummaryView>(captureResponse);
                Assert.NotEqual(Guid.Empty, capture.EventId);
                Assert.True(capture.EventPosition > 0);
                Assert.Equal(workspaceId, capture.WorkspaceId);
                Assert.Equal(baseSha, capture.BaseSha);
                Assert.Equal(workspaceBranch, capture.BranchName);
                using var detailResponse = await SendAsync(currentOwnerClient, HttpMethod.Get,
                    sourceControlPath + $"/output-captures/{capture.CaptureId}", runToken, [TenantId]);
                await AssertStatusAsync(detailResponse, HttpStatusCode.OK);
                var detail = await ReadJsonAsync<SourceControlOutputCaptureDetailView>(detailResponse);
                Assert.Equal(capture, detail.Capture);
                Assert.Equal(RunId, detail.Manifest.RunId);
                var collected = Assert.Single(storedOperation.OutputEvidence);
                var captured = Assert.Single(detail.Manifest.Files, file => file.Path == collected.RelativePath);
                Assert.Equal(collected.CapturedBytes, captured.ByteLength);
                Assert.Equal(collected.CapturedSha256, captured.Sha256);
                using var fileResponse = await SendAsync(currentOwnerClient, HttpMethod.Get,
                    sourceControlPath + $"/output-captures/{capture.CaptureId}/files?path={collected.RelativePath}",
                    runToken, [TenantId]);
                await AssertStatusAsync(fileResponse, HttpStatusCode.OK);
                var capturedBytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                Assert.Equal(materializedBytes, capturedBytes);
                Assert.Equal(collected.CapturedSha256, Convert.ToHexStringLower(SHA256.HashData(capturedBytes)));
                outputCaptureCount++;
            }
            return OwnerResponse(request, HttpStatusCode.OK, new SandboxBuildTestOperationResult(storedOperation, false));
        }

        WebApplicationFactory<OrchestratorHost::Program>? ownerFactory = null;
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => (ownerFactory ?? throw new InvalidOperationException("The public owner is not initialized."))
                .Server.CreateHandler(), sessionMaterialObjects: captureObjects);
        await using var baseFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => eventsFactory.Server.CreateHandler(), sandboxProvider: sandboxProvider,
            brokerHandler: () => _brokerFactory.Server.CreateHandler(),
            sourceControlHandler: sourceControlGitHub.CreateHandler, providerCatalog: catalog,
            gitWorkspaceManager: gitWorkspaceManager);
        await using var configuredFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RuntimeRegistrationOwnerOptions>();
                services.AddSingleton(new RuntimeRegistrationOwnerOptions(new Uri("https://environment.test/")));
                services.RemoveAll<MafBuildTestEnvironmentClient>();
                services.AddHttpClient<MafBuildTestEnvironmentClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new PublicBuildTestOwnerHandler(RespondToEnvironmentAsync));
            }));
        ownerFactory = configuredFactory;
        using var orchestrator = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test")
        });
        ownerClient = orchestrator;
        var rootPath = $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/sessions/root";
        using var acceptRoot = await SendJsonAsync(orchestrator, HttpMethod.Post,
            $"/api/projects/{project.ProjectId}/runs/{RunId}/coordination/root", runToken, new AcceptRootRequest("root"));
        await AssertStatusAsync(acceptRoot, HttpStatusCode.Created);

        long stateVersion = 1;
        async Task ApplyDecisionAsync(string path, object body, string? gate)
        {
            using var response = await SendJsonAsync(orchestrator, HttpMethod.Post, rootPath + path, runToken, body);
            await AssertStatusAsync(response, HttpStatusCode.OK);
            var result = await ReadJsonAsync<CoordinatorDecisionOperationResponse>(response);
            Assert.True(result.Accepted);
            Assert.Empty(result.Issues);
            Assert.Equal(stateVersion + 1, result.StateVersion);
            Assert.Equal(gate, result.PendingGate?.RequestId);
            stateVersion = result.StateVersion;
        }
        await ApplyDecisionAsync("/actions/propose_outcome_spec",
            new ProposeCoordinatorOutcomeRequest(stateVersion, "outcome-1", "outcome-gate",
                new CoordinatorOutcomeSpecification("outcome", "Build the approved change", "Required results exist",
                    "Use the accepted command", "Keep the accepted provider", [])), "outcome-gate");
        await ApplyDecisionAsync("/decisions/gates/outcome-gate/answer",
            new AnswerCoordinatorGateRequest(stateVersion, "outcome-answer", "approve", null), null);
        var workflow = new WorkflowDefinition("public-build-test", "1.0.0", "public-build-test-catalog-v1",
            WorkflowDefinitionOrigin.Generated, 1,
            [
                new WorkflowStepDefinition("build-test", "Run the accepted command and collect required results.",
                    WorkflowStepMode.Platform, 0, new WorkflowCardinality(0, 1), [], [], [], [], [],
                    null, WorkflowPlatformGate.BuildTest) { BuildTestCommand = command }
            ]);
        Assert.Empty(WorkflowDefinitionValidator.ValidateAndSnapshot(workflow).Issues);
        await ApplyDecisionAsync("/actions/select_workflow",
            new SelectCoordinatorWorkflowRequest(stateVersion, "workflow-1", "workflow-gate", workflow.Id, workflow),
            "workflow-gate");
        await ApplyDecisionAsync("/decisions/gates/workflow-gate/answer",
            new AnswerCoordinatorGateRequest(stateVersion, "workflow-answer", "approve", null), null);
        await ApplyDecisionAsync("/actions/propose_work_plan",
            new ProposeCoordinatorWorkPlanRequest(stateVersion, "plan-1", "plan-gate",
                new WorkPlan("plan-1", workflow.Id, workflow.Revision, workflow.CatalogVersion, [])), "plan-gate");
        await ApplyDecisionAsync("/decisions/gates/plan-gate/answer",
            new AnswerCoordinatorGateRequest(stateVersion, "plan-answer", "approve", null), null);
        Assert.Equal(1, sandboxProvider.NegotiationCount);
        using (var pinResponse = await SendAsync(
                   orchestrator, HttpMethod.Post, sourceControlPath + "/pin", runToken, [TenantId]))
            await AssertStatusAsync(pinResponse, HttpStatusCode.Accepted);
        using (var workspaceResponse = await SendJsonAsync(orchestrator, HttpMethod.Post,
                   sourceControlPath + "/workspaces", runToken,
                   new PrepareSourceControlWorkspaceRequest(workspaceId, baseSha, workspaceBranch)))
        {
            await AssertStatusAsync(workspaceResponse, HttpStatusCode.Accepted);
            var workspace = await ReadJsonAsync<SourceControlWorkspaceView>(workspaceResponse);
            Assert.Equal(workspaceId, workspace.WorkspaceId);
            Assert.Equal(baseSha, workspace.BaseSha);
            Assert.Equal(workspaceBranch, workspace.BranchName);
            workspacePath = workspace.WorkspacePath;
            Assert.StartsWith(Path.Combine(workspaceFiles.Path, "owned-workspaces") + Path.DirectorySeparatorChar,
                workspacePath, StringComparison.Ordinal);
            Assert.Equal(new Uri(originPath).AbsoluteUri,
                (await RunFixtureGitAsync(workspacePath, ["remote", "get-url", "origin"])).Trim());
        }

        var dispatchRequest = new MafExecutionDispatchRequest(
            scenario == "stale-decision" ? stateVersion - 1 : stateVersion, "plan-1")
        {
            BuildTestEnvironmentId = "environment-1"
        };
        using var firstDispatch = await SendJsonAsync(
            orchestrator, HttpMethod.Post, rootPath + "/actions/dispatch", runToken, dispatchRequest);
        if (scenario is "revoked-authority" or "foreign-binding" or "stale-decision")
        {
            await AssertStatusAsync(firstDispatch, scenario == "revoked-authority"
                ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict);
            Assert.Equal(scenario == "stale-decision" ? 0 : 1, prepareCount);
            Assert.Equal(0, startCount);
            Assert.Equal(0, reconcileCount);
        }
        else
        {
            if (scenario == "lost-acknowledgment")
            {
                await AssertStatusAsync(firstDispatch, HttpStatusCode.BadGateway);
                Assert.Equal(0, outputCaptureCount);
                using var retry = await SendJsonAsync(
                    orchestrator, HttpMethod.Post, rootPath + "/actions/dispatch", runToken, dispatchRequest);
                await AssertStatusAsync(retry, HttpStatusCode.OK);
                Assert.True((await ReadJsonAsync<MafExecutionDispatchResponse>(retry)).IsComplete);
            }
            else
            {
                await AssertStatusAsync(firstDispatch, HttpStatusCode.OK);
                var dispatched = await ReadJsonAsync<MafExecutionDispatchResponse>(firstDispatch);
                Assert.Equal(scenario == "completed", dispatched.IsComplete);
            }
            Assert.NotNull(acceptedCommand);
            Assert.NotNull(storedOperation);
            Assert.Equal(acceptedCommand.OperationId, storedOperation.OperationId);
            Assert.Equal(1, prepareCount);
            Assert.Equal(1, startCount);
            Assert.Equal(scenario == "lost-acknowledgment" ? 1 : 0, reconcileCount);
            using var replay = await SendJsonAsync(
                orchestrator, HttpMethod.Post, rootPath + "/actions/dispatch", runToken, dispatchRequest);
            await AssertStatusAsync(replay, HttpStatusCode.OK);
            Assert.Equal(1, prepareCount);
            Assert.Equal(1, startCount);
            Assert.Equal(scenario == "lost-acknowledgment" ? 1 : 0, reconcileCount);
            var binding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
                new(project.ProjectId, RunId, "root"),
                new CoordinationActor(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri, runnerSubject),
                acceptedCommand.Checkpoint.ExecutionFence, MafExecutionCheckpointStore.CurrentSdkVersion, "platform-model");
            var checkpoint = await new MafExecutionCheckpointStore(
                configuredFactory.Services.GetRequiredService<PostgresMafCheckpointStore>(), binding)
                .ReadLatestAsync(CancellationToken.None);
            Assert.NotNull(checkpoint);
            var retained = checkpoint.State.BuildTestIntents["build-test"];
            Assert.Equal(acceptedCommand.OperationId, retained.OperationId);
            Assert.Equal(acceptedCommand.Checkpoint, retained.CheckpointReference());
            Assert.Equal(acceptedCommand.ImmutableHash, retained.ToAcceptedCommand().ImmutableHash);
            Assert.Equal(scenario == "failed" ? MafExecutionTaskStatus.Failed : MafExecutionTaskStatus.Succeeded,
                checkpoint.State.Progress.NonModelSteps["build-test"]);
            Assert.Equal(acceptedCommand.OperationId, checkpoint.State.BuildTestReceipts["build-test"].OperationId);
        }
        Assert.Equal(scenario is "completed" or "lost-acknowledgment" ? 1 : 0, outputCaptureCount);
        if (scenario != "revoked-authority")
        {
            using var selectionReplay = await SendAsync(projects.Client, HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{RunId}/selection", runToken, [TenantId]);
            await AssertStatusAsync(selectionReplay, HttpStatusCode.OK);
            Assert.Equal(acceptedSelection, await selectionReplay.Content.ReadAsStringAsync());
        }
    }

    private static SandboxBuildTestOperationSnapshot CreatePublicBuildTestOperation(
        SandboxBuildTestAcceptedCommand accepted,
        SandboxBuildTestApiRequest request,
        bool failed,
        byte[] artifactBytes)
    {
        var expected = request.ExpectedBinding;
        var fingerprint = request.ComputeRequestFingerprint(accepted);
        ImmutableDictionary<string, string> Labels(string role) =>
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(SandboxBuildTestLimits.PolicyOperationLabel, accepted.OperationId.ToString("N"))
                .Add(SandboxBuildTestLimits.PolicyRoleLabel, role);
        var commandPolicy = new SandboxBuildTestCommandNetworkPolicyBinding(
            "command-policy", new string('a', 64), expected.NetworkPolicyGeneration, Labels("command"));
        var collectorPolicy = new SandboxBuildTestOutputCollectorNetworkPolicyBinding(
            "collector-policy", new string('b', 64), expected.NetworkPolicyGeneration, Labels("collector"));
        var pod = new SandboxBuildTestPodReference("namespace", "command", "command-uid", "1");
        var collector = new SandboxBuildTestPodReference("namespace", "collector", "collector-uid", "2");
        var started = DateTimeOffset.UtcNow;
        var terminal = new SandboxBuildTestTerminalEvidence(pod.Uid, "build-test",
            SandboxBuildTestTerminationKind.Exited, failed ? 1 : 0, started, started.AddSeconds(1), null);
        var collectorTerminal = new SandboxBuildTestTerminalEvidence(collector.Uid,
            SandboxBuildTestLimits.OutputCollectorContainerName, SandboxBuildTestTerminationKind.Exited,
            0, started.AddSeconds(1), started.AddSeconds(2), null);
        var bytes = Encoding.UTF8.GetBytes("Controlled compiler output; required files and terminal evidence decide the result.");
        var output = new SandboxBuildTestOutputCapture(pod.Uid, terminal.ContainerName, bytes.ToImmutableArray(),
            bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), false, bytes.Length);
        var obligation = accepted.Outputs.Single();
        var evidence = new SandboxBuildTestOutputEvidence(collector.Uid,
            SandboxBuildTestLimits.OutputCollectorContainerName, obligation.Name, obligation.RelativePath,
            true, obligation.MaximumBytes, true, artifactBytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(artifactBytes)));
        var receipt = new SandboxBuildTestOutputCollectorReceipt(1, accepted.OperationId, accepted.ImmutableHash,
            fingerprint, accepted.Checkpoint, collector.Uid, SandboxBuildTestLimits.OutputCollectorContainerName,
            [evidence], "");
        return new(accepted.OperationId, accepted.ImmutableHash, fingerprint,
            failed ? SandboxBuildTestOperationStatus.Failed : SandboxBuildTestOperationStatus.Completed,
            expected, commandPolicy,
            new(expected.Fence, expected.SandboxLeaseOperationId, expected.SandboxResource,
                expected.ProviderFencingGeneration, expected.WorkspaceVolume, expected.DataGeneration, expected.NetworkPolicyGeneration,
                commandPolicy),
            pod, terminal, output, collector, collectorTerminal, collectorPolicy,
            SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt), [evidence],
            failed ? "command_failed" : null, started, started.AddSeconds(3));
    }

    private sealed class PublicBuildTestOwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
