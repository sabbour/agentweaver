extern alias EventsHost;
extern alias OrchestratorHost;
extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.AgentRuntime;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    /// <summary>Proves fresh interactive selection supports silent binding and first-message start through the Gateway.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedChatSilentlyBindsAndStartsWithProjectsOwnerProof(bool hasLegacyChild)
    {
        var signingKey = new X509SecurityKey(_runBindingSigningCertificate);
        // Owners use TestServer transport; the Broker independently queries the same store over pinned TLS.
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString,
            signingKey,
            providerCatalog: CreateSourceControlProviderCatalog(
                storageOptions: new AzureFilesCsiOptions(
                    AzureFilesCsiOptions.CurrentOptionsSchemaVersion,
                    "runtime-storage-options",
                    "agentweaver",
                    "azure-files",
                    1,
                    60,
                    100)));
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
        using var copilotConnection = new ControlledCopilotConnection();
        var linkedConnection = await LinkRuntimeCopilotConnectionAsync(
            projects, adminToken, copilotConnection);
        using (var defaultsRequest = new HttpRequestMessage(HttpMethod.Put, "/api/platform/runtime-defaults/")
        {
            Content = JsonContent.Create(new UpdatePlatformRuntimeDefaultsRequest
            {
                ExpectedRevision = 0,
                Defaults = new PlatformRuntimeDefaults
                {
                    ModelSelection = new ModelSelectionSettings(
                        "platform-model",
                        SourceMode: ModelSourceMode.HostedCopilot,
                        ConnectionId: linkedConnection.ConnectionId),
                    EgressBaseline = [],
                    RunLimits = new CopilotRunLimits
                    {
                        MaxModelTurns = 12,
                        MaxToolCalls = 100,
                        MaxChildren = 4,
                        MaxConcurrentChildren = 2,
                        MaxWallTimeSeconds = 3600,
                        MaxPromptTokens = 20_000,
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
        var orchestratorAssignment = await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, orchestratorMembership.MembershipId,
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
            ["Orchestrator__RuntimeRegistration__EnvironmentOwnerAddress"] = "https://environment.test/",
            ["Orchestrator__RuntimeUsage__BrokerOwnerAddress"] = "https://broker.test/",
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
        var runtimeRunStartOwners = new RuntimeRunStartOwnerProbe();
        var runtimeRoutes = new Dictionary<string, Func<HttpMessageHandler>>(StringComparer.Ordinal)
        {
            ["projects.test"] = projects.CreateHandler
        };
        var runtimeOwnerFailures = new ConcurrentQueue<string>();
        var environmentPlacementRequests = new ConcurrentQueue<string>();
        await using var runtimeEnvironmentHolder = new RuntimeRunStartEnvironmentHolder();
        var runtimeReceiverReady = new TaskCompletionSource<HttpMessageHandler>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtimeRoutes[RuntimeReceiverHost] = () => new RuntimeRunStartDeferredHandler(runtimeReceiverReady.Task);
        var statusSelectionBarrier = new ControlledRequestBarrier();
        var statusSelectionChange = new ProjectsSelectionResponseChange();
        var statusSelectionReads = 0;
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey,
            () => new ProjectsSelectionBarrierHandler(
                projects.CreateHandler(), statusSelectionBarrier, statusSelectionChange),
            () => eventsReference!.Server.CreateHandler(),
            environmentHandler: () => runtimeRunStartOwners.CreateHandler(
                () => new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures)),
            runtimeUsageHandler: () => new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures),
            loggerProvider: runtimeRunStartOwners.LoggerProvider,
            runtimeHostHandler: () => new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures));
        var sessionMaterialObjects = new RuntimeMaterialObjects();
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler(),
            sessionMaterialObjects: sessionMaterialObjects,
            simulateDirectTls: true);
        eventsReference = eventsFactory;
        runtimeRoutes["events.test"] = () => eventsFactory.Server.CreateHandler();
        runtimeRoutes["orchestrator.test"] = () => orchestratorFactory.Server.CreateHandler();
        runtimeRunStartOwners.SetProvisioner(async (startingOwner, startRequest, request, token) =>
        {
            startRequest.ValidateFor(startingOwner);
            var bearer = request.Headers.Authorization?.Parameter
                ?? throw new InvalidOperationException("The startup placement request has no bearer.");
            using var selectionResponse = await SendAsync(
                projects.Client,
                HttpMethod.Get,
                $"/api/projects/{startingOwner.ProjectId}/runs/{startingOwner.RunId}/selection",
                bearer,
                [startingOwner.TenantId]);
            Assert.Equal(HttpStatusCode.OK, selectionResponse.StatusCode);
            var selectionBytes = await selectionResponse.Content.ReadAsByteArrayAsync(token);
            var selection = JsonSerializer.Deserialize<ProjectRunSelectionResponse>(
                selectionBytes, CoordinationJsonOptions)
                ?? throw new InvalidOperationException("Projects returned an empty run selection.");
            var sandboxCandidate = Assert.Single(
                Assert.Single(selection.Providers, provider => provider.Seam == ProviderSeam.Sandbox).Candidates);
            using var selectionDocument = JsonDocument.Parse(selectionBytes);
            var environmentOwner = new EnvironmentOwnerIdentity(
                startingOwner.TenantId,
                startingOwner.ProjectId,
                startingOwner.RunId,
                RuntimeRunStartKeys.EnvironmentId(startingOwner));
            var environmentServer = await RuntimePlacementTestServer.StartAsync(
                _connectionString,
                signingKey,
                projects.CreateHandler,
                environmentOwner,
                sandboxCandidate,
                selectionDocument.RootElement.Clone(),
                () => new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures),
                leaseClockStart: DateTimeOffset.UtcNow,
                // This fixture creates the Host after provisioning and checks the owner binding first.
                startupBudgets: new AgentSandboxStartupBudgets(30, 30, 30, 60, 30, 120),
                mountPath: "/workspace",
                volumeId: RuntimeRunStartKeys.WorkspaceVolumeId(startingOwner),
                firstStartWorkspace: true,
                provisionFirstStartOnDemand: true);
            runtimeEnvironmentHolder.Set(environmentServer);
            runtimeRoutes["environment.test"] = environmentServer.CreateHandler;
            using var environmentClient = new HttpClient(
                new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures))
            {
                BaseAddress = new Uri("https://environment.test/")
            };
            using var placementRequest = new HttpRequestMessage(
                HttpMethod.Post, "/internal/runtime/run-start/placement")
            {
                Content = JsonContent.Create(
                    new RuntimeRunStartPlacementRequest(
                        1, startingOwner, null, startRequest.BootstrapOperationId),
                    options: OrchestratorJsonOptions)
            };
            AddBearerAndTenant(placementRequest, bearer, startingOwner.TenantId);
            using var placementResponse = await environmentClient.SendAsync(placementRequest, token);
            if (placementResponse.StatusCode != HttpStatusCode.OK)
                Assert.Fail(
                    $"Environment placement returned {(int)placementResponse.StatusCode}: " +
                    await placementResponse.Content.ReadAsStringAsync(token));
            environmentPlacementRequests.Enqueue("POST /internal/runtime/run-start/placement");
            return await ReadJsonAsync<RuntimeRunStartPlacementReceipt>(placementResponse);
        });

        await using var runtimeBrokerFactory = new IdentityBrokerWebApplicationFactory(
            _connectionString,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                for (var index = 0; index < ProjectScopes.Length; index++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{index + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[index];
                settings["IdentityBroker__RuntimeBootstrap__OrchestratorOwnerAddress"] = "https://orchestrator.test/";
                settings["IdentityBroker__RuntimeBootstrap__EnvironmentOwnerAddress"] = "https://environment.test/";
                settings["IdentityBroker__RuntimeBootstrap__BootstrapLifetime"] = "00:01:00";
                settings["IdentityBroker__RuntimeBootstrap__SourceLifetime"] = "00:02:00";
                ConfigureRunBindingSettings(settings);
                copilotConnection.ConfigureSettings(settings);
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                copilotConnection.ConfigureServices(services, projects.CreateHandler);
                services.AddHttpClient(nameof(RuntimeRegistrationHttpClient))
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures));
                services.AddHttpClient(nameof(BrokerRuntimeBootstrapDeliveryClient))
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures));
            });
        using var runtimeBroker = runtimeBrokerFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test/")
        });
        runtimeRoutes["broker.test"] = () => runtimeBrokerFactory.Server.CreateHandler();
        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test"),
        });
        using (var startupScope = orchestratorFactory.Services.CreateScope())
        {
            _ = startupScope.ServiceProvider.GetRequiredService<RuntimeRunStartContinuation>();
            _ = startupScope.ServiceProvider.GetRequiredService<RuntimeRunStartCleanupContinuation>();
        }
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
        var boundJwt = new JwtSecurityTokenHandler().ReadJwtToken(boundToken);
        var claims = boundJwt.Claims;
        var boundIssuer = boundJwt.Issuer;
        Assert.Equal(project.ProjectId, SingleClaim(claims, "project_id"));
        Assert.Equal(pending.RunId, SingleClaim(claims, "run_id"));
        Assert.Equal(BrokerRunBindingAuthority.ProjectsAuthorityKind,
            SingleClaim(claims, BrokerRunBindingAuthority.AuthorityKindClaim));
        Assert.False(string.IsNullOrEmpty(SingleClaim(claims, "aw:run_binding_selection_hash")));

        var startPath = $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start";
        var duplicateStartTask = SendAsync(gateway.Client, HttpMethod.Post, startPath, boundToken, [TenantId]);
        using var started = await SendAsync(gateway.Client, HttpMethod.Post, startPath, boundToken, [TenantId]);
        using var duplicateStarted = await duplicateStartTask;
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicateStarted.StatusCode);
        var result = await started.Content.ReadFromJsonAsync<StartProjectRunResponse>(OrchestratorJsonOptions);
        Assert.NotNull(result);
        Assert.Equal("started", result.State);
        Assert.Equal(pending.RootSessionId, result.RootSessionId);
        Assert.Equal("pending", result.RuntimeStartup?.State);
        using var runtimeOwnerResponse = await SendAsync(
            orchestrator,
            HttpMethod.Get,
            $"/internal/projects/{project.ProjectId}/runs/{pending.RunId}" +
                $"/coordination/sessions/{pending.RootSessionId}/runtime-owner-context",
            boundToken,
            [TenantId]);
        Assert.Equal(HttpStatusCode.OK, runtimeOwnerResponse.StatusCode);
        var firstStartOwner = await ReadJsonAsync<RuntimeOwnerContext>(runtimeOwnerResponse);
        var firstPlacement = await runtimeRunStartOwners.WaitForPlacementAsync();
        Assert.Equal(RuntimeRunStartKeys.EnvironmentId(firstStartOwner), firstPlacement.EnvironmentId);
        Assert.Equal("POST /internal/runtime/run-start/placement", Assert.Single(environmentPlacementRequests));
        Assert.Equal(1, runtimeRunStartOwners.UniquePlacementCount);

        var enrollmentPath = $"/internal/projects/{project.ProjectId}/runs/{pending.RunId}" +
            $"/coordination/sessions/{pending.RootSessionId}/runtime-registrations";
        RuntimeRegistration? firstRegistration = null;
        using (var registrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                while (firstRegistration is null)
                {
                    using var registrationResponse = await SendAsync(
                        orchestrator, HttpMethod.Get, enrollmentPath, boundToken, [TenantId]);
                    if (registrationResponse.StatusCode == HttpStatusCode.OK)
                        firstRegistration = await registrationResponse.Content.ReadFromJsonAsync<RuntimeRegistration>(
                            OrchestratorJsonOptions, registrationTimeout.Token);
                    else if (registrationResponse.StatusCode != HttpStatusCode.Conflict)
                        Assert.Fail(
                            $"Runtime registration returned {(int)registrationResponse.StatusCode}: " +
                            await registrationResponse.Content.ReadAsStringAsync(registrationTimeout.Token));
                    if (firstRegistration is null)
                        await Task.Delay(TimeSpan.FromMilliseconds(20), registrationTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (registrationTimeout.IsCancellationRequested)
            {
                Assert.Fail(
                    $"Runtime registration was not created. Owner calls: " +
                    $"{string.Join(" | ", runtimeRunStartOwners.ObservedPaths)}. " +
                    $"Route failures: {string.Join(" | ", runtimeOwnerFailures)}. " +
                    $"Logs: {string.Join(" | ", runtimeRunStartOwners.LoggerMessages)}.");
            }
        }
        Assert.NotNull(firstRegistration);
        var binding = firstRegistration.Binding;
        var staleBindingFields = new List<string>();
        if (binding.ActorIssuer != firstStartOwner.ActorIssuer) staleBindingFields.Add(nameof(binding.ActorIssuer));
        if (binding.ActorId != firstStartOwner.ActorId) staleBindingFields.Add(nameof(binding.ActorId));
        if (binding.TenantId != firstStartOwner.TenantId) staleBindingFields.Add(nameof(binding.TenantId));
        if (binding.ProjectId != firstStartOwner.ProjectId) staleBindingFields.Add(nameof(binding.ProjectId));
        if (binding.RunId != firstStartOwner.RunId) staleBindingFields.Add(nameof(binding.RunId));
        if (binding.SessionId != firstStartOwner.SessionId) staleBindingFields.Add(nameof(binding.SessionId));
        if (binding.AgentId != RuntimeActionContract.CoordinatorRootStepId) staleBindingFields.Add(nameof(binding.AgentId));
        var resolvedProfilePrefix =
            $"{firstPlacement.SourceProfileId[..Math.Min(firstPlacement.SourceProfileId.Length, 220)]}.r.";
        if (!binding.ProfileId.StartsWith(resolvedProfilePrefix, StringComparison.Ordinal) ||
            binding.ProfileId.Length != resolvedProfilePrefix.Length + 24 ||
            binding.ProfileId.AsSpan(resolvedProfilePrefix.Length).IndexOfAnyExcept("0123456789abcdef") >= 0)
            staleBindingFields.Add(nameof(binding.ProfileId));
        if (binding.AcceptedSelectionHash != firstStartOwner.AcceptedSelectionHash) staleBindingFields.Add(nameof(binding.AcceptedSelectionHash));
        if (binding.ExecutionFence != firstStartOwner.ExecutionFence) staleBindingFields.Add(nameof(binding.ExecutionFence));
        if (binding.EnvironmentId != firstPlacement.EnvironmentId) staleBindingFields.Add(nameof(binding.EnvironmentId));
        if (binding.PlacementGeneration != firstPlacement.SandboxResource.Generation) staleBindingFields.Add(nameof(binding.PlacementGeneration));
        if (binding.EnvironmentCurrentFencingGeneration != firstPlacement.CurrentFencingGeneration) staleBindingFields.Add(nameof(binding.EnvironmentCurrentFencingGeneration));
        if (binding.EnvironmentProviderFencingGeneration != firstPlacement.ProviderFencingGeneration) staleBindingFields.Add(nameof(binding.EnvironmentProviderFencingGeneration));
        Assert.Empty(staleBindingFields);
        using var currentOwnerHttp = new HttpClient(new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures))
        {
            BaseAddress = new Uri("https://orchestrator.test/")
        };
        var currentOwner = new RuntimeRegistrationHttpClient(currentOwnerHttp, new("https://orchestrator.test/"));
        using (var environmentContextHttp =
            new HttpClient(new RuntimeServiceRouter(runtimeRoutes, runtimeOwnerFailures))
            {
                BaseAddress = new Uri("https://environment.test/")
            })
        {
            var contextPath =
                $"/internal/projects/{firstStartOwner.ProjectId}/runs/{firstStartOwner.RunId}" +
                $"/environments/{firstPlacement.EnvironmentId}/coordination/sessions/{firstStartOwner.SessionId}" +
                $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(binding.ProfileId)}";
            using var contextResponse = await SendAsync(
                environmentContextHttp, HttpMethod.Get, contextPath, boundToken, [TenantId]);
            Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
            var environmentContext = await ReadJsonAsync<EnvironmentRuntimeBootstrapContext>(contextResponse);
            Assert.Equal(binding.ProfileId, environmentContext.ProfileId);
            Assert.Equal(firstPlacement.SandboxResource, environmentContext.Resource);
            Assert.Equal(firstPlacement.LifecycleGeneration, environmentContext.LifecycleGeneration);
            Assert.Equal(firstPlacement.LeaseRevision, environmentContext.LeaseRevision);
            Assert.Equal(firstPlacement.CurrentFencingGeneration, environmentContext.CurrentFencingGeneration);
            Assert.Equal(firstPlacement.ProviderFencingGeneration, environmentContext.ProviderFencingGeneration);
            Assert.Equal(firstPlacement.LeaseExpiresAt, environmentContext.LeaseExpiresAt);
            Assert.Equal(binding.PlacementUid, environmentContext.Resource.ResourceId);
        }
        await using var sdk = new ControlledCopilotRuntime(TimeSpan.FromMinutes(2))
        {
            EmitUsageAfterCreate = false,
            ExpectedAvailableToolsCount = 7,
            SdkCredential = copilotConnection.AccessToken
        };
        var modelSelectionReference = firstRegistration.Binding.ModelSelectionReference
            ?? throw new InvalidOperationException("The selected runtime model reference is missing.");
        var modelBindings = new RuntimeModelBindingsResolver(
            "runtime-run-start-test-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                [modelSelectionReference] = new(sdk.ModelId, ModelSourceMode.HostedCopilot)
            });
        var privateStateDirectory = Path.Combine(
            AppContext.BaseDirectory, "runtime-run-start-state", firstRegistration.RuntimeInstanceId.ToString("N"));
        var sessions = new RuntimeCopilotSessionFactory(
            sdk.Connection, privateStateDirectory, modelBindings, workingDirectory: privateStateDirectory);
        using var runtimeHostHttp = new HttpClient(new RuntimeServiceRouter(
            runtimeRoutes,
            runtimeOwnerFailures,
            async (request, response, token) =>
            {
                if (!response.IsSuccessStatusCode ||
                    request.RequestUri is not { Host: "environment.test", AbsolutePath: var path } ||
                    !(path.EndsWith("/readiness", StringComparison.Ordinal) ||
                      path.EndsWith("/pod-observation", StringComparison.Ordinal)))
                    return;
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))
                    ?? throw new InvalidOperationException("Environment returned an empty Host-readiness response.");
                if (path.EndsWith("/readiness", StringComparison.Ordinal))
                    body["workspaceMountPath"] = privateStateDirectory;
                else
                    body["workspaceAttachment"]!["mountPath"] = privateStateDirectory;
                response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }));
        await using var runtimeAgentHost = new RuntimeAgentHost(
            new RuntimeAgentHostOptions(
                firstRegistration.Binding.ConfigureEndpoint,
                runtimeBroker.BaseAddress!,
                new("https://orchestrator.test/"),
                new("https://environment.test/"),
                new("https://events.test/"),
                new("https://projects.test/"),
                firstRegistration.Binding.Image!,
                2),
            runtimeHostHttp,
            currentOwner,
            sessions,
            TimeProvider.System);
        using var runtimeReceiverHost = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                AddJwtBearer(services, signingKey);
                services.AddAuthorization();
                services.ConfigureHttpJsonOptions(options =>
                {
                    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                    options.SerializerOptions.Converters.Add(
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Value?.EndsWith("/activate", StringComparison.Ordinal) == true)
                    {
                        runtimeOwnerFailures.Enqueue(
                            $"Host activation request scheme={context.Request.Scheme}, host={context.Request.Host}, " +
                            $"expected={runtimeAgentHost.ConfigureEndpoint.Scheme}://" +
                            $"{runtimeAgentHost.ConfigureEndpoint.Authority}, endpoint={context.GetEndpoint()?.DisplayName}.");
                        await next();
                        runtimeOwnerFailures.Enqueue(
                            $"Host activation response status={context.Response.StatusCode}, " +
                            $"cache-control={context.Response.Headers.CacheControl}.");
                        return;
                    }
                    await next();
                });
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapRuntimeAgentHost(runtimeAgentHost, TimeProvider.System));
            });
        }).StartAsync();
        Assert.True(runtimeReceiverReady.TrySetResult(runtimeReceiverHost.GetTestServer().CreateHandler()));

        var startStore = new CoordinationRuntimeRunStartStore(_nativeDataSource, ownerSchema);
        var startedIntent = await WaitForRuntimeStartIntentAsync(
            startStore,
            firstStartOwner,
            "ready",
            runtimeOwnerFailures.TakeLast(20).Concat(runtimeRunStartOwners.ReadinessDiagnostics));
        Assert.NotNull(startedIntent);
        Assert.Contains(runtimeRunStartOwners.ObservedPaths,
            path => path.Contains("/internal/runtime/v1/activate", StringComparison.Ordinal));
        Assert.DoesNotContain(runtimeRunStartOwners.ObservedPaths,
            path => path.Contains("/internal/runtime/observations", StringComparison.Ordinal));
        Assert.False(sdk.TurnReceived.Task.IsCompleted);
        Assert.DoesNotContain(runtimeRunStartOwners.LoggerMessages,
            message => message.Contains("runtime first-start continuation failed", StringComparison.Ordinal));
        var placementsBeforeReadyStatus = runtimeRunStartOwners.UniquePlacementCount;
        var bootstrapRequestsBeforeReadyStatus = runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/bootstrap/request", StringComparison.Ordinal));
        var activationsBeforeReadyStatus = runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/v1/activate", StringComparison.Ordinal));
        using (var readyStatusResponse = await SendAsync(
            gateway.Client,
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/status",
            boundToken,
            [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, readyStatusResponse.StatusCode);
            var readyStatus = await readyStatusResponse.Content.ReadFromJsonAsync<SessionStatusSnapshot>(
                OrchestratorJsonOptions);
            Assert.Equal("ready", readyStatus?.RuntimeStartup?.State);
        }
        Assert.Equal(placementsBeforeReadyStatus, runtimeRunStartOwners.UniquePlacementCount);
        Assert.Equal(bootstrapRequestsBeforeReadyStatus, runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/bootstrap/request", StringComparison.Ordinal)));
        Assert.Equal(activationsBeforeReadyStatus, runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/v1/activate", StringComparison.Ordinal)));
        try
        {
            await runtimeAgentHost.DisposeAsync();
        }
        catch (RuntimeAuthorizationException error)
        {
            var codes = runtimeOwnerFailures
                .Select(failure => System.Text.RegularExpressions.Regex.Match(
                    failure, "\"(?:code|error)\"\\s*:\\s*\"([a-z0-9_]+)\""))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct();
            Assert.Fail($"{error.Code}; downstream error codes: {string.Join(", ", codes)}");
        }

        var concurrentStartReplays = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(
            gateway.Client,
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start",
            boundToken,
            [TenantId])));
        try
        {
            foreach (var replayedStart in concurrentStartReplays)
            {
                var responseBody = await replayedStart.Content.ReadAsStringAsync();
                Assert.True(
                    replayedStart.StatusCode == HttpStatusCode.OK,
                    $"Expected OK, received {(int)replayedStart.StatusCode}: {responseBody}");
                var replayed = JsonSerializer.Deserialize<StartProjectRunResponse>(
                    responseBody, OrchestratorJsonOptions);
                Assert.NotNull(replayed);
                Assert.Equal(result.RootSessionId, replayed.RootSessionId);
                Assert.Equal("started", replayed.State);
                Assert.Equal("pending", replayed.RuntimeStartup?.State);
            }
        }
        finally
        {
            foreach (var replayedStart in concurrentStartReplays)
                replayedStart.Dispose();
        }

        var runMessagePath = $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/messages";
        using (var missingKey = await SendRunMessageAsync(boundToken, runMessagePath, null, "Status?"))
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using (var unboundDenied = await SendRunMessageAsync(unboundToken, runMessagePath, "user-message-1", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, unboundDenied.StatusCode);
        using (var wrongProject = await SendRunMessageAsync(
            boundToken, $"/api/v1/projects/{project.ProjectId}-other/runs/{pending.RunId}/messages",
            "wrong-project", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongProject.StatusCode);
        using (var wrongRun = await SendRunMessageAsync(
            boundToken, $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}-other/messages",
            "wrong-run", "Status?"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongRun.StatusCode);
        using (var spoofedSender = new HttpRequestMessage(HttpMethod.Post, runMessagePath)
        {
            Content = new StringContent(
                """{"text":"Status?","senderIssuer":"https://spoofed.test","senderSubject":"someone-else"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        })
        {
            AddBearerAndTenant(spoofedSender, boundToken, TenantId);
            spoofedSender.Headers.Add("Idempotency-Key", "spoofed-sender");
            using var response = await gateway.Client.SendAsync(spoofedSender);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var genericSelfMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/messages")
        {
            Content = JsonContent.Create(new
            {
                recipientSessionId = pending.RootSessionId,
                idempotencyKey = "generic-self",
                deliveryMode = "immediate",
                purpose = "steering",
                kind = "text",
                payload = new { text = "Status?" },
            }),
        })
        {
            AddBearerAndTenant(genericSelfMessage, boundToken, TenantId);
            using var response = await gateway.Client.SendAsync(genericSelfMessage);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using (var startupIntentCount = new NpgsqlCommand($"""
            SELECT count(*)
            FROM "{ownerSchema}".runtime_run_start_intents
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
            """, connection))
        {
            startupIntentCount.Parameters.AddWithValue("project", project.ProjectId);
            startupIntentCount.Parameters.AddWithValue("run", pending.RunId);
            startupIntentCount.Parameters.AddWithValue("root", pending.RootSessionId);
            Assert.Equal(1L, (long)(await startupIntentCount.ExecuteScalarAsync())!);
        }
        await using (var firstMessageCount = new NpgsqlCommand($"""
            SELECT count(*)
            FROM "{ownerSchema}".coordination_messages
            WHERE project_id = @project AND run_id = @run
              AND sender_session_id = @root AND idempotency_key = @key
            """, connection))
        {
            firstMessageCount.Parameters.AddWithValue("project", project.ProjectId);
            firstMessageCount.Parameters.AddWithValue("run", pending.RunId);
            firstMessageCount.Parameters.AddWithValue("root", pending.RootSessionId);
            firstMessageCount.Parameters.AddWithValue("key", $"{pending.RunId}:first");
            Assert.Equal(1L, (long)(await firstMessageCount.ExecuteScalarAsync())!);
        }
        await using (var activeSessionCountCommand = new NpgsqlCommand($"""
            SELECT count(*) FROM "{ownerSchema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run
              AND lifecycle_state = 'active'
            """, connection))
        {
            activeSessionCountCommand.Parameters.AddWithValue("project", project.ProjectId);
            activeSessionCountCommand.Parameters.AddWithValue("run", pending.RunId);
            var activeSessionCount = (long)(await activeSessionCountCommand.ExecuteScalarAsync())!;
            Assert.Equal(1L, activeSessionCount);
        }

        using var userMessage = await SendRunMessageAsync(boundToken, runMessagePath, "user-message-1", "Status?");
        Assert.Equal(HttpStatusCode.OK, userMessage.StatusCode);
        var userReceipt = await userMessage.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
        Assert.NotNull(userReceipt);
        Assert.Equal(project.ProjectId, userReceipt.ProjectId);
        Assert.Equal(pending.RunId, userReceipt.RunId);
        Assert.Equal(pending.RootSessionId, userReceipt.RootSessionId);
        Assert.Equal(pending.RootSessionId, userReceipt.RecipientSessionId);
        Assert.Equal("accepted", userReceipt.Status);
        Assert.Equal(boundIssuer, userReceipt.Sender.Issuer);
        Assert.Equal(subject, userReceipt.Sender.Subject);
        Assert.NotEqual(Guid.Empty, userReceipt.MessageId);
        Assert.True(userReceipt.ThreadSequence > 0);

        using var userMessageReplay = await SendRunMessageAsync(
            boundToken, runMessagePath, "user-message-1", "Status?");
        Assert.Equal(HttpStatusCode.OK, userMessageReplay.StatusCode);
        var replayedUserReceipt =
            await userMessageReplay.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
        Assert.Equal(userReceipt, replayedUserReceipt);

        using (var idempotencyConflict = await SendRunMessageAsync(
            boundToken, runMessagePath, "user-message-1", "Different text"))
            Assert.Equal(HttpStatusCode.Conflict, idempotencyConflict.StatusCode);

        await runtimeEnvironmentHolder.ExpireCurrentLeaseAsync();
        using (var expiredStatusResponse = await SendAsync(
            gateway.Client,
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/status",
            boundToken,
            [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, expiredStatusResponse.StatusCode);
            var expiredStatus = await expiredStatusResponse.Content.ReadFromJsonAsync<SessionStatusSnapshot>(
                OrchestratorJsonOptions);
            Assert.Equal("blocked", expiredStatus?.RuntimeStartup?.State);
            Assert.Equal("sandbox_lease_expired", expiredStatus?.RuntimeStartup?.FailureCode);
        }
        Assert.Equal(placementsBeforeReadyStatus, runtimeRunStartOwners.UniquePlacementCount);
        Assert.Equal(bootstrapRequestsBeforeReadyStatus, runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/bootstrap/request", StringComparison.Ordinal)));
        Assert.Equal(activationsBeforeReadyStatus, runtimeRunStartOwners.ObservedPaths.Count(
            path => path.Contains("/internal/runtime/v1/activate", StringComparison.Ordinal)));

        var messageTargetSessionId = $"message-target-{Guid.NewGuid():N}";
        if (hasLegacyChild)
        {
            using (var registerTarget = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/children")
            {
                Content = JsonContent.Create(new { sessionId = messageTargetSessionId }),
            })
            {
                AddBearerAndTenant(registerTarget, boundToken, TenantId);
                using var response = await gateway.Client.SendAsync(registerTarget);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
            using var explicitTargetMessage = await SendRunMessageAsync(
                boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
            Assert.Equal(HttpStatusCode.OK, explicitTargetMessage.StatusCode);
            var explicitTargetReceipt =
                await explicitTargetMessage.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(OrchestratorJsonOptions);
            Assert.NotNull(explicitTargetReceipt);
            Assert.Equal(pending.RootSessionId, explicitTargetReceipt.RootSessionId);
            Assert.Equal(messageTargetSessionId, explicitTargetReceipt.RecipientSessionId);
            Assert.Equal(userReceipt.Sender, explicitTargetReceipt.Sender);

            using (var targetStatusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/status"))
            {
                AddBearerAndTenant(targetStatusRequest, boundToken, TenantId);
                using var targetStatusResponse = await orchestrator.SendAsync(targetStatusRequest);
                Assert.Equal(HttpStatusCode.OK, targetStatusResponse.StatusCode);
                var targetStatus =
                    await targetStatusResponse.Content.ReadFromJsonAsync<SessionStatusSnapshot>(OrchestratorJsonOptions);
                Assert.NotNull(targetStatus);

                using var turnBoundaryRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/turn-boundary")
                {
                    Content = JsonContent.Create(
                        new TurnBoundaryRequest(targetStatus.ExecutionFence, targetStatus.StateVersion),
                        options: OrchestratorJsonOptions),
                };
                AddBearerAndTenant(turnBoundaryRequest, boundToken, TenantId);
                using var turnBoundaryResponse = await orchestrator.SendAsync(turnBoundaryRequest);
                Assert.Equal(HttpStatusCode.OK, turnBoundaryResponse.StatusCode);
                var turnBoundary =
                    await turnBoundaryResponse.Content.ReadFromJsonAsync<TurnBoundaryResult>(OrchestratorJsonOptions);
                Assert.NotNull(turnBoundary);
                var presentedMessage = Assert.IsType<AddressedMessageEnvelope>(turnBoundary.PresentedMessage);
                Assert.Equal(explicitTargetReceipt.MessageId, presentedMessage.MessageId);
                Assert.Equal(AddressedMessageStatus.Delivered, presentedMessage.Status);

                using var replayAfterPresentation = await SendRunMessageAsync(
                    boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
                Assert.Equal(HttpStatusCode.OK, replayAfterPresentation.StatusCode);
                var presentedReplayReceipt =
                    await replayAfterPresentation.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(
                        OrchestratorJsonOptions);
                Assert.Equal(explicitTargetReceipt, presentedReplayReceipt);

                using var acknowledgeRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/messages/{presentedMessage.MessageId:D}/acknowledge")
                {
                    Content = JsonContent.Create(
                        new AddressedMessageClaimFenceRequest(presentedMessage.ClaimFence),
                        options: OrchestratorJsonOptions),
                };
                AddBearerAndTenant(acknowledgeRequest, boundToken, TenantId);
                using var acknowledgeResponse = await orchestrator.SendAsync(acknowledgeRequest);
                Assert.Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);
                var acknowledgedMessage =
                    await acknowledgeResponse.Content.ReadFromJsonAsync<AddressedMessageEnvelope>(OrchestratorJsonOptions);
                Assert.NotNull(acknowledgedMessage);
                Assert.Equal(AddressedMessageStatus.Acknowledged, acknowledgedMessage.Status);

                using var replayAfterAcknowledgment = await SendRunMessageAsync(
                    boundToken, runMessagePath, "user-message-2", "Worker status?", messageTargetSessionId);
                Assert.Equal(HttpStatusCode.OK, replayAfterAcknowledgment.StatusCode);
                var acknowledgmentReplayReceipt =
                    await replayAfterAcknowledgment.Content.ReadFromJsonAsync<ProjectRunMessageReceipt>(
                        OrchestratorJsonOptions);
                Assert.Equal(explicitTargetReceipt, acknowledgmentReplayReceipt);
            }
        }

        // Replay start and check the real Events journal, not a mocked owner receipt.
        using var replay = await SendAsync(gateway.Client, HttpMethod.Post,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/start", boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var journal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
            """, connection);
        journal.Parameters.AddWithValue("project", project.ProjectId);
        journal.Parameters.AddWithValue("run", pending.RunId);
        journal.Parameters.AddWithValue("key", $"{pending.RunId}:first");
        Assert.Equal(1L, await journal.ExecuteScalarAsync());

        await using (var userMessageJournal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
              AND sender_session_id = @root AND recipient_session_id = @root
              AND sender_issuer = @issuer AND sender_identity_hash = @subjectHash
              AND payload ->> 'text' = @text
            """, connection))
        {
            userMessageJournal.Parameters.AddWithValue("project", project.ProjectId);
            userMessageJournal.Parameters.AddWithValue("run", pending.RunId);
            userMessageJournal.Parameters.AddWithValue("key", "user-message-1");
            userMessageJournal.Parameters.AddWithValue("root", pending.RootSessionId);
            userMessageJournal.Parameters.AddWithValue("issuer", boundIssuer);
            userMessageJournal.Parameters.AddWithValue("subjectHash",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{boundIssuer}\0{subject}"))));
            userMessageJournal.Parameters.AddWithValue("text", "Status?");
            Assert.Equal(1L, await userMessageJournal.ExecuteScalarAsync());
        }
        if (hasLegacyChild)
        {
            await using (var acknowledgedTargetJournal = new NpgsqlCommand($"""
            SELECT count(*) FROM "{eventsSchema}".addressed_messages
            WHERE project_id = @project AND sender_run_id = @run AND idempotency_key = @key
              AND recipient_session_id = @recipient AND status = 'Acknowledged'
            """, connection))
            {
                acknowledgedTargetJournal.Parameters.AddWithValue("project", project.ProjectId);
                acknowledgedTargetJournal.Parameters.AddWithValue("run", pending.RunId);
                acknowledgedTargetJournal.Parameters.AddWithValue("key", "user-message-2");
                acknowledgedTargetJournal.Parameters.AddWithValue("recipient", messageTargetSessionId);
                Assert.Equal(1L, await acknowledgedTargetJournal.ExecuteScalarAsync());
            }
        }
        await using (var ownerMessage = new NpgsqlCommand($"""
            SELECT count(*) FROM "{ownerSchema}".coordination_messages
            WHERE project_id = @project AND run_id = @run AND message_id = @message
              AND writer_issuer = @issuer AND writer_subject = @subject
            """, connection))
        {
            ownerMessage.Parameters.AddWithValue("project", project.ProjectId);
            ownerMessage.Parameters.AddWithValue("run", pending.RunId);
            ownerMessage.Parameters.AddWithValue("message", userReceipt.OwnerMessageId);
            ownerMessage.Parameters.AddWithValue("issuer", boundIssuer);
            ownerMessage.Parameters.AddWithValue("subject", subject);
            Assert.Equal(1L, await ownerMessage.ExecuteScalarAsync());
        }

        if (hasLegacyChild)
        {
            using (var childSteering = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/messages")
            {
                Content = JsonContent.Create(new CoordinationMessageRequest(
                    pending.RootSessionId,
                    "child-to-root-steering",
                    AddressedMessageDeliveryMode.Immediate,
                    AddressedMessagePurpose.Steering,
                    AddressedMessageKind.Text,
                    System.Text.Json.JsonSerializer.SerializeToElement(new { text = "Worker status?" })),
                    options: OrchestratorJsonOptions),
            })
            {
                AddBearerAndTenant(childSteering, boundToken, TenantId);
                using var response = await orchestrator.SendAsync(childSteering);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        var rootStatusPath =
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{pending.RootSessionId}/status";
        var childStatusPath =
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/coordination/sessions/{messageTargetSessionId}/status";
        using var rootStatusBeforeCancelResponse = await SendAsync(
            gateway.Client, HttpMethod.Get, rootStatusPath, boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, rootStatusBeforeCancelResponse.StatusCode);
        var rootStatusBeforeCancel = await rootStatusBeforeCancelResponse.Content
            .ReadFromJsonAsync<SessionStatusSnapshot>(OrchestratorJsonOptions);
        Assert.NotNull(rootStatusBeforeCancel);
        Assert.Equal(CoordinationSessionKind.Coordinator, rootStatusBeforeCancel.Kind);
        Assert.Equal(CoordinationLifecycleState.Active, rootStatusBeforeCancel.Lifecycle);
        Assert.NotNull(rootStatusBeforeCancel.ExecutionRecovery);

        SessionStatusSnapshot? childStatusBeforeCancel = null;
        if (hasLegacyChild)
        {
            using var childStatusBeforeCancelResponse = await SendAsync(
                gateway.Client, HttpMethod.Get, childStatusPath, boundToken, [TenantId]);
            Assert.Equal(HttpStatusCode.OK, childStatusBeforeCancelResponse.StatusCode);
            childStatusBeforeCancel = await childStatusBeforeCancelResponse.Content
                .ReadFromJsonAsync<SessionStatusSnapshot>(OrchestratorJsonOptions);
            Assert.NotNull(childStatusBeforeCancel);
            Assert.Equal(CoordinationSessionKind.ChildWork, childStatusBeforeCancel.Kind);
            Assert.Null(childStatusBeforeCancel.ExecutionRecovery);
        }

        var acceptedSelectionPath =
            $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/selection";
        Interlocked.Exchange(ref statusSelectionReads, 0);
        statusSelectionChange.ChangeSecondResponse(acceptedSelectionPath);
        statusSelectionBarrier.Arm((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get &&
            request.RequestUri?.AbsolutePath == acceptedSelectionPath &&
            Interlocked.Increment(ref statusSelectionReads) == 2));
        var changedSelectionRead = SendAsync(
            gateway.Client, HttpMethod.Get, rootStatusPath, boundToken, [TenantId]);
        try
        {
            var selectionPause = statusSelectionBarrier.WaitUntilPausedAsync();
            var selectionRace = await Task.WhenAny(selectionPause, changedSelectionRead)
                .WaitAsync(TimeSpan.FromSeconds(30));
            if (selectionRace == changedSelectionRead)
            {
                using var earlyResponse = await changedSelectionRead;
                Assert.Fail(
                    $"The status read finished before the final-selection barrier " +
                    $"(selection reads: {statusSelectionReads}, status: {(int)earlyResponse.StatusCode}).");
            }
            await selectionPause;
            statusSelectionBarrier.Release();
            using var changedSelectionResponse =
                    await changedSelectionRead.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Conflict, changedSelectionResponse.StatusCode);
            Assert.Contains(
                "coordinator_selection_stale",
                await changedSelectionResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            statusSelectionBarrier.Release();
        }

        // Model a lost placement response: cleanup has the stable provision key, but no saved placement receipt.
        await using (var lostPlacementResponse = new NpgsqlCommand($"""
            UPDATE "{ownerSchema}".runtime_run_start_intents
            SET receipt_links = receipt_links - 'placement'
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @root
            """, connection))
        {
            lostPlacementResponse.Parameters.AddWithValue("tenant", TenantId);
            lostPlacementResponse.Parameters.AddWithValue("project", project.ProjectId);
            lostPlacementResponse.Parameters.AddWithValue("run", pending.RunId);
            lostPlacementResponse.Parameters.AddWithValue("root", pending.RootSessionId);
            Assert.Equal(1, await lostPlacementResponse.ExecuteNonQueryAsync());
        }

        // Project Contributors can cancel another actor's run without obtaining that actor's run-bound token.
        var contributorToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "chat-contributor", null, null, []);
        var contributorSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(contributorToken).Claims, "sub");
        var contributorMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, contributorSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, contributorMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Contributor);
        var viewerToken = await IssueTokenAsync(
            "projects.orchestrator", [TenantId], "chat-viewer", null, null, []);
        var viewerSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(viewerToken).Claims, "sub");
        var viewerMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, viewerSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource, viewerMembership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Viewer);
        using (var deniedCreate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/{project.ProjectId}/runs")
        {
            Content = JsonContent.Create(new CreateProjectRunRequest("Not an Orchestrator.", project.ConfigurationRevision)),
        })
        {
            AddBearerAndTenant(deniedCreate, contributorToken, TenantId);
            deniedCreate.Headers.Add("Idempotency-Key", "contributor-create");
            using var response = await gateway.Client.SendAsync(deniedCreate);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("project_orchestrator_role_required_acceptRunSelection",
                await response.Content.ReadAsStringAsync());
        }
        using (var denied = await CancelChatAsync(viewerToken, "viewer-cancel"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var boundDenied = await CancelChatAsync(boundToken, "bound-cancel"))
            Assert.Equal(HttpStatusCode.Forbidden, boundDenied.StatusCode);
        Interlocked.Exchange(ref statusSelectionReads, 0);
        statusSelectionBarrier.Arm((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get &&
            request.RequestUri?.AbsolutePath == acceptedSelectionPath &&
            Interlocked.Increment(ref statusSelectionReads) == 2));
        var overlappingStatusRead = SendAsync(
            gateway.Client, HttpMethod.Get, rootStatusPath, boundToken, [TenantId]);
        try
        {
            await statusSelectionBarrier.WaitUntilPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var firstCancelToken = await IssueTokenAsync(
                "projects.orchestrator", [TenantId], "chat-contributor", null, null, []);
            using (var selectionBeforeCancel = await SendAsync(
                projects.Client, HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/cancellation-selection",
                firstCancelToken, [TenantId]))
                Assert.Equal(HttpStatusCode.OK, selectionBeforeCancel.StatusCode);
            using var cancelled = await CancelChatAsync(firstCancelToken, "contributor-cancel");
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var receipt = await cancelled.Content.ReadFromJsonAsync<CancelProjectRunResponse>(OrchestratorJsonOptions);
            Assert.NotNull(receipt);
            Assert.False(receipt.AlreadyTerminal);
            using (var selectionAfterCancel = await SendAsync(
                projects.Client, HttpMethod.Get,
                $"/api/projects/{project.ProjectId}/runs/{pending.RunId}/cancellation-selection",
                firstCancelToken, [TenantId]))
            {
                var selectionError = await selectionAfterCancel.Content.ReadAsStringAsync();
                Assert.True(
                    selectionAfterCancel.StatusCode == HttpStatusCode.OK,
                    $"Post-cancel selection status: {(int)selectionAfterCancel.StatusCode}; body: {selectionError}");
            }

            using (var authorizationResponse = await SendAsync(
                projects.Client, HttpMethod.Get, "/api/authorization/context", firstCancelToken, [TenantId]))
            {
                Assert.Equal(HttpStatusCode.OK, authorizationResponse.StatusCode);
                var authorization = await ReadJsonAsync<ProjectsAuthorizationContext>(authorizationResponse);
                var canonicalIssuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
                var hasCancelPermission = authorization.EffectiveAuthority.Any(resource =>
                    (resource.ResourceType == "project" && resource.ResourceId == project.ProjectId ||
                     resource.ResourceType == "tenant" && resource.ResourceId == TenantId) &&
                    resource.Permissions.Any(permission =>
                        permission.Permission == "cancelRun" && permission.RoleRevision > 0));
                Assert.True(
                    authorization.ContractVersion == 1 &&
                    authorization.Issuer == canonicalIssuer &&
                    authorization.ActorId == contributorSubject &&
                    authorization.TenantId == TenantId &&
                    authorization.MembershipRevision > 0 &&
                    authorization.BoundRunId is null &&
                    (authorization.BoundProjectId is null || authorization.BoundProjectId == project.ProjectId) &&
                    hasCancelPermission,
                    $"Fresh cancellation authority fields: contract={authorization.ContractVersion == 1}, " +
                    $"issuer={authorization.Issuer == IdentityBrokerWebApplicationFactory.Issuer}, " +
                    $"actor={authorization.ActorId == contributorSubject}, tenant={authorization.TenantId == TenantId}, " +
                    $"membership={authorization.MembershipRevision > 0}, boundRun={authorization.BoundRunId is not null}, " +
                    $"boundProject={authorization.BoundProjectId is not null}, permission={hasCancelPermission}.");
            }

            var firstCancelExpiry = new DateTimeOffset(
                new JwtSecurityTokenHandler().ReadJwtToken(firstCancelToken).ValidTo);
            var cleanupCredential = new SecretCredential(firstCancelToken, firstCancelExpiry, TimeProvider.System);
            AuthorizedRunSelection cleanupSelection;
            try
            {
                using var cleanupScope = orchestratorFactory.Services.CreateScope();
                cleanupSelection = await cleanupScope.ServiceProvider
                    .GetRequiredService<ProjectsRunSelectionClient>()
                    .ReadSelectionForCancelAsync(
                        new RuntimeActorAuthorization(cleanupCredential, TenantId),
                        new CoordinationActor(
                            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
                            contributorSubject),
                        project.ProjectId, pending.RunId, CancellationToken.None);
                Assert.Equal(contributorSubject, cleanupSelection.Authorization.ActorId);
            }
            finally
            {
                cleanupCredential.Invalidate();
            }

            var cleanupIntent = await startStore.FindCleanupIntentAsync(
                TenantId, project.ProjectId, pending.RunId, CancellationToken.None);
            Assert.NotNull(cleanupIntent);
            Assert.Equal("cleanup_pending", cleanupIntent.State);
            Assert.True(cleanupIntent.CleanupObligation);
            Assert.Equal(
                cleanupIntent.AcceptedSelectionHash,
                CoordinationOwnerStore.HashSelection(cleanupSelection.Selection),
                StringComparer.OrdinalIgnoreCase);
            if (hasLegacyChild)
            {
                var legacyFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
                    startStore.ReadCleanupProjectionAsync(
                        project.ProjectId, pending.RunId, pending.RootSessionId, cleanupSelection,
                        CancellationToken.None));
                Assert.Equal("runtime_start_child_cleanup_pending", legacyFailure.Code);
                var aggregate = await startStore.ReadCleanupSummaryAsync(
                    TenantId, project.ProjectId, pending.RunId, CancellationToken.None);
                Assert.Equal("pending", aggregate!.State);
                Assert.Equal(0, runtimeRunStartOwners.CleanupCount);
            }
            else
            {
                var cleanupProjection = await startStore.ReadCleanupProjectionAsync(
                    project.ProjectId, pending.RunId, pending.RootSessionId, cleanupSelection,
                    CancellationToken.None);
                Assert.Equal(cleanupIntent.IntentId, cleanupProjection.IntentId);
                Assert.True(
                    runtimeRunStartOwners.UniquePlacementCount == 1,
                    $"Observed Environment calls: {string.Join(", ", runtimeRunStartOwners.ObservedPaths)}");
                var firstCleanup = await runtimeRunStartOwners.WaitForCleanupAsync(1);
                Assert.Equal(firstCancelToken, firstCleanup.Bearer);
                Assert.Equal(cleanupIntent.IntentId, firstCleanup.Projection.IntentId);
                Assert.Equal("cleanup_pending", firstCleanup.Projection.CleanupDisposition);
                Assert.False(firstCleanup.Projection.ReceiptLinks.TryGetProperty("placement", out _));
                Assert.Equal(runtimeRunStartOwners.FirstPlacement.SandboxOperationId,
                    firstCleanup.Placement.SandboxOperationId);
                using (var deniedCleanupReplay = await CancelChatAsync(viewerToken, "viewer-cleanup-replay"))
                    Assert.Equal(HttpStatusCode.Forbidden, deniedCleanupReplay.StatusCode);
                Assert.Equal(1, runtimeRunStartOwners.CleanupCount);
                Assert.Equal(
                    "cleanup_pending",
                    (await startStore.FindCleanupIntentAsync(
                        TenantId, project.ProjectId, pending.RunId, CancellationToken.None))?.State);
                var lateReady = await Assert.ThrowsAsync<CoordinationException>(() =>
                    startStore.CommitReadyAsync(
                        cleanupIntent,
                        firstStartOwner,
                        cleanupIntent.RowVersion,
                        new { late = true },
                        CancellationToken.None));
                Assert.Equal("runtime_start_run_terminal", lateReady.Code);

                var cleanupState = await WaitForCleanupStateAsync(
                    startStore, TenantId, project.ProjectId, pending.RunId, "cleanup_pending");
                Assert.NotNull(cleanupState);
                var replayOwnerSchema = ownerSchema;
                await using var replayFactory = new OrchestratorIntegrationFactory(
                    _connectionString,
                    replayOwnerSchema,
                    signingKey,
                    projects.CreateHandler,
                    () => eventsReference!.Server.CreateHandler(),
                    environmentHandler: () => runtimeRunStartOwners.CreateHandler(
                        () => eventsReference!.Server.CreateHandler()),
                    runtimeUsageHandler: () => eventsReference!.Server.CreateHandler(),
                    loggerProvider: runtimeRunStartOwners.LoggerProvider);
                using var replayOrchestrator = replayFactory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    BaseAddress = new Uri("https://orchestrator.test/")
                });
                await using var replayGateway = GatewayProductionResourceServer.Start(
                    signingKey,
                    (request, token) => ForwardOwnerRequestAsync(request, projects.Client, token),
                    orchestratorOwner: (request, token) => ForwardOwnerRequestAsync(
                        request, replayOrchestrator, token),
                    identityAudience: "https://api.test");
                using var cancelReplayRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/cancel")
                {
                    Content = JsonContent.Create(new CancelProjectRunRequest())
                };
                var replayCancelToken = await IssueTokenAsync(
                    "projects.orchestrator", [TenantId], "chat-contributor", null, null, []);
                AddBearerAndTenant(cancelReplayRequest, replayCancelToken, TenantId);
                cancelReplayRequest.Headers.Add("Idempotency-Key", "contributor-cancel-restart-replay");
                using var cancelReplay = await replayGateway.Client.SendAsync(cancelReplayRequest);
                Assert.Equal(HttpStatusCode.OK, cancelReplay.StatusCode);
                var secondCleanup = await runtimeRunStartOwners.WaitForCleanupAsync(2);
                Assert.Equal(replayCancelToken, secondCleanup.Bearer);
                Assert.Equal(firstCleanup.Projection.IntentId, secondCleanup.Projection.IntentId);
                Assert.Equal(
                    firstCleanup.Placement.SandboxOperationId,
                    secondCleanup.Placement.SandboxOperationId);
                var savedCleanupAttempt = secondCleanup.Projection.ReceiptLinks
                    .GetProperty("cleanupAttempt").Deserialize<RuntimeRunStartCleanupAttempt>(OrchestratorJsonOptions);
                Assert.NotNull(savedCleanupAttempt);
                Assert.Equal(secondCleanup.Placement.SandboxOperationId, savedCleanupAttempt.SandboxOperationId);
                Assert.Equal(secondCleanup.Placement.SandboxResource.Generation, savedCleanupAttempt.ResourceGeneration);
                Assert.Equal(secondCleanup.Placement.ProviderFencingGeneration,
                    savedCleanupAttempt.ProviderFencingGeneration);
                Assert.Equal(secondCleanup.Placement.SandboxResource, savedCleanupAttempt.SandboxResource);
                Assert.Equal("cleanup_pending", secondCleanup.Projection.CleanupDisposition);
                var completedCleanupState = await WaitForCleanupStateAsync(
                    startStore, TenantId, project.ProjectId, pending.RunId, "cleanup_complete");
                Assert.NotNull(completedCleanupState);
                var completedCleanupLinks = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    completedCleanupState.ReceiptLinks, OrchestratorJsonOptions);
                Assert.NotNull(completedCleanupLinks);
                var completedCleanupReceipt = completedCleanupLinks["cleanup"]
                    .Deserialize<RuntimeRunStartCleanupReceipt>(OrchestratorJsonOptions);
                Assert.NotNull(completedCleanupReceipt);
                var mismatchedCleanupReceipt = completedCleanupReceipt with
                {
                    SandboxOperationId = Guid.NewGuid()
                };
                var completedProjection = await startStore.ReadCleanupProjectionAsync(
                    project.ProjectId, pending.RunId, pending.RootSessionId, cleanupSelection,
                    CancellationToken.None, contractVersion: 2);
                var mismatch = await Assert.ThrowsAsync<CoordinationException>(() =>
                    startStore.MarkCleanupCompleteAsync(
                        completedCleanupState,
                        completedCleanupState.RowVersion,
                        mismatchedCleanupReceipt,
                        completedProjection,
                        CancellationToken.None));
                Assert.Equal("runtime_start_cleanup_receipt_invalid", mismatch.Code);
            }
        }
        finally
        {
            statusSelectionBarrier.Release();
        }
        using (var overlapping = await overlappingStatusRead.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            if (overlapping.StatusCode == HttpStatusCode.OK)
            {
                var earlier = await overlapping.Content.ReadFromJsonAsync<SessionStatusSnapshot>(
                    OrchestratorJsonOptions);
                Assert.NotNull(earlier);
                Assert.Equal(CoordinationLifecycleState.Active, earlier.Lifecycle);
                Assert.Equal(rootStatusBeforeCancel.ExecutionFence, earlier.ExecutionFence);
                Assert.Equal(rootStatusBeforeCancel.StateVersion, earlier.StateVersion);
                Assert.Equal(rootStatusBeforeCancel.RunExecution, earlier.RunExecution);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
            }
        }

        using var rootStatusAfterCancelResponse = await SendAsync(
            gateway.Client, HttpMethod.Get, rootStatusPath, boundToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, rootStatusAfterCancelResponse.StatusCode);
        var rootStatusAfterCancelBody =
            await rootStatusAfterCancelResponse.Content.ReadAsStringAsync();
        var rootStatusAfterCancel = JsonSerializer.Deserialize<SessionStatusSnapshot>(
            rootStatusAfterCancelBody, OrchestratorJsonOptions);
        Assert.NotNull(rootStatusAfterCancel);
        Assert.Equal(rootStatusBeforeCancel.Identity, rootStatusAfterCancel.Identity);
        Assert.Equal(rootStatusBeforeCancel.ParentSessionId, rootStatusAfterCancel.ParentSessionId);
        Assert.Equal(rootStatusBeforeCancel.RootSessionId, rootStatusAfterCancel.RootSessionId);
        Assert.Equal(rootStatusBeforeCancel.Kind, rootStatusAfterCancel.Kind);
        Assert.Equal(rootStatusBeforeCancel.Detached, rootStatusAfterCancel.Detached);
        Assert.Equal(CoordinationLifecycleState.Cancelled, rootStatusAfterCancel.Lifecycle);
        Assert.Equal(rootStatusBeforeCancel.ExecutionFence + 1, rootStatusAfterCancel.ExecutionFence);
        Assert.Equal(rootStatusBeforeCancel.StateVersion + 1, rootStatusAfterCancel.StateVersion);
        Assert.Equal(rootStatusBeforeCancel.Activity, rootStatusAfterCancel.Activity);
        Assert.Equal(rootStatusBeforeCancel.ActivityUnavailableCode, rootStatusAfterCancel.ActivityUnavailableCode);
        Assert.Equal(rootStatusBeforeCancel.RuntimeEffectsState, rootStatusAfterCancel.RuntimeEffectsState);
        Assert.Equal(
            rootStatusBeforeCancel.RuntimeEffectsUnavailableCode,
            rootStatusAfterCancel.RuntimeEffectsUnavailableCode);
        Assert.Equal(rootStatusBeforeCancel.InterruptionIntent, rootStatusAfterCancel.InterruptionIntent);
        Assert.Empty(rootStatusAfterCancel.Blockers);
        Assert.Null(rootStatusAfterCancel.ExecutionRecovery);
        Assert.Equal("cancelled", rootStatusAfterCancel.RunExecution.State);
        Assert.Equal(
            rootStatusBeforeCancel.RunExecution.StateVersion + 1,
            rootStatusAfterCancel.RunExecution.StateVersion);
        using (var rootJson = JsonDocument.Parse(rootStatusAfterCancelBody))
            Assert.False(rootJson.RootElement.TryGetProperty("executionRecovery", out _));

        if (hasLegacyChild)
        {
            Assert.NotNull(childStatusBeforeCancel);
            using var childStatusAfterCancelResponse = await SendAsync(
                gateway.Client, HttpMethod.Get, childStatusPath, boundToken, [TenantId]);
            Assert.Equal(HttpStatusCode.OK, childStatusAfterCancelResponse.StatusCode);
            var childStatusAfterCancel = await childStatusAfterCancelResponse.Content
                .ReadFromJsonAsync<SessionStatusSnapshot>(OrchestratorJsonOptions);
            Assert.NotNull(childStatusAfterCancel);
            Assert.Equal(childStatusBeforeCancel.Identity, childStatusAfterCancel.Identity);
            Assert.Equal(childStatusBeforeCancel.ParentSessionId, childStatusAfterCancel.ParentSessionId);
            Assert.Equal(childStatusBeforeCancel.RootSessionId, childStatusAfterCancel.RootSessionId);
            Assert.Equal(childStatusBeforeCancel.Kind, childStatusAfterCancel.Kind);
            Assert.Equal(childStatusBeforeCancel.Detached, childStatusAfterCancel.Detached);
            Assert.Equal(CoordinationLifecycleState.Cancelled, childStatusAfterCancel.Lifecycle);
            Assert.Equal(childStatusBeforeCancel.ExecutionFence + 1, childStatusAfterCancel.ExecutionFence);
            Assert.Equal(childStatusBeforeCancel.StateVersion + 1, childStatusAfterCancel.StateVersion);
            Assert.Equal(childStatusBeforeCancel.Activity, childStatusAfterCancel.Activity);
            Assert.Equal(
                childStatusBeforeCancel.ActivityUnavailableCode,
                childStatusAfterCancel.ActivityUnavailableCode);
            Assert.Equal(childStatusBeforeCancel.RuntimeEffectsState, childStatusAfterCancel.RuntimeEffectsState);
            Assert.Equal(
                childStatusBeforeCancel.RuntimeEffectsUnavailableCode,
                childStatusAfterCancel.RuntimeEffectsUnavailableCode);
            Assert.Equal(childStatusBeforeCancel.InterruptionIntent, childStatusAfterCancel.InterruptionIntent);
            Assert.Empty(childStatusAfterCancel.Blockers);
            Assert.Null(childStatusAfterCancel.ExecutionRecovery);
            Assert.Equal("cancelled", childStatusAfterCancel.RunExecution.State);
        }

        using (var wrongRunStatus = await SendAsync(
            gateway.Client,
            HttpMethod.Get,
            $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}-wrong/coordination/sessions/{pending.RootSessionId}/status",
            boundToken,
            [TenantId]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongRunStatus.StatusCode);
        }
        using (var terminal = await CancelChatAsync(contributorToken, "terminal-cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
            var receipt = await terminal.Content.ReadFromJsonAsync<CancelProjectRunResponse>(OrchestratorJsonOptions);
            Assert.NotNull(receipt);
            Assert.True(receipt.AlreadyTerminal);
        }
        Assert.Equal(1, runtimeRunStartOwners.UniquePlacementCount);
        Assert.Equal(hasLegacyChild ? 0 : 2, runtimeRunStartOwners.CleanupCount);
        await RevokeRoleAsync(
            projects.PrivilegedFixtureDataSource,
            orchestratorAssignment.AssignmentId,
            orchestratorAssignment.Revision);
        using (var revokedStatus = await SendAsync(
            gateway.Client, HttpMethod.Get, rootStatusPath, boundToken, [TenantId]))
            Assert.Equal(HttpStatusCode.Forbidden, revokedStatus.StatusCode);

        // Exercise the production Gateway and both fresh Projects checks for each cancellation.
        async Task<HttpResponseMessage> CancelChatAsync(string token, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/projects/{project.ProjectId}/runs/{pending.RunId}/cancel")
            {
                Content = JsonContent.Create(new CancelProjectRunRequest()),
            };
            AddBearerAndTenant(request, token, TenantId);
            request.Headers.Add("Idempotency-Key", key);
            return await gateway.Client.SendAsync(request);
        }

        async Task<HttpResponseMessage> SendRunMessageAsync(
            string token,
            string path,
            string? key,
            string text,
            string? targetSessionId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new ProjectRunMessageRequest(text, targetSessionId)),
            };
            AddBearerAndTenant(request, token, TenantId);
            if (key is not null)
                request.Headers.Add("Idempotency-Key", key);
            return await gateway.Client.SendAsync(request);
        }
    }

    [Fact]
    public async Task GenuineP1SetupAdmissionStartsOnceAndKeepsTheInitialRunTurnPending()
    {
        var signingKey = new X509SecurityKey(_runBindingSigningCertificate);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(_connectionString, signingKey);
        var p1ActorToken = await IssueTokenAsync(
            "projects.admin projects.orchestrator",
            [TenantId],
            "p1-runtime-start-actor",
            null,
            null,
            ["platform_admin"]);
        var actorId = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(p1ActorToken).Claims, "sub");
        var membership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, actorId, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            membership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            membership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        using (var defaults = await SendJsonAsync(
            projects.Client,
            HttpMethod.Put,
            "/api/platform/runtime-defaults/",
            p1ActorToken,
            new UpdatePlatformRuntimeDefaultsRequest
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
                        MaxPromptTokens = 20_000
                    }
                }
            }))
            await AssertStatusAsync(defaults, HttpStatusCode.OK);

        var invocationId = Guid.NewGuid();
        using var issueSetup = new HttpRequestMessage(
            HttpMethod.Post, $"/api/p1/invocations/{invocationId:D}/setup");
        AddBearerAndTenant(issueSetup, p1ActorToken, TenantId);
        using var setupResponse = await projects.Client.SendAsync(issueSetup);
        await AssertStatusAsync(setupResponse, HttpStatusCode.OK);
        var setup = await ReadJsonAsync<ProjectSetupReceipt>(setupResponse);

        var ownerSchema = "p1_start_owner_" + Guid.NewGuid().ToString("N");
        var eventsSchema = "p1_start_events_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        using var serviceEnvironment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ConnectionStrings__Orchestrator"] = _connectionString,
            ["Orchestrator__Database__WorkloadIdentity__TenantId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__ClientId"] = Guid.NewGuid().ToString(),
            ["Orchestrator__Database__WorkloadIdentity__TokenFilePath"] = TestTokenFile,
            ["Orchestrator__Schema"] = ownerSchema,
            ["Orchestrator__RuntimeRegistration__EnvironmentOwnerAddress"] = "https://environment.test/",
            ["Orchestrator__RuntimeUsage__BrokerOwnerAddress"] = "https://broker.test/",
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
        await CoordinationOwnerMigrator.MigrateAsync(_nativeDataSource, ownerSchema);
        await EventsHost::Agentweaver.EventsAndSessions.EventsAndSessionsMigrator
            .MigrateAsync(_nativeDataSource, eventsSchema);
        EventsIntegrationFactory? eventsReference = null;
        await using var orchestratorFactory = new OrchestratorIntegrationFactory(
            _connectionString,
            ownerSchema,
            signingKey,
            projects.CreateHandler,
            () => eventsReference!.Server.CreateHandler(),
            environmentHandler: () => eventsReference!.Server.CreateHandler(),
            runtimeUsageHandler: () => eventsReference!.Server.CreateHandler());
        await using var eventsFactory = new EventsIntegrationFactory(
            _connectionString,
            eventsSchema,
            signingKey,
            projects.CreateHandler,
            () => orchestratorFactory.Server.CreateHandler());
        eventsReference = eventsFactory;
        using var orchestrator = orchestratorFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://orchestrator.test/")
        });

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/projects/{setup.ProjectId}/runs")
        {
            Content = JsonContent.Create(new CreateProjectRunRequest(
                "Start from a genuine P1 setup.",
                setup.ProjectConfigurationRevision,
                P1SetupInvocationId: invocationId))
        };
        AddBearerAndTenant(createRequest, p1ActorToken, TenantId);
        createRequest.Headers.Add("Idempotency-Key", "p1-runtime-start");
        using var created = await orchestrator.SendAsync(createRequest);
        await AssertStatusAsync(created, HttpStatusCode.Created);
        var pending = await ReadJsonAsync<CreateProjectRunResponse>(created);
        Assert.Equal("awaiting_binding", pending.State);
        await CreateRunBindingGrantAsync(actorId, setup.ProjectId, pending.RunId);
        var runToken = await IssueTokenAsync(
            "projects.orchestrator",
            [TenantId],
            "p1-runtime-start-actor",
            setup.ProjectId,
            pending.RunId,
            ["orchestrator"]);

        var runPath = $"/api/projects/{setup.ProjectId}/runs/{pending.RunId}/start";
        using var started = await SendAsync(orchestrator, HttpMethod.Post, runPath, runToken, [TenantId]);
        await AssertStatusAsync(started, HttpStatusCode.OK);
        var response = await ReadJsonAsync<StartProjectRunResponse>(started);
        Assert.Equal(pending.RootSessionId, response.RootSessionId);
        Assert.Equal("pending", response.RuntimeStartup?.State);
        Assert.Equal("accepted", response.RuntimeStartup?.Step);

        var replayResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            SendAsync(orchestrator, HttpMethod.Post, runPath, runToken, [TenantId])));
        try
        {
            foreach (var replay in replayResponses)
            {
                await AssertStatusAsync(replay, HttpStatusCode.OK);
                var replayed = await ReadJsonAsync<StartProjectRunResponse>(replay);
                Assert.Equal(response.RootSessionId, replayed.RootSessionId);
            }
        }
        finally
        {
            foreach (var replay in replayResponses)
                replay.Dispose();
        }

        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using var counts = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{ownerSchema}".runtime_run_start_intents
                 WHERE project_id = @project AND run_id = @run AND root_session_id = @root),
                (SELECT count(*) FROM "{ownerSchema}".coordination_messages
                 WHERE project_id = @project AND run_id = @run
                   AND sender_session_id = @root AND idempotency_key = @firstMessageKey),
                (SELECT count(*) FROM "{ownerSchema}".coordination_sessions
                 WHERE project_id = @project AND run_id = @run
                   AND lifecycle_state = 'active'),
                (SELECT logical_turn_ordinal FROM "{ownerSchema}".coordination_sessions
                 WHERE project_id = @project AND run_id = @run AND session_id = @root)
            """, connection);
        counts.Parameters.AddWithValue("project", setup.ProjectId);
        counts.Parameters.AddWithValue("run", pending.RunId);
        counts.Parameters.AddWithValue("root", pending.RootSessionId);
        counts.Parameters.AddWithValue("firstMessageKey", $"{pending.RunId}:first");
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
    }

    private sealed class ProjectsSelectionBarrierHandler(
        HttpMessageHandler innerHandler,
        ControlledRequestBarrier barrier,
        ProjectsSelectionResponseChange selectionChange) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await barrier.PauseIfMatchedAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await selectionChange.MutateSecondResponseAsync(
                request, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RuntimeRunStartIntent?> WaitForCleanupStateAsync(
        CoordinationRuntimeRunStartStore store,
        string tenantId,
        string projectId,
        string runId,
        string expectedState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        RuntimeRunStartIntent? intent = null;
        while (!timeout.IsCancellationRequested)
        {
            intent = await store.FindCleanupIntentAsync(
                tenantId, projectId, runId, timeout.Token);
            if (intent?.State == expectedState)
                return intent;
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
        return intent;
    }

    private static async Task<RuntimeRunStartIntent> WaitForRuntimeStartIntentAsync(
        CoordinationRuntimeRunStartStore store,
        RuntimeOwnerContext owner,
        string expectedState,
        IEnumerable<string> diagnostics)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(190));
        var intent = await store.EnsureIntentAsync(owner, timeout.Token);
        try
        {
            while (intent.State != expectedState)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
                intent = await store.ReadAsync(intent, timeout.Token)
                    ?? throw new Xunit.Sdk.XunitException("The durable runtime-start intent disappeared.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var current = await store.ReadAsync(intent, CancellationToken.None) ?? intent;
            throw new Xunit.Sdk.XunitException(
                $"Runtime start did not reach '{expectedState}'. Current state='{current.State}', " +
                $"step='{current.CurrentStep}', failure='{current.FailureCode}'. " +
                $"Owner calls: {string.Join(" | ", diagnostics)}.");
        }
        return intent;
    }

    private sealed class RuntimeRunStartOwnerProbe
    {
        private readonly ConcurrentDictionary<string, RuntimeRunStartPlacementReceipt> _placements =
            new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<CleanupCall> _cleanupCalls = new();
        private readonly ConcurrentQueue<string> _observedPaths = new();
        private readonly ConcurrentQueue<string> _readinessDiagnostics = new();
        private readonly ConcurrentQueue<string> _provisionerFailures = new();
        private readonly ConcurrentQueue<string> _handlerFailures = new();
        private readonly ConcurrentQueue<RuntimeRunStartPlacementRequest> _placementAttempts = new();
        private readonly CapturingLoggerProvider _loggerProvider = new();
        private SandboxStartupPhaseObservation[] _activatedStartupPhases = [];
        private readonly TaskCompletionSource<RuntimeRunStartPlacementReceipt> _firstPlacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _cleanupSignal = new(0);
        private int _cleanupCount;
        private int _dropChildPlacementReply;
        private bool _replayPlacementToOwner;

        public int UniquePlacementCount => _placements.Count;
        public int CleanupCount => Volatile.Read(ref _cleanupCount);
        public RuntimeRunStartPlacementReceipt FirstPlacement => _placements.Values.First();
        public IReadOnlyCollection<string> ObservedPaths => _observedPaths.ToArray();
        public IEnumerable<string> ReadinessDiagnostics => _readinessDiagnostics;
        public IReadOnlyCollection<string> ProvisionerFailures => _provisionerFailures.ToArray();
        public IReadOnlyCollection<string> HandlerFailures => _handlerFailures.ToArray();
        public IReadOnlyCollection<RuntimeRunStartPlacementRequest> PlacementAttempts => _placementAttempts.ToArray();
        public IReadOnlyCollection<string> LoggerMessages => _loggerProvider.Messages;
        public Microsoft.Extensions.Logging.ILoggerProvider LoggerProvider => _loggerProvider;

        private Func<RuntimeOwnerContext, RuntimeRunStartPlacementRequest, HttpRequestMessage, CancellationToken,
            Task<RuntimeRunStartPlacementReceipt>>? _provisioner;
        private readonly SemaphoreSlim _placementLock = new(1, 1);

        public HttpMessageHandler CreateHandler(Func<HttpMessageHandler> inner, bool onlyChildStart = false) =>
            new Handler(inner(), this, onlyChildStart);

        public void DropNextChildPlacementReply() => Interlocked.Exchange(ref _dropChildPlacementReply, 1);

        public void SetProvisioner(
            Func<RuntimeOwnerContext, RuntimeRunStartPlacementRequest, HttpRequestMessage, CancellationToken,
                Task<RuntimeRunStartPlacementReceipt>> provisioner, bool replayToOwner = false)
        {
            _provisioner = provisioner;
            _replayPlacementToOwner = replayToOwner;
        }

        public async Task<RuntimeRunStartPlacementReceipt> WaitForPlacementAsync()
        {
            try
            {
                return await _firstPlacement.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Startup placement was not returned. Provisioner failures: " +
                    $"{string.Join(" | ", ProvisionerFailures)}. Handler failures: " +
                    $"{string.Join(" | ", HandlerFailures)}. Owner calls: " +
                    $"{string.Join(" | ", ObservedPaths)}. Logs: {string.Join(" | ", LoggerMessages)}.");
            }
        }

        public async Task<CleanupCall> WaitForCleanupAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                while (_cleanupCalls.Count < count)
                    await _cleanupSignal.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Cleanup call {count} was not observed. Recent paths: " +
                    $"{string.Join(" | ", ObservedPaths.TakeLast(12))}. Recent logs: " +
                    $"{string.Join(" | ", LoggerMessages.TakeLast(12))}.");
            }
            return _cleanupCalls.ToArray()[count - 1];
        }

        private async Task<RuntimeRunStartPlacementReceipt> GetPlacementAsync(
            RuntimeOwnerContext owner,
            RuntimeRunStartPlacementRequest startRequest,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            startRequest.ValidateFor(owner);
            var key = $"{owner.ProjectId}:{owner.RunId}:{owner.SessionId}:{startRequest.BootstrapOperationId:N}";
            if (_placements.TryGetValue(key, out var existing) && !_replayPlacementToOwner)
                return existing;
            await _placementLock.WaitAsync(cancellationToken);
            try
            {
                if (_placements.TryGetValue(key, out existing) && !_replayPlacementToOwner)
                    return existing;
                var provisioner = _provisioner
                    ?? throw new InvalidOperationException("The startup placement owner is not configured.");
                RuntimeRunStartPlacementReceipt placement;
                try
                {
                    placement = await provisioner(owner, startRequest, request, cancellationToken);
                }
                catch (Exception exception)
                {
                    _provisionerFailures.Enqueue(exception.ToString());
                    throw;
                }
                if (existing is not null)
                    Assert.Equal(existing, placement);
                _placements[key] = placement;
                _firstPlacement.TrySetResult(placement);
                return placement;
            }
            finally
            {
                _placementLock.Release();
            }
        }

        private static HttpResponseMessage JsonResponse<T>(T value, HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value, options: OrchestratorJsonOptions),
                RequestMessage = request
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        }

        public sealed record CleanupCall(
            RuntimeRunStartCleanupProjection Projection,
            RuntimeRunStartPlacementReceipt Placement,
            string? Bearer);

        private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
        {
            private readonly ConcurrentQueue<string> _messages = new();

            public IReadOnlyCollection<string> Messages => _messages.ToArray();

            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
                new CapturingLogger(categoryName, _messages);

            public void Dispose()
            {
            }

            private sealed class CapturingLogger(
                string category,
                ConcurrentQueue<string> messages) : Microsoft.Extensions.Logging.ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
                    logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

                public void Log<TState>(
                    Microsoft.Extensions.Logging.LogLevel logLevel,
                    Microsoft.Extensions.Logging.EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    if (IsEnabled(logLevel))
                        messages.Enqueue($"{category}: {formatter(state, exception)}");
                }
            }
        }

        private sealed class Handler(
            HttpMessageHandler inner, RuntimeRunStartOwnerProbe owner, bool onlyChildStart)
            : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner._observedPaths.Enqueue($"{request.Method} {request.RequestUri}");
                var path = request.RequestUri?.AbsolutePath;
                if (path != "/internal/runtime/run-start/child-placement" &&
                    (onlyChildStart || path is not (
                        "/internal/runtime/run-start/placement" or
                        "/internal/runtime/run-start/terminal-cleanup")))
                {
                    var response = await base.SendAsync(request, cancellationToken);
                    if (!onlyChildStart)
                        await owner.ObserveReadinessAsync(request, response, cancellationToken);
                    return response;
                }

                try
                {
                    var input = await request.Content!.ReadFromJsonAsync<RuntimeRunStartPlacementRequest>(
                        OrchestratorJsonOptions, cancellationToken);
                    Assert.NotNull(input);
                    input.Validate();
                    if (input.Owner is { } startingOwner)
                    {
                        owner._placementAttempts.Enqueue(input);
                        var startedPlacement = await owner.GetPlacementAsync(
                            startingOwner, input, request, cancellationToken);
                        if (path == "/internal/runtime/run-start/child-placement" &&
                            Interlocked.Exchange(ref owner._dropChildPlacementReply, 0) == 1)
                            throw new HttpRequestException("The child placement reply was lost.");
                        return JsonResponse(startedPlacement, request);
                    }

                    var projection = Assert.IsType<RuntimeRunStartCleanupProjection>(input.Cleanup);
                    var placement = projection.ReceiptLinks.TryGetProperty("placement", out var savedPlacement)
                        ? savedPlacement.Deserialize<RuntimeRunStartPlacementReceipt>(OrchestratorJsonOptions)
                            ?? throw new InvalidOperationException("The saved startup placement is empty.")
                        : owner.FirstPlacement;
                    if (projection.ReceiptLinks.TryGetProperty("cleanupAttempt", out var savedAttempt))
                    {
                        var recoveredAttempt = savedAttempt.Deserialize<RuntimeRunStartCleanupAttempt>(
                            OrchestratorJsonOptions);
                        Assert.NotNull(recoveredAttempt);
                        Assert.Equal(placement.SandboxOperationId, recoveredAttempt.SandboxOperationId);
                        Assert.Equal(placement.SandboxResource.Generation, recoveredAttempt.ResourceGeneration);
                        Assert.Equal(placement.ProviderFencingGeneration, recoveredAttempt.ProviderFencingGeneration);
                    }
                    var bearer = request.Headers.Authorization?.Parameter;
                    var call = new CleanupCall(projection, placement, bearer);
                    owner._cleanupCalls.Enqueue(call);
                    var attempt = Interlocked.Increment(ref owner._cleanupCount);
                    owner._cleanupSignal.Release();
                    return JsonResponse(new RuntimeRunStartCleanupReceipt(
                        projection.ContractVersion,
                        projection.IntentId,
                        placement.SandboxOperationId,
                        placement.SandboxResource.Generation,
                        placement.ProviderFencingGeneration,
                        placement.SandboxIdempotencyKey,
                        attempt == 1 ? "releasing" : "released",
                        attempt == 1 ? "cleanup_pending" : "cleanup_complete")
                    {
                        SandboxResource = projection.ContractVersion == 2 ? placement.SandboxResource : null
                    }, request);
                }
                catch (Exception exception)
                {
                    owner._handlerFailures.Enqueue(exception.ToString());
                    throw;
                }
            }
        }

        private async Task ObserveReadinessAsync(
            HttpRequestMessage request,
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode ||
                request.RequestUri is not { Host: "environment.test", AbsolutePath: var path } ||
                !(path.EndsWith("/activate", StringComparison.Ordinal) ||
                  path.EndsWith("/readiness", StringComparison.Ordinal)))
                return;

            var contentType = response.Content.Headers.ContentType;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            response.Content.Dispose();
            response.Content = new ByteArrayContent(body);
            if (contentType is not null)
                response.Content.Headers.ContentType = contentType;

            if (path.EndsWith("/activate", StringComparison.Ordinal))
            {
                var activated = JsonSerializer.Deserialize<RuntimeHostReadinessReceipt>(
                    body, OrchestratorJsonOptions)
                    ?? throw new InvalidOperationException("Environment returned an empty Host activation receipt.");
                _activatedStartupPhases = activated.StartupPhases.ToArray();
                _readinessDiagnostics.Enqueue(
                    $"Host phases={string.Join(",", _activatedStartupPhases.Select(phase => phase.Phase))}.");
                return;
            }

            var readiness = JsonSerializer.Deserialize<EnvironmentRuntimeReadinessContext>(
                body, OrchestratorJsonOptions)
                ?? throw new InvalidOperationException("Environment returned an empty readiness context.");
            var placement = FirstPlacement;
            var current = readiness.Placement;
            var observation = readiness.Observation;
            var pod = observation.PodObservation;
            var providerPhases = observation.StartupPhases;
            var now = DateTimeOffset.UtcNow;
            var phasesMatch = providerPhases.All(phase => _activatedStartupPhases.Contains(phase));
            const string expectedMountPath = "/workspace";
            var workspaceMountMatches = readiness.WorkspaceMountPath == expectedMountPath;
            var podWorkspaceMatches =
                pod?.WorkspaceAttachment is { ReadOnly: false, MountPath: expectedMountPath };
            _readinessDiagnostics.Enqueue(
                $"Readiness checks: placement={current.EnvironmentId == placement.EnvironmentId && current.Resource == placement.SandboxResource && current.LifecycleGeneration == placement.LifecycleGeneration && current.LeaseRevision == placement.LeaseRevision && current.CurrentFencingGeneration == placement.CurrentFencingGeneration && current.ProviderFencingGeneration == placement.ProviderFencingGeneration && current.LeaseExpiresAt == placement.LeaseExpiresAt}, " +
                $"leaseLive={current.LeaseExpiresAt > now}, workspace={workspaceMountMatches}, " +
                $"hostPhases={_activatedStartupPhases.Length}, providerPhases={providerPhases.Length}, phaseSubset={phasesMatch}, " +
                $"state={observation.State}, network={observation.VerifiedNetworkGeneration == placement.NetworkPolicyGeneration}, " +
                $"workspaceVerified={observation.WorkspaceAttachmentVerified}, isolationVerified={observation.VmIsolationVerified}, " +
                $"provision={observation.ProvisionOperationId == placement.SandboxOperationId}, " +
                $"runtimeEndpoint={observation.RuntimeEndpoint is not null}, pod={pod is not null}, " +
                $"podClaim={pod?.ClaimUid == placement.SandboxResource.ResourceId}, " +
                $"podProvision={pod?.ProvisionOperationId == placement.SandboxOperationId}, " +
                $"podWorkspace={podWorkspaceMatches}, " +
                $"podEndpoint={pod is not null && observation.RuntimeEndpoint?.PodUid == pod.PodUid}.");
        }
    }

    private sealed class RuntimeRunStartEnvironmentHolder : IAsyncDisposable
    {
        private RuntimePlacementTestServer? _server;

        public void Set(RuntimePlacementTestServer server)
        {
            if (Interlocked.CompareExchange(ref _server, server, null) is not null)
                throw new InvalidOperationException("The startup Environment host was already created.");
        }

        public Task ExpireCurrentLeaseAsync() =>
            (_server ?? throw new InvalidOperationException("The startup Environment host was not created."))
                .ExpireCurrentLeaseAsync();

        public async ValueTask DisposeAsync()
        {
            var server = Interlocked.Exchange(ref _server, null);
            if (server is not null)
                await server.DisposeAsync();
        }
    }

    private sealed class RuntimeRunStartDeferredHandler(Task<HttpMessageHandler> ready) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var inner = await ready.WaitAsync(cancellationToken);
            using var invoker = new HttpMessageInvoker(inner, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ProjectsSelectionResponseChange
    {
        private string? _path;
        private int _remaining;

        public void ChangeSecondResponse(string path)
        {
            Volatile.Write(ref _path, path);
            Volatile.Write(ref _remaining, 2);
        }

        public async Task<HttpResponseMessage> MutateSecondResponseAsync(
            HttpRequestMessage request,
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != Volatile.Read(ref _path) ||
                Interlocked.Decrement(ref _remaining) != 0 ||
                response.StatusCode != HttpStatusCode.OK)
                return response;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var selection = JsonNode.Parse(body)?.AsObject()
                ?? throw new InvalidOperationException("Projects returned a selection that is not a JSON object.");
            selection["contextRevision"] = "status-recheck-changed";
            response.Content.Dispose();
            response.Content = new StringContent(selection.ToJsonString(), Encoding.UTF8, "application/json");
            Volatile.Write(ref _path, null);
            return response;
        }
    }
}
