extern alias EnvironmentService;
extern alias OrchestratorHost;
extern alias ProjectsConfig;
extern alias EventsHost;

using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using EnvironmentService::Agentweaver.Environment;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private static RuntimeModelBindingsResolver AzureByokModelBindings() => new("azure-byok-bindings-v1",
        new Dictionary<string, RuntimeModelBinding>
        {
            ["platform-model"] = new("controlled-model", ModelSourceMode.Byok,
                new("azure", new Uri("https://byok.test/"), "responses", "2025-04-01-preview"))
            {
                PromptCapacityTokens = 20000
            }
        });

    [Fact]
    public Task AzureByokNativeUsageUsesBrokerCoreAndEventsWithExactDispatchProvenance() =>
        RunBrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending(
            false, null, false, OwnerRunFailureState.Indeterminate, azureByok: true);

    [Theory]
    [InlineData("sdk-preparation-grant")]
    [InlineData("sdk-preparation-lease")]
    [InlineData("sdk-preparation-expiry")]
    [InlineData("head")]
    public Task AzureByokAuthorityLossPreventsNativeOrLedgerEffects(string loss) =>
        RunBrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending(
            false, loss, false, OwnerRunFailureState.Indeterminate, azureByok: true);

    [Theory]
    [InlineData("grant")]
    [InlineData("lease")]
    [InlineData("expiry")]
    public Task AuthorityLossDuringSdkPreparationPreventsNativeSessionCreation(string loss) =>
        BrokerIssuedRunTokenRegistersSessionsDeliversAtTurnBoundaryAndKeepsGatePending(
            false, $"sdk-preparation-{loss}");

    private async Task VerifyRunBoundRuntimeRegistrationWithCurrentEnvironmentAsync(
        string ownerSchema, SecurityKey signingKey, ProjectsConfigResourceServer projects,
        EventsIntegrationFactory events, string runToken, RuntimeOwnerContext owner, Guid membershipId,
        ICoordinatorSandboxResourceProvider sandboxProvider, bool revokeSourceBeforeSdk, string? sourceLoss,
        ControlledCopilotConnection copilotConnection, string connectionOwnerToken,
        Func<bool, Task> setHistoricalReadAuthority, RecordingSecretRedemption? byokSecrets = null,
        SkillRuntimeContentV1? expectedAcceptedSkill = null, string? unassignedAgentId = null,
        ProjectsConfigResourceServer? skillWriter = null, bool stopAfterRegistration = false)
    {
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membershipId,
            ProjectAuthorityResourceType.Project, owner.ProjectId, ProjectAuthorityRole.Owner);
        using var selectionResponse = await SendAsync(
            projects.Client, HttpMethod.Get,
            $"/api/projects/{owner.ProjectId}/runs/{owner.RunId}/selection", runToken, [owner.TenantId]);
        await AssertStatusAsync(selectionResponse, HttpStatusCode.OK);
        var selectionBytes = await selectionResponse.Content.ReadAsByteArrayAsync();
        var selection = JsonSerializer.Deserialize<ProjectRunSelectionResponse>(
            selectionBytes, CoordinationJsonOptions)!;
        var candidate = Assert.Single(Assert.Single(
            selection.Providers, provider => provider.Seam == ProviderSeam.Sandbox).Candidates);
        var networkCandidate = Assert.Single(
            Assert.Single(selection.Providers, provider => provider.Seam == ProviderSeam.NetworkPolicy).Candidates,
            provider => provider.Layer == NetworkPolicyLayer.L3L4);
        Assert.Equal(CiliumEgressPolicyAdapter.ProviderId, networkCandidate.ProviderId);
        using var selectionDocument = JsonDocument.Parse(selectionBytes);
        var environmentOwner = new EnvironmentOwnerIdentity(
            owner.TenantId, owner.ProjectId, owner.RunId, "runtime-environment");
        var routes = new Dictionary<string, Func<HttpMessageHandler>>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        TraceNativeStage(failures, $"Environment startup begin ({sourceLoss ?? "positive"}).");
        await using var environment = await RuntimePlacementTestServer.StartAsync(
            _connectionString, signingKey, projects.CreateHandler, environmentOwner,
            candidate, selectionDocument.RootElement.Clone(), () => new RuntimeServiceRouter(routes, failures));
        TraceNativeStage(failures, "Environment startup completed.");
        routes.Add("environment.test", environment.CreateHandler);
        using var runtimeConfiguration = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["Orchestrator__RuntimeRegistration__EnvironmentOwnerAddress"] = "https://environment.test/",
            ["Orchestrator__RuntimeUsage__BrokerOwnerAddress"] = "https://broker.test/"
        });
        using var environmentClient = new HttpClient(environment.CreateHandler())
        {
            BaseAddress = new Uri("https://environment.test/")
        };
        var placementPath = $"/api/projects/{owner.ProjectId}/runs/{owner.RunId}" +
            $"/environments/{environmentOwner.EnvironmentId}/sandbox/v1/placement";
        using var placementResponse = await SendAsync(
            environmentClient, HttpMethod.Get, placementPath, runToken, [owner.TenantId]);
        await AssertStatusAsync(placementResponse, HttpStatusCode.Forbidden);
        Assert.True(placementResponse.Headers.CacheControl?.NoStore);
        Assert.Contains("project_write_not_authorized", await placementResponse.Content.ReadAsStringAsync());
        using var internalPlacement = await SendAsync(environmentClient, HttpMethod.Get,
            placementPath.Replace("/v1/placement", "/v1/internal/placement", StringComparison.Ordinal),
            runToken, [owner.TenantId]);
        await AssertStatusAsync(internalPlacement, HttpStatusCode.OK);
        Assert.True(internalPlacement.Headers.CacheControl?.NoStore);
        await using var factory = new OrchestratorIntegrationFactory(
            _connectionString, ownerSchema, signingKey, projects.CreateHandler,
            () => events.Server.CreateHandler(), sandboxProvider: sandboxProvider,
            providerCatalog: CreateSourceControlProviderCatalog(), policyOptions: SourceControlMergePolicyOptions(),
            environmentHandler: () => new RuntimeServiceRouter(routes, failures),
            runtimeUsageHandler: () => new RuntimeServiceRouter(routes, failures));
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://orchestrator.test/"), AllowAutoRedirect = false
        });
        routes.Add("orchestrator.test", () => factory.Server.CreateHandler());
        var enrollmentPath = $"/internal/projects/{owner.ProjectId}/runs/{owner.RunId}" +
            $"/coordination/sessions/{owner.SessionId}/runtime-registrations";
        using var unavailable = await SendAsync(
            client, HttpMethod.Get, enrollmentPath, runToken, [owner.TenantId]);
        await AssertStatusAsync(unavailable, HttpStatusCode.Conflict);
        Assert.Contains("runtime_registration_unavailable",
            await unavailable.Content.ReadAsStringAsync());
        TraceNativeStage(failures, "Runtime registration begin.");
        using var response = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath,
            runToken, new RegisterRuntimeRequest(environmentOwner.EnvironmentId, "runtime-profile"));
        TraceNativeStage(failures, $"Runtime registration returned {(int)response.StatusCode}.");
        await AssertStatusAsync(response, HttpStatusCode.OK);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var registration = await response.Content.ReadFromJsonAsync<RuntimeRegistration>(CoordinationJsonOptions);
        Assert.NotNull(registration);
        Assert.Equal(owner.ActorId, registration.Binding.ActorId);
        Assert.Equal(owner.TurnId, registration.Binding.TurnId);
        Assert.Equal(owner.ModelSelectionReference, registration.Binding.ModelSelectionReference);
        Assert.Equal(owner.ModelCredentialReference, registration.Binding.ModelCredentialReference);
        Assert.Equal(owner.ModelConnectionId, registration.Binding.ModelConnectionId);
        Assert.Equal(owner.ModelConnectionScope, registration.Binding.ModelConnectionScope);
        Assert.Equal(byokSecrets is null ? ModelSourceMode.HostedCopilot : ModelSourceMode.Byok, owner.ModelSourceMode);
        Assert.Equal(owner.MaxPromptTokens, registration.Binding.MaxPromptTokens);
        Assert.Equal(owner.ModelBindingPin, registration.Binding.ModelBindingPin);
        Assert.Equal(owner.ModelSourceMode, registration.Binding.ModelSourceMode);
        Assert.Equal(owner.AcceptedSelectionHash, registration.Binding.AcceptedSelectionHash);
        Assert.Equal(environment.Lease.LeaseRevision, registration.Binding.EnvironmentLeaseRevision);
        Assert.Equal(environment.Lease.ProvisionedResource!.Resource.ProviderId,
            registration.Binding.PlacementProviderId);
        Assert.Equal(environment.Lease.ProvisionedResource.Resource.ResourceId, registration.Binding.PlacementUid);
        using var discovered = await SendAsync(
            client, HttpMethod.Get, enrollmentPath, runToken, [owner.TenantId]);
        await AssertStatusAsync(discovered, HttpStatusCode.OK);
        Assert.True(discovered.Headers.CacheControl?.NoStore);
        Assert.Equal(registration,
            await discovered.Content.ReadFromJsonAsync<RuntimeRegistration>(CoordinationJsonOptions));
        using var current = await SendAsync(client, HttpMethod.Get,
            $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}", runToken, [owner.TenantId]);
        await AssertStatusAsync(current, HttpStatusCode.OK);
        Assert.Equal(registration, await current.Content.ReadFromJsonAsync<RuntimeRegistration>(CoordinationJsonOptions));
        using var skills = await SendAsync(client, HttpMethod.Get,
            $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}/skills", runToken, [owner.TenantId]);
        await AssertStatusAsync(skills, HttpStatusCode.OK);
        Assert.True(skills.Headers.CacheControl?.NoStore);
        var skillProjection = await skills.Content.ReadFromJsonAsync<SkillRuntimeContentProjectionV1>(
            CoordinationJsonOptions);
        Assert.NotNull(skillProjection);
        Assert.Equal(registration.RuntimeInstanceId, skillProjection.RuntimeInstanceId);
        Assert.Equal(registration.Revision, skillProjection.RegistrationRevision);
        Assert.Equal(registration.Binding.ProjectConfigurationRevision, skillProjection.ProjectConfigurationRevision);
        Assert.Equal(registration.Binding.ExecutionFence, skillProjection.ExecutionFence);
        Assert.Equal(registration.Binding.TenantId, skillProjection.TenantId);
        Assert.Equal(registration.Binding.ProjectId, skillProjection.ProjectId);
        Assert.Equal(registration.Binding.RunId, skillProjection.RunId);
        Assert.Equal(registration.Binding.SessionId, skillProjection.SessionId);
        Assert.Equal(registration.Binding.AgentId, skillProjection.AgentId);
        Assert.Equal(registration.Binding.AcceptedSelectionHash, skillProjection.AcceptedSelectionHash);
        if (expectedAcceptedSkill is null)
            Assert.Empty(skillProjection.Skills);
        else
        {
            Assert.Equal("test-agent", registration.Binding.AgentId);
            AssertPinnedSkill(expectedAcceptedSkill, Assert.Single(skillProjection.Skills));
            using var acceptedRunSkills = await SendAsync(
                projects.Client,
                HttpMethod.Get,
                $"/api/projects/{owner.ProjectId}/runs/{owner.RunId}/agents/{registration.Binding.AgentId}/skills",
                runToken,
                [owner.TenantId]);
            await AssertStatusAsync(acceptedRunSkills, HttpStatusCode.OK);
            AssertPinnedSkill(
                expectedAcceptedSkill,
                Assert.Single(await ReadJsonAsync<SkillRuntimeContentV1[]>(acceptedRunSkills)));
            var provider = new RuntimeAcceptedSkillProvider(skillProjection);
            var descriptor = Assert.Single(await provider.ListSkillsAsync(CancellationToken.None));
            Assert.Equal(RuntimeAcceptedSkillProvider.RuntimeName(expectedAcceptedSkill.SkillId), descriptor.Name);
            Assert.Equal(expectedAcceptedSkill.Description, descriptor.Description);
            var resource = Assert.Single(expectedAcceptedSkill.Resources);
            Assert.Equal(
                $"{expectedAcceptedSkill.Instructions}\n\nBundled resources from revision " +
                $"{expectedAcceptedSkill.Revision}:\n- `{resource.RelativePath}`: " +
                $"`/workspace/{RuntimeAcceptedSkillProvider.ResourceDirectory(descriptor.Name)}/{resource.RelativePath}`",
                await provider.ReadSkillAsync(descriptor.Name, CancellationToken.None));
        }
        if (unassignedAgentId is not null)
        {
            using var activeUnassignedSkills = await SendAsync(
                projects.Client,
                HttpMethod.Get,
                $"/api/projects/{owner.ProjectId}/runs/{owner.RunId}/agents/{unassignedAgentId}/skills",
                runToken,
                [owner.TenantId]);
            await AssertStatusAsync(activeUnassignedSkills, HttpStatusCode.OK);
            Assert.Empty(await ReadJsonAsync<SkillRuntimeContentV1[]>(activeUnassignedSkills));
        }
        await using var source = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{ownerSchema}\".runtime_registration_heads", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        using var forged = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath, runToken,
            new { environmentOwner.EnvironmentId, ProfileId = "runtime-profile", DesiredModel = "foreign" });
        await AssertStatusAsync(forged, HttpStatusCode.BadRequest);
        if (stopAfterRegistration)
            return;
        await VerifyCurrentRuntimeDeliveryAndNativeSessionAsync(
            registration, signingKey, runToken, routes, failures, projects, events.Schema, ownerSchema,
            revokeSourceBeforeSdk, sourceLoss, environment, copilotConnection,
            connectionOwnerToken, setHistoricalReadAuthority, factory.Services, byokSecrets,
            expectedAcceptedSkill, skillWriter);
        TraceNativeStage(failures, "Runtime exercise and explicit cleanup completed.");
    }

    private static void AssertPinnedSkill(
        SkillRuntimeContentV1 expected,
        SkillRuntimeContentV1 actual)
    {
        Assert.Equal(expected.SkillId, actual.SkillId);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Instructions, actual.Instructions);
        Assert.Equal(expected.ContentDigest, actual.ContentDigest);
        Assert.Equal(expected.Resources.Length, actual.Resources.Length);
        for (var index = 0; index < expected.Resources.Length; index++)
        {
            Assert.Equal(expected.Resources[index].RelativePath, actual.Resources[index].RelativePath);
            Assert.Equal(expected.Resources[index].Sha256, actual.Resources[index].Sha256);
            Assert.Equal(
                expected.Resources[index].Content.ToArray(),
                actual.Resources[index].Content.ToArray());
        }
    }

    private async Task VerifyCurrentRuntimeDeliveryAndNativeSessionAsync(
        RuntimeRegistration registration, SecurityKey signingKey, string runToken,
        Dictionary<string, Func<HttpMessageHandler>> routes, ConcurrentQueue<string> failures,
        ProjectsConfigResourceServer projects, string eventsSchema, string ownerSchema, bool revokeSourceBeforeSdk,
        string? sourceLoss, RuntimePlacementTestServer environment, ControlledCopilotConnection copilotConnection,
        string connectionOwnerToken, Func<bool, Task> setHistoricalReadAuthority,
        IServiceProvider orchestratorServices, RecordingSecretRedemption? byokSecrets = null,
        SkillRuntimeContentV1? expectedAcceptedSkill = null,
        ProjectsConfigResourceServer? skillWriter = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var expiryTimeProvider = sourceLoss == "sdk-preparation-expiry"
            ? new RuntimeExpiryTimeProvider()
            : null;
        TimeProvider runtimeTimeProvider = expiryTimeProvider ?? TimeProvider.System;
        TraceNativeStage(failures, "Broker startup begin.");
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
                settings["IdentityBroker__RuntimeBootstrap__SourceLifetime"] =
                    sourceLoss == "sdk-preparation-expiry" ? "00:03:00" : "00:02:00";
                ConfigureRunBindingSettings(settings);
                copilotConnection.ConfigureSettings(settings);
            },
            configureServices: services =>
            {
                ConfigureRunBindingTlsIdentity(services);
                copilotConnection.ConfigureServices(services, projects.CreateHandler);
                if (byokSecrets is not null)
                {
                    services.RemoveAll<ISecretRedemption>();
                    services.AddSingleton<ISecretRedemption>(byokSecrets);
                }
                services.AddHttpClient(nameof(RuntimeRegistrationHttpClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new RuntimeServiceRouter(routes, failures));
                services.AddHttpClient(nameof(BrokerRuntimeBootstrapDeliveryClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new RuntimeServiceRouter(routes, failures));
            });
        using var broker = brokerFactory.CreateClient(new()
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://broker.test/")
        });
        TraceNativeStage(failures, "Broker startup completed.");
        routes.Add("broker.test", () => brokerFactory.Server.CreateHandler());
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? inspectOwnerResponse = null;
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? inspectRuntimeResponse =
            async (request, response, token) =>
            {
                if (request.RequestUri?.AbsolutePath != "/internal/runtime/model-session/redeem" ||
                    !response.IsSuccessStatusCode)
                    return;
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                var body = System.Text.Encoding.UTF8.GetString(bytes);
                Assert.DoesNotContain(copilotConnection.RefreshToken, body);
                Assert.DoesNotContain(ControlledCopilotConnection.ClientSecret, body);
                Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
                var redeemed = JsonSerializer.Deserialize<RuntimeModelCredentialResponse>(
                    bytes, CoordinationJsonOptions);
                Assert.NotNull(redeemed);
                Assert.Equal(byokSecrets?.Value ?? copilotConnection.AccessToken, redeemed.Value);
                Assert.Equal(registration.Binding.ModelConnectionId, redeemed.Receipt.ConnectionId);
                Assert.Equal(byokSecrets is null ? RuntimeModelCredentialKind.GitHubUserAccess :
                    RuntimeModelCredentialKind.ByokKey, redeemed.Receipt.CredentialKind);
                Assert.Equal(byokSecrets is null ? $"version-{copilotConnection.Writes}" : "model-v1",
                    redeemed.Receipt.CredentialReference.Version);
                Assert.True(redeemed.ExpiresAt <= redeemed.Receipt.ExpiresAt);
                Assert.True(response.Headers.CacheControl?.NoStore);
            };
        using var owners = new HttpClient(new RuntimeServiceRouter(routes, failures,
            (request, response, token) => inspectOwnerResponse?.Invoke(request, response, token) ?? Task.CompletedTask));
        var currentOwner = new RuntimeRegistrationHttpClient(owners, new("https://orchestrator.test/"));
        await using var sdk = new ControlledCopilotRuntime(TimeSpan.FromMinutes(5));
        sdk.Byok = byokSecrets is not null;
        sdk.SdkCredential = byokSecrets?.Value ?? copilotConnection.AccessToken;
        using var runtimeHttp = new HttpClient(new RuntimeServiceRouter(routes, failures,
            (request, response, token) => inspectRuntimeResponse?.Invoke(request, response, token) ?? Task.CompletedTask))
        {
            BaseAddress = broker.BaseAddress
        };
        await using var receiver = new RuntimeBootstrapReceiver(
            registration, currentOwner, broker, broker.BaseAddress!, TimeProvider.System);
        TraceNativeStage(failures, "Receiver host startup begin.");
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                AddJwtBearer(services, signingKey);
                services.AddAuthorization();
                services.ConfigureHttpJsonOptions(options =>
                    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                    endpoints.MapRuntimeBootstrapReceiver(registration.Binding.ConfigureEndpoint, receiver));
            });
        }).StartAsync();
        TraceNativeStage(failures, "Receiver host startup completed.");
        routes.Add(RuntimeReceiverHost, () => host.GetTestServer().CreateHandler());
        var bearer = new SecretCredential(runToken, registration.ExpiresAt);
        var primaryFailure = await Record.ExceptionAsync(async () =>
        {
            failures.Enqueue($"Before delivery: {elapsed.Elapsed} lease remaining " +
                $"{registration.ExpiresAt - DateTimeOffset.UtcNow}");
            output.WriteLine(failures.Last());
            var actor = new RuntimeActorAuthorization(bearer, registration.Binding.TenantId);
            var configuration = "{}"u8.ToArray();
            var input = new RuntimeBootstrapRequest(
                registration.RuntimeInstanceId, Guid.NewGuid(), RuntimeContractValidation.Hash(configuration));
            TraceNativeStage(failures, "Bootstrap delivery begin.");
            using var deliveryResponse = await SendJsonAsync(
                broker, HttpMethod.Post, "/internal/runtime/bootstrap/request", runToken, input);
            TraceNativeStage(failures, $"Bootstrap delivery returned {(int)deliveryResponse.StatusCode}.");
            Assert.True(deliveryResponse.StatusCode == HttpStatusCode.OK,
                await deliveryResponse.Content.ReadAsStringAsync() + "\n" + string.Join('\n', failures));
            var delivered = await deliveryResponse.Content.ReadFromJsonAsync<RuntimeBootstrapDeliveryReceipt>(
                CoordinationJsonOptions);
            Assert.NotNull(delivered);
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            failures.Enqueue($"After delivery: {elapsed.Elapsed}");
            output.WriteLine(failures.Last());
            Assert.Equal(registration.RuntimeInstanceId, delivered.RuntimeInstanceId);
            if (!revokeSourceBeforeSdk)
            {
                TraceNativeStage(failures, "Bootstrap replay begin.");
                var replay = await RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapDeliveryReceipt>(
                    broker, broker.BaseAddress!, "/internal/runtime/bootstrap/request", actor, input, default);
                TraceNativeStage(failures, "Bootstrap replay completed.");
                Assert.Equal(delivered, replay);
            }
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            var modelBindings = sdk.Byok ? AzureByokModelBindings() :
                new RuntimeModelBindingsResolver("legacy-unversioned", new Dictionary<string, RuntimeModelBinding>
                {
                    [registration.Binding.ModelSelectionReference!] =
                        new(sdk.ModelId, ModelSourceMode.HostedCopilot)
                });
            var factory = new RuntimeCopilotSessionFactory(
                sdk.Connection, Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
                modelBindings);
            var runtimeBroker = new RuntimeBrokerCredentialClient(
                runtimeHttp, broker.BaseAddress!, IdentityBrokerWebApplicationFactory.Issuer, actor, runtimeTimeProvider);
            sdk.ExpectedAvailableToolsCount = 7;
            sdk.PersistNativeSessionState = true;
            var bootstrap = new RuntimeSessionBootstrap(currentOwner, runtimeBroker, factory,
                new RuntimeSkillContentHttpClient(runtimeHttp, new("https://orchestrator.test/"), actor),
                actor, runtimeTimeProvider,
                actions: new RuntimeActionHttpClient(runtimeHttp, new("https://orchestrator.test/"), actor));
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => receiver.ConfigureAsync(
                bootstrap, "forged"u8.ToArray(), Guid.NewGuid(), Guid.NewGuid(), default));
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            Assert.Empty(sdk.Requests);
            var operationLimit = TimeSpan.FromMinutes(3);
            using var timeout = new CancellationTokenSource(operationLimit);
            var operationElapsed = System.Diagnostics.Stopwatch.StartNew();
            using var deadlineTrace = timeout.Token.Register(() =>
                TraceNativeStage(failures,
                    $"Native operation deadline cancelled after {operationElapsed.Elapsed}; limit={operationLimit}."));
            var duringSdkPreparation = sourceLoss?.StartsWith("sdk-preparation-", StringComparison.Ordinal) == true;
            if (revokeSourceBeforeSdk || duringSdkPreparation)
            {
                RuntimeCredentialProof? exchanged = null;
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                inspectRuntimeResponse = async (request, response, token) =>
                {
                    if (request.RequestUri?.AbsolutePath != "/internal/runtime/bootstrap/exchange" ||
                        !response.IsSuccessStatusCode)
                        return;
                    var exchange = JsonSerializer.Deserialize<RuntimeCredentialExchangeResponse>(
                        await response.Content.ReadAsByteArrayAsync(token), CoordinationJsonOptions);
                    Assert.NotNull(exchange);
                    Assert.NotNull(exchange.CredentialValue);
                    var grant = exchange.Receipt;
                    expiryTimeProvider?.SetUtcNow(grant.RecordedAt);
                    exchanged = new(grant.GrantId, grant.RuntimeInstanceId, grant.Revision, grant.Purpose,
                        grant.Audience, grant.ConfigurationHash,
                        new SecretCredential(exchange.CredentialValue, grant.ExpiresAt, runtimeTimeProvider));
                    output.WriteLine("Captured actual Broker source exchange for the creation-authority wait.");
                };
                if (duringSdkPreparation)
                {
                    sdk.BeforeStatusResponse = async token =>
                    {
                        Assert.NotNull(exchanged);
                        held.TrySetResult();
                        await release.Task.WaitAsync(token);
                    };
                }
                else
                {
                    inspectOwnerResponse = async (request, response, token) =>
                    {
                        if (exchanged is null || request.RequestUri?.AbsolutePath !=
                            $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}" ||
                            !response.IsSuccessStatusCode)
                            return;
                        held.TrySetResult();
                        await release.Task.WaitAsync(token);
                    };
                }
                var configure = receiver.ConfigureAsync(bootstrap, configuration, Guid.NewGuid(), Guid.NewGuid(),
                    timeout.Token);
                try
                {
                    await Task.WhenAny(configure, held.Task).WaitAsync(timeout.Token);
                    if (configure.IsCompleted)
                        await configure;
                    await held.Task.WaitAsync(timeout.Token);
                    output.WriteLine(duringSdkPreparation
                        ? "Holding the actual SDK status.get response before native session creation."
                        : "Holding the final actual pre-SDK runtime registration response.");
                    Assert.NotNull(exchanged);
                    Assert.DoesNotContain(sdk.Requests, request => request.Method == "session.create");
                    if (duringSdkPreparation)
                        Assert.Contains(sdk.Requests, request => request.Method == "status.get");
                    else
                        Assert.Empty(sdk.Requests);
                    switch (sourceLoss)
                    {
                        case "sdk-preparation-expiry":
                            expiryTimeProvider!.SetUtcNow(exchanged.Credential.ExpiresAt.AddMilliseconds(1));
                            Assert.False(exchanged.Credential.IsUsable());
                            break;
                        case "sdk-preparation-lease":
                            using (var response = await SendAsync(projects.Client, HttpMethod.Get,
                                "/api/authorization/context", runToken, [registration.Binding.TenantId]))
                            {
                                await AssertStatusAsync(response, HttpStatusCode.OK);
                                var current = await ReadAuthorizationContextAsync(response);
                                await environment.RetireCurrentLeaseAsync(new(
                                    current.Issuer, current.ActorId, current.MembershipRevision), timeout.Token);
                            }
                            break;
                        default:
                            Assert.Equal(RuntimeCredentialState.Revoked,
                                (await runtimeBroker.RevokeAsync(exchanged, Guid.NewGuid(), timeout.Token)).State);
                            break;
                    }
                }
                finally
                {
                    release.TrySetResult();
                    inspectOwnerResponse = null;
                    inspectRuntimeResponse = null;
                    sdk.BeforeStatusResponse = null;
                }
                if (sourceLoss == "sdk-preparation-expiry")
                {
                    var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => configure);
                    Assert.Equal("runtime_credential_unavailable", failure.Code);
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<AggregateException>(() => configure);
                    Assert.All(failure.InnerExceptions, exception => Assert.IsType<RuntimeAuthorizationException>(exception));
                }
                Assert.DoesNotContain(sdk.Requests, request => request.Method == "session.create");
                if (!duringSdkPreparation)
                    Assert.Empty(sdk.Requests);
                Assert.Equal(RuntimeBootstrapReceiverState.Failed, receiver.State);
                exchanged!.Credential.Invalidate();
                await AssertNativeSourceCountsAsync(ownerSchema, 0, 0);
                await AssertNativeAccountingCountsAsync(eventsSchema, 0);
                await using var scope = brokerFactory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
                if (sourceLoss == "sdk-preparation-expiry")
                {
                    var revision = await db.RuntimeGrantRevisions.AsNoTracking()
                        .SingleAsync(row => row.GrantId == exchanged.GrantId);
                    Assert.Equal(RuntimeCredentialState.Active, revision.State);
                    Assert.True(revision.ExpiresAt <= runtimeTimeProvider.GetUtcNow());
                }
                else if (sourceLoss != "sdk-preparation-lease")
                {
                    var revision = await db.RuntimeGrantRevisions.AsNoTracking()
                        .SingleAsync(row => row.GrantId == exchanged.GrantId && row.Revision == 2);
                    Assert.Equal(RuntimeCredentialState.Revoked, revision.State);
                }
                output.WriteLine($"Actual creation-authority loss {sourceLoss ?? "pre-SDK revoke"} denied with zero native sessions, source writes, or accounting: {elapsed.Elapsed}");
                return;
            }
            TraceNativeStage(failures, "Native configure begin.");
            var consumeOperationId = Guid.NewGuid();
            var exchangeOperationId = Guid.NewGuid();
            var session = await receiver.ConfigureAsync(
                bootstrap, configuration, consumeOperationId, exchangeOperationId, timeout.Token);
            TraceNativeStage(failures, "Native configure completed.");
            Assert.Equal(RuntimeBootstrapReceiverState.Ready, receiver.State);
            if (byokSecrets is null)
                Assert.Contains(copilotConnection.Reads, request =>
                request.Secret.Id == $"copilot-user-{registration.Binding.ModelConnectionId:N}" &&
                request.Secret.Version == $"version-{copilotConnection.Writes}" &&
                request.Purpose == RuntimeSecretPurposes.ModelSession &&
                request.RunId == registration.Binding.RunId);
            else
            {
                Assert.Contains(byokSecrets.Requests, request =>
                    request.Secret == registration.Binding.ModelCredentialReference &&
                    request.Purpose == RuntimeSecretPurposes.ModelSession &&
                    request.RunId == registration.Binding.RunId);
                Assert.Empty(copilotConnection.Reads);
                Assert.Equal(new SdkByokProviderFacts("azure", sdk.ModelId, modelBindings.ConfigurationHash),
                    session.Facts.ByokProvider);
            }
            failures.Enqueue($"After SDK configure: {elapsed.Elapsed}");
            output.WriteLine(failures.Last());
            Assert.Equal(registration, session.Registration);
            Assert.Equal(registration.Binding.ModelSelectionReference, session.Facts.ModelSelectionReference);
            Assert.Equal(registration.Binding.AcceptedSelectionHash, session.Facts.AcceptedSelectionHash);
            var nativeWire = string.Join('\n', sdk.Requests.Select(request => request.Parameters.GetRawText()));
            Assert.DoesNotContain(copilotConnection.RefreshToken, nativeWire);
            Assert.DoesNotContain(ControlledCopilotConnection.ClientSecret, nativeWire);
            Assert.DoesNotContain("refreshToken", nativeWire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(copilotConnection.RefreshToken, string.Join('\n', brokerFactory.LogMessages));
            Assert.DoesNotContain(ControlledCopilotConnection.ClientSecret, string.Join('\n', brokerFactory.LogMessages));
            var create = Assert.Single(sdk.Requests, request => request.Method == "session.create");
            Assert.False(create.Parameters.GetProperty("enableSessionStore").GetBoolean());
            Assert.False(create.Parameters.GetProperty("enableConfigDiscovery").GetBoolean());
            Assert.Equal(expectedAcceptedSkill is not null,
                create.Parameters.GetProperty("enableSkills").GetBoolean());
            if (expectedAcceptedSkill is not null)
            {
                var resource = Assert.Single(expectedAcceptedSkill.Resources);
                var runtimeName = RuntimeAcceptedSkillProvider.RuntimeName(expectedAcceptedSkill.SkillId);
                var nativePath = $"workspace/{RuntimeAcceptedSkillProvider.ResourceDirectory(runtimeName)}/" +
                    resource.RelativePath;
                var nativeResource = await sdk.InvokeNativeFilesAsync(
                    "sessionFs.readFile", nativePath, null, timeout.Token);
                Assert.Equal(
                    System.Text.Encoding.UTF8.GetString(resource.Content.AsSpan()),
                    nativeResource.GetProperty("content").GetString());
            }
            var sdkSessionCreateCount = sdk.Requests.Count(request => request.Method == "session.create");
            var configureReplay = await receiver.ConfigureAsync(
                bootstrap, configuration, consumeOperationId, exchangeOperationId, timeout.Token);
            Assert.Same(session, configureReplay);
            Assert.Equal(sdkSessionCreateCount,
                sdk.Requests.Count(request => request.Method == "session.create"));
            var changedConfiguration = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                receiver.ConfigureAsync(
                    bootstrap, "changed"u8.ToArray(), consumeOperationId, exchangeOperationId, timeout.Token));
            Assert.Equal("runtime_configuration_conflict", changedConfiguration.Code);
            var changedOperation = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                receiver.ConfigureAsync(
                    bootstrap, configuration, Guid.NewGuid(), exchangeOperationId, timeout.Token));
            Assert.Equal("runtime_configuration_conflict", changedOperation.Code);
            var sourceClient = new RuntimeUsageSourceHttpClient(
                runtimeHttp, new("https://orchestrator.test/"), actor);
            if (sourceLoss is not null)
            {
                await VerifyNativeSourceAuthorityAfterWaitAsync(
                    sourceLoss, session, sourceClient, runtimeBroker, receiver, environment, ownerSchema,
                    projects, runToken, failures, timeout.Token);
                return;
            }
            await using var usage = session.CommitUsageAsync(sourceClient, timeout.Token).GetAsyncEnumerator();
            Assert.True(await usage.MoveNextAsync());
            var accepted = usage.Current;
            failures.Enqueue($"After durable usage: {elapsed.Elapsed} lease remaining " +
                $"{registration.ExpiresAt - DateTimeOffset.UtcNow}");
            output.WriteLine(failures.Last());
            Assert.Equal(17, accepted.Usage.Measurement.InputTokens);
            Assert.Equal(11, accepted.Usage.Measurement.OutputTokens);
            Assert.Equal(7, accepted.Usage.Measurement.CachedTokens);
            Assert.Equal(5, accepted.Usage.Measurement.CacheWriteTokens);
            Assert.Equal(3, accepted.Usage.Measurement.ReasoningTokens);
            Assert.Equal(sdk.Byok ? (decimal?)null : 1234567.25m, accepted.Usage.Measurement.ProviderUnits);
            Assert.Equal(12.5m, accepted.Usage.Measurement.DurationMilliseconds);
            Assert.Null(accepted.Usage.Measurement.RequestCount);
            Assert.Equal(sdk.Byok ? (decimal?)null : 2.5m, session.Facts.ModelMultiplier);
            await sdk.EmitUsageAsync(sdk.UsageData());
            Assert.True(await usage.MoveNextAsync());
            Assert.Equal(accepted, usage.Current);
            var observation = new SdkUsageObservation(
                accepted.Usage.EventId, accepted.Usage.SdkEventId!, session.Facts.SdkSessionId,
                accepted.Usage.OccurredAt, session.Facts.ModelId, 17, 11, 7, 5, 3,
                sdk.Byok ? null : 1234567.25m, 12.5m);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                sourceClient.AppendAsync(session, observation with { CacheWriteTokens = 6 }, timeout.Token));
            await AssertNativeSourceCountsAsync(ownerSchema, 1, 1);
            await AssertNativeSourceImmutabilityAsync(ownerSchema);
            var fetched = await RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageSourceReceipt>(
                runtimeHttp, new("https://orchestrator.test/"),
                $"/internal/projects/{registration.Binding.ProjectId}/runs/{registration.Binding.RunId}" +
                $"/coordination/sessions/{registration.Binding.SessionId}/usage-receipts/{accepted.ReceiptId:D}",
                actor, null, timeout.Token);
            Assert.Equal(accepted, fetched);
            await VerifyNativeUsageAccountingAsync(
                accepted, signingKey, runToken, projects, routes, failures, eventsSchema, async (materialClient, objects) =>
                {
                    await VerifyNativeSessionMaterialAsync(session, runToken, actor,
                        projects, failures, sdk, async () =>
                        {
                            if (expectedAcceptedSkill is not null)
                            {
                                using var revokeResponse = await SendJsonAsync(
                                    skillWriter!.Client,
                                    HttpMethod.Post,
                                    $"/api/projects/{registration.Binding.ProjectId}/skills/" +
                                    $"{expectedAcceptedSkill.SkillId}/revisions/{expectedAcceptedSkill.Revision}/revoke",
                                    connectionOwnerToken,
                                    new RevokeSkillRevisionRequest
                                    {
                                        Reason = "Integration test verifies revoked content cannot be delivered again."
                                    });
                                await AssertStatusAsync(revokeResponse, HttpStatusCode.NoContent);
                                using var deniedRequest = new HttpRequestMessage(
                                    HttpMethod.Get,
                                    new Uri("https://orchestrator.test/internal/runtime/registrations/" +
                                        $"{registration.RuntimeInstanceId:D}/skills"));
                                AddBearerAndTenant(deniedRequest, runToken, registration.Binding.TenantId);
                                using var deniedSkills = await owners.SendAsync(deniedRequest, timeout.Token);
                                await AssertStatusAsync(deniedSkills, HttpStatusCode.Conflict);
                                Assert.True(deniedSkills.Headers.CacheControl?.NoStore);
                                Assert.Equal(sdkSessionCreateCount,
                                    sdk.Requests.Count(request => request.Method == "session.create"));

                                await using var retryReceiver = new RuntimeBootstrapReceiver(
                                    registration, currentOwner, broker, broker.BaseAddress!, TimeProvider.System);
                                using var retryHost = await new HostBuilder().ConfigureWebHost(web =>
                                {
                                    web.UseTestServer();
                                    web.ConfigureServices(services =>
                                    {
                                        services.AddRouting();
                                        AddJwtBearer(services, signingKey);
                                        services.AddAuthorization();
                                        services.ConfigureHttpJsonOptions(options =>
                                            options.SerializerOptions.UnmappedMemberHandling =
                                                JsonUnmappedMemberHandling.Disallow);
                                    });
                                    web.Configure(app =>
                                    {
                                        app.UseRouting();
                                        app.UseAuthentication();
                                        app.UseAuthorization();
                                        app.UseEndpoints(endpoints => endpoints.MapRuntimeBootstrapReceiver(
                                            registration.Binding.ConfigureEndpoint, retryReceiver));
                                    });
                                }).StartAsync();
                                var registeredRuntimeRoute = routes[RuntimeReceiverHost];
                                routes[RuntimeReceiverHost] = () => retryHost.GetTestServer().CreateHandler();
                                try
                                {
                                    var retryInput = new RuntimeBootstrapRequest(
                                        registration.RuntimeInstanceId,
                                        Guid.NewGuid(),
                                        RuntimeContractValidation.Hash(configuration));
                                    using var retryDelivery = await SendJsonAsync(
                                        broker, HttpMethod.Post, "/internal/runtime/bootstrap/request", runToken, retryInput);
                                    await AssertStatusAsync(retryDelivery, HttpStatusCode.OK);
                                    Assert.Equal(RuntimeBootstrapReceiverState.Pending, retryReceiver.State);
                                    await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                                        retryReceiver.ConfigureAsync(
                                            bootstrap, configuration, Guid.NewGuid(), Guid.NewGuid(), timeout.Token));
                                    Assert.Equal(RuntimeBootstrapReceiverState.Failed, retryReceiver.State);
                                    Assert.Equal(sdkSessionCreateCount,
                                        sdk.Requests.Count(request => request.Method == "session.create"));
                                }
                                finally
                                {
                                    routes[RuntimeReceiverHost] = registeredRuntimeRoute;
                                }
                            }
                            if (!sdk.Byok)
                            {
                                using var currentConnection = await SendAsync(broker, HttpMethod.Get,
                                $"/internal/connections/copilot-user/{registration.Binding.ModelConnectionId:D}",
                                connectionOwnerToken, [registration.Binding.TenantId]);
                            await AssertStatusAsync(currentConnection, HttpStatusCode.OK);
                            var current = await currentConnection.Content.ReadFromJsonAsync<CopilotConnectionReceipt>(
                                CoordinationJsonOptions);
                            Assert.NotNull(current);
                            using var disconnected = await SendJsonAsync(broker, HttpMethod.Post,
                                "/internal/connections/copilot-user/revoke", connectionOwnerToken,
                                new ChangeCopilotConnectionRequest(current.ConnectionId, current.Revision));
                            await AssertStatusAsync(disconnected, HttpStatusCode.OK);
                            }
                            environment.ExpireCurrentLease();
                            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                                currentOwner.ReadCurrentAsync(registration.RuntimeInstanceId, actor, timeout.Token));
                        }, setHistoricalReadAuthority, sourceClient, orchestratorServices, ownerSchema,
                        materialClient, objects, timeout.Token, sdk.Byok ? async recorded =>
                        {
                            Assert.True(await usage.MoveNextAsync());
                            var nativeReceipt = usage.Current;
                            Assert.Equal(sdk.NativeUsageEventId.ToString("D"), nativeReceipt.Usage.SdkEventId);
                            Assert.Null(nativeReceipt.Usage.A2AMessageId);
                            Assert.Equal(session.Facts, nativeReceipt.Usage.SdkSource);
                            Assert.Equal(session.Registration, nativeReceipt.Registration);
                            Assert.Contains(sdk.NativeUsageEventId, recorded.Observation.UsageEventIds);
                            using var appended = await SendJsonAsync(materialClient, HttpMethod.Post,
                                $"/internal/sessions/{registration.Binding.SessionId}/usage-receipts", runToken,
                                new RuntimeUsageReceiptReferenceRequest(nativeReceipt.ReceiptId));
                            await AssertStatusAsync(appended, HttpStatusCode.OK);
                            var ack = await appended.Content.ReadFromJsonAsync<RuntimeUsageAccountingAcknowledgment>(
                                CoordinationJsonOptions);
                            Assert.NotNull(ack);
                            Assert.Equal(CostDisposition.Unpriced, ack.Accounting.Disposition);
                            Assert.Equal(nativeReceipt.Usage.EventId, ack.Accounting.EventId);
                            await AssertNativeSourceCountsAsync(ownerSchema, 1, 2);
                            await AssertNativeAccountingCountsAsync(eventsSchema, 2, 0);
                            return ImmutableArray.Create(new RuntimeUsageCostReceiptReference(
                                nativeReceipt.ReceiptId, ack.Accounting));
                        } : null, runtimeHttp);
                    await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => receiver.DisposeAsync().AsTask());
                    Assert.Equal(RuntimeBootstrapReceiverState.Disposed, receiver.State);
                });
            output.WriteLine($"After actual Events priced ACK: {elapsed.Elapsed}");
        });
        TraceNativeStage(failures, $"Primary exercise completed: {primaryFailure?.ToString() ?? "success"}");
        TraceNativeStage(failures, "Receiver cleanup begin.");
        var receiverCleanupFailure = await Record.ExceptionAsync(() => receiver.DisposeAsync().AsTask());
        TraceNativeStage(failures, $"Receiver cleanup completed: {receiverCleanupFailure?.ToString() ?? "success"}");
        bearer.Invalidate();
        TraceNativeStage(failures, "Receiver host stop begin.");
        var hostCleanupFailure = await Record.ExceptionAsync(() => host.StopAsync());
        TraceNativeStage(failures, $"Receiver host stop completed: {hostCleanupFailure?.ToString() ?? "success"}");
        var lifecycleFailures = new[] { primaryFailure, receiverCleanupFailure, hostCleanupFailure }
            .OfType<Exception>().ToArray();
        if (lifecycleFailures.Length > 0)
            Assert.Fail(new AggregateException("Native runtime proof and cleanup failed.", lifecycleFailures)
                + "\n" + string.Join('\n', failures));
    }

    private static void TraceNativeStage(ConcurrentQueue<string> failures, string stage)
    {
        var message = $"Native fixture {DateTimeOffset.UtcNow:O}: {stage}";
        failures.Enqueue(message);
        Console.WriteLine(message);
    }

    private async Task VerifyNativeSessionMaterialAsync(
        AuthorizedRuntimeSession session, string runToken, RuntimeActorAuthorization actor,
        ProjectsConfigResourceServer projects, ConcurrentQueue<string> failures, ControlledCopilotRuntime sdk,
        Func<Task> endRuntimeAuthority, Func<bool, Task> setHistoricalReadAuthority,
        RuntimeUsageSourceHttpClient sourceClient, IServiceProvider orchestratorServices, string ownerSchema,
        HttpClient client, RuntimeMaterialObjects objects,
        CancellationToken cancellationToken,
        Func<RuntimeNativeTurnRecordedReceipt, Task<ImmutableArray<RuntimeUsageCostReceiptReference>>>?
            accountNativeUsage = null, HttpClient? ownerClient = null)
    {
        var sourceCredentialValue = session.Proof().Credential.GetValue();
        var material = new RuntimeSessionMaterialHttpClient(client, client.BaseAddress!, actor);
        var eventId = Guid.NewGuid();
        const string userContent = "Run the accepted bounded turn.";
        var receipt = await session.CommitTurnContentAsync(
            material, eventId, "user", userContent, cancellationToken);
        Assert.Equal(session.Registration.RuntimeInstanceId, receipt.Reference.Material!.RuntimeInstanceId);
        Assert.Equal(session.Facts.SdkVersion, receipt.Reference.Material.SdkVersion);
        Assert.Equal(session.Facts.ModelId, receipt.Reference.Material.ModelId);
        Assert.Equal(1, objects.Writes);
        var replay = await session.CommitTurnContentAsync(
            material, eventId, "user", userContent, cancellationToken);
        Assert.Equal(receipt, replay);
        Assert.Equal(1, objects.Writes);
        var read = await material.ReadRecordedAsync(
            receipt.Identity, eventId, SessionMaterialKind.TurnContent, cancellationToken);
        Assert.Equal(receipt, read.Material);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(userContent), read.Bytes);
        var path = $"/internal/sessions/{session.Registration.Binding.SessionId}/material";
        var input = new SessionMaterialWriteRequest(1, Guid.NewGuid(), session.Registration.RuntimeInstanceId,
            session.Registration.Revision, session.Registration.Binding.ExecutionFence + 1,
            SessionMaterialKind.TurnContent, read.Bytes, "user", session.Facts.SdkVersion, session.Facts.ModelId);
        using var staleFence = await SendJsonAsync(client, HttpMethod.Post, path, runToken, input);
        await AssertStatusAsync(staleFence, HttpStatusCode.Forbidden);
        using var observeOnly = await SendJsonAsync(client, HttpMethod.Post, path,
            session.Proof().Credential.GetValue(), input with
            {
                ExecutionFence = session.Registration.Binding.ExecutionFence
            });
        await AssertStatusAsync(observeOnly, HttpStatusCode.Unauthorized);
        Assert.Equal(1, objects.Writes);
        Assert.True(staleFence.Headers.CacheControl?.NoStore);
        var refreshOperation = Guid.NewGuid();
        var previousSource = session.Proof();
        var refreshed = await session.RefreshAsync(refreshOperation, previousSource.Revision, cancellationToken);
        Assert.Equal(previousSource.Revision + 1, refreshed.SourceGrant.Revision);
        Assert.False(previousSource.Credential.IsUsable());
        Assert.Equal(refreshed,
            await session.RefreshAsync(refreshOperation, previousSource.Revision, cancellationToken));
        var refreshedCredentialValue = session.Proof().Credential.GetValue();
        TraceNativeStage(failures, "Guarded actual model turn and native cache begin.");
        var sendCount = sdk.Requests.Count(request => request.Method == "session.send");
        var legacyFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            session.SendTurnAsync("A bounded user request.", material, cancellationToken));
        Assert.Equal("runtime_native_turn_admission_required", legacyFailure.Code);
        Assert.Equal(sendCount, sdk.Requests.Count(request => request.Method == "session.send"));
        var binding = session.Registration.Binding;
        string rootSessionId;
        string associationId;
        await using (var dataSource = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"""
            SELECT root_session_id, work_plan_item_id
            FROM "{ownerSchema}".coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            """, connection))
        {
            command.Parameters.AddWithValue("project", binding.ProjectId);
            command.Parameters.AddWithValue("run", binding.RunId);
            command.Parameters.AddWithValue("session", binding.SessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            rootSessionId = reader.GetString(0);
            associationId = reader.GetString(1);
        }
        await using var ownerScope = orchestratorServices.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = ownerScope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {runToken}";
        context.Request.Headers["X-Agentweaver-Tenant"] = binding.TenantId;
        var authentication = await context.AuthenticateAsync();
        Assert.True(authentication.Succeeded);
        context.User = Assert.IsAssignableFrom<System.Security.Claims.ClaimsPrincipal>(authentication.Principal);
        var ownerActor = new CoordinationActor(binding.ActorIssuer, binding.ActorId);
        var rootIdentity = new SessionIdentity(binding.ProjectId, binding.RunId, rootSessionId);
        var selection = await ownerScope.ServiceProvider.GetRequiredService<ProjectsRunSelectionClient>()
            .ReadAcceptedSelectionWithAuthorityAsync(context, binding.ProjectId, binding.RunId, cancellationToken);
        var decisions = ownerScope.ServiceProvider.GetRequiredService<CoordinatorDecisionOwnerStore>();
        var decision = await decisions.ReadCurrentAsync(ownerActor, rootIdentity, selection, cancellationToken);
        var plan = decision.State.ConfirmedWorkPlan;
        Assert.NotNull(plan);
        Assert.True(decision.State.CanDispatch);
        Assert.Equal(binding.WorkflowStepId, Assert.Single(plan.Plan.Items, item => item.Id == associationId).WorkflowStepId);
        var selectionContext = await ownerScope.ServiceProvider.GetRequiredService<CoordinatorRunSelectionContextStore>()
            .ReadAsync(selection.Selection, decision.State.Fence, cancellationToken)
            ?? CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Selection.Snapshot);
        var execution = new MafExecutionCheckpointStore(
            ownerScope.ServiceProvider.GetRequiredService<PostgresMafCheckpointStore>(),
            new(rootIdentity, ownerActor, binding.ExecutionFence, MafExecutionCheckpointStore.CurrentSdkVersion,
                binding.ModelSelectionReference!, null, MafExecutionCheckpointContract.StoreName));
        var latest = await execution.ReadLatestAsync(cancellationToken);
        var intent = new MafExecutionDispatchIntent(associationId, binding.SessionId, Guid.NewGuid(),
            RuntimeContractValidation.Hash("A bounded user request."u8));
        var progress = latest?.State.Progress ?? MafExecutionProgress.Empty;
        var state = new MafExecutionCheckpoint((latest?.State.Revision ?? 0) + 1, plan.Plan.Id, decision.StateVersion,
            progress with { WorkItems = progress.WorkItems.SetItem(associationId, MafExecutionTaskStatus.Running) })
        {
            PendingDispatches = (latest?.State.PendingDispatches
                ?? ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty).SetItem(associationId, intent)
        };
        var checkpoint = await execution.AppendAsync(Guid.NewGuid().ToString("N"), latest?.Info, state, cancellationToken);
        var message = new RuntimeA2ASendRequest(new("message", intent.MessageId, session.Facts.SdkSessionId, "user",
            [new("text", "A bounded user request.")], new(new(1, session.Registration, session.SourceGrant.GrantId,
                session.SourceGrant.Revision, RuntimeCredentialPurpose.Observe), AddressedMessageDeliveryMode.Enqueue)));
        Task RequireCurrentOwner(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token) =>
            decisions.RequireCurrentDispatchStateInTransactionAsync(connection, transaction, ownerActor, rootIdentity,
                selection, selectionContext, decision.StateVersion, binding.ExecutionFence, plan.Plan.Id, token);
        var nativeOwner = ownerScope.ServiceProvider.GetRequiredService<RuntimeUsageSourceOwner>();
        await nativeOwner.PrepareNativeTurnAsync(context, session.Registration, rootIdentity, checkpoint, intent,
            message, RequireCurrentOwner, cancellationToken);
        sdk.EmitNativeCompletionReceipt = true;
        sdk.EmitUsageWithTurn = accountNativeUsage is not null;
        var (answer, recorded) = await session.SendNativeTurnAsync(message, material, sourceClient, cancellationToken);
        Assert.Equal(sdk.AssistantResponse, answer);
        RuntimeNativeTurnContract.ValidateRecorded(recorded);
        Assert.Equal(intent.MessageId, recorded.Admission.MessageId);
        Assert.Equal(sdk.NativeMessageId.ToString("D"), recorded.Observation.NativeMessageId);
        Assert.Equal(sdk.NativeCompletionReceiptEventId, recorded.Observation.NativeCompletionReceiptEventId);
        var references = accountNativeUsage is null
            ? ImmutableArray<RuntimeUsageCostReceiptReference>.Empty : await accountNativeUsage(recorded);
        if (accountNativeUsage is null)
            Assert.Empty(recorded.Observation.UsageEventIds);
        else
            Assert.Single(recorded.Observation.UsageEventIds);
        if (accountNativeUsage is not null)
            await VerifyRecordedNativeOwnerReadAsync(
                Assert.IsType<HttpClient>(ownerClient), actor, recorded, references, cancellationToken);
        var accounted = await sourceClient.CompleteNativeTurnAsync(session, recorded, references, cancellationToken);
        RuntimeNativeTurnContract.ValidateAccounted(accounted, recorded, references);
        Assert.Equal(intent.MessageId, accounted.Recorded.Admission.MessageId);
        Assert.Equal(intent.MessageId, accounted.Snapshot.DispatchId);
        if (accountNativeUsage is null)
            Assert.Equal(0.00123456725m, Assert.Single(accounted.Snapshot.CopilotTotals.Amounts).Amount);
        else
            Assert.Equal(Assert.Single(references), Assert.Single(accounted.Snapshot.RepresentedReceipts));
        await nativeOwner.RequireNativeTurnAccountedAsync(context, session.Registration, message, answer,
            RequireCurrentOwner, cancellationToken);
        Assert.Equal(sendCount + 1, sdk.Requests.Count(request => request.Method == "session.send"));
        Assert.Equal(4, objects.Writes);
        var recovery = await material.ReadRecoveryAsync(session.Registration, cancellationToken);
        Assert.NotNull(recovery.Cache);
        Assert.Contains(recovery.Turns, turn => turn == ("user", "A bounded user request."));
        Assert.Contains(recovery.Turns, turn => turn == ("assistant", answer));
        Assert.Equal(session.Facts.SdkVersion, recovery.Cache.Material.Reference.Material!.SdkVersion);
        Assert.DoesNotContain(sdk.SdkCredential, System.Text.Encoding.UTF8.GetString(recovery.Cache.Bytes));
        Assert.DoesNotContain(sourceCredentialValue,
            System.Text.Encoding.UTF8.GetString(recovery.Cache.Bytes));
        Assert.DoesNotContain(refreshedCredentialValue,
            System.Text.Encoding.UTF8.GetString(recovery.Cache.Bytes));
        Assert.True(await objects.DeleteAsync(recovery.Cache.Material.Reference.Key, cancellationToken));
        var missing = await material.ReadRecoveryAsync(session.Registration, cancellationToken);
        Assert.Null(missing.Cache);
        Assert.Equal(recovery.Turns, missing.Turns);
        await endRuntimeAuthority();
        var historical = await material.ReadRecordedAsync(
            receipt.Identity, eventId, SessionMaterialKind.TurnContent, cancellationToken);
        Assert.Equal(receipt, historical.Material);
        Assert.Equal(read.Bytes, historical.Bytes);
        using var camelCase = await SendAsync(client, HttpMethod.Get,
            $"{path}/{eventId:D}/turnContent", runToken, [session.Registration.Binding.TenantId]);
        await AssertStatusAsync(camelCase, HttpStatusCode.OK);
        foreach (var invalidKind in new[] { "unknown", "0", "1" })
        {
            using var invalid = await SendAsync(client, HttpMethod.Get,
                $"{path}/{eventId:D}/{invalidKind}", runToken, [session.Registration.Binding.TenantId]);
            await AssertStatusAsync(invalid, HttpStatusCode.BadRequest);
        }

        var pinPath = $"/internal/projects/{receipt.Identity.ProjectId}/runs/{receipt.Identity.RunId}/sessions-provider-binding";
        using var pinResponse = await SendAsync(client, HttpMethod.Get, pinPath,
            runToken, [session.Registration.Binding.TenantId]);
        await AssertStatusAsync(pinResponse, HttpStatusCode.OK);
        Assert.True(pinResponse.Headers.CacheControl?.NoStore);
        var pin = await pinResponse.Content.ReadFromJsonAsync<SessionProviderBinding>(CoordinationJsonOptions);
        Assert.NotNull(pin);
        Assert.Equal(EventsHost::Agentweaver.EventsAndSessions.NativePostgresSessionsProvider.ProviderId, pin.ProviderId);
        Assert.True(pin.ResourceGeneration > 0);
        Assert.Contains(EventsHost::Agentweaver.EventsAndSessions.SessionsCapabilities.ObjectReferences,
            pin.NegotiatedCapabilities);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => session.CommitTurnContentAsync(
            material, Guid.NewGuid(), "user", "No execution after authority loss.", cancellationToken));
        objects.AfterRead = async () =>
        {
            objects.AfterRead = null;
            await setHistoricalReadAuthority(false);
        };
        try
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => material.ReadRecordedAsync(
                receipt.Identity, eventId, SessionMaterialKind.TurnContent, cancellationToken));
            using var summary = await SendAsync(projects.Client, HttpMethod.Get,
                $"/api/projects/{receipt.Identity.ProjectId}?runId={receipt.Identity.RunId}",
                runToken, [session.Registration.Binding.TenantId]);
            await AssertStatusAsync(summary, HttpStatusCode.OK);
            using var deniedCache = await SendAsync(client, HttpMethod.Get,
                $"{path}/{recovery.Cache.Material.EventId:D}/sdkCache",
                runToken, [session.Registration.Binding.TenantId]);
            await AssertStatusAsync(deniedCache, HttpStatusCode.Forbidden);
            using var deniedPin = await SendAsync(client, HttpMethod.Get, pinPath,
                runToken, [session.Registration.Binding.TenantId]);
            await AssertStatusAsync(deniedPin, HttpStatusCode.Forbidden);
        }
        finally
        {
            objects.AfterRead = null;
            await setHistoricalReadAuthority(true);
        }
        var restored = await material.ReadRecordedAsync(
            receipt.Identity, eventId, SessionMaterialKind.TurnContent, cancellationToken);
        Assert.Equal(historical.Material, restored.Material);
        Assert.Equal(historical.Bytes, restored.Bytes);
        TraceNativeStage(failures, "Guarded model turn, exact native cache, and missing-cache journal path completed.");
    }

    private static async Task VerifyRecordedNativeOwnerReadAsync(
        HttpClient client, RuntimeActorAuthorization actor, RuntimeNativeTurnRecordedReceipt recorded,
        ImmutableArray<RuntimeUsageCostReceiptReference> references, CancellationToken cancellationToken)
    {
        var snapshot = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        var retained = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnRecordedReceipt>(
            client, new("https://orchestrator.test/"), "/internal/runtime/turns/recorded",
            actor, recorded, cancellationToken);
        RuntimeNativeTurnContract.ValidateAccountingSnapshot(snapshot, retained);
        var changedObservation = recorded.Observation with { OutputHash = new string('c', 64) };
        foreach (var changed in new[]
        {
            recorded with { OwnerRevision = recorded.OwnerRevision + 1 },
            recorded with { Observation = changedObservation,
                CanonicalPayloadHash = RuntimeNativeTurnContract.ObservationHash(changedObservation) },
            recorded with { Admission = recorded.Admission with
            {
                Registration = recorded.Admission.Registration with
                {
                    Binding = recorded.Admission.Registration.Binding with
                    {
                        ExecutionFence = recorded.Admission.Registration.Binding.ExecutionFence + 1
                    }
                }
            } }
        })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://orchestrator.test/internal/runtime/turns/recorded")
            {
                Content = JsonContent.Create(changed, options: CoordinationJsonOptions)
            };
            AddBearerAndTenant(request, actor.Bearer.GetValue(), recorded.Admission.Registration.Binding.TenantId);
            using var denied = await client.SendAsync(request, cancellationToken);
            await AssertStatusAsync(denied, HttpStatusCode.Forbidden);
            Assert.True(denied.Headers.CacheControl?.NoStore);
        }
    }

    private sealed class MaterialResponseTraceHandler(ConcurrentQueue<string> failures) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                TraceNativeStage(failures, $"Material response status: {(int)response.StatusCode}.");
                TraceNativeStage(failures,
                    $"Material response {(int)response.StatusCode}: " +
                    await response.Content.ReadAsStringAsync(cancellationToken));
            }
            return response;
        }
    }

    private sealed class RuntimeMaterialObjects : IObjectStore
    {
        private readonly ConcurrentDictionary<ObjectKey, byte[]> _values = [];
        public int Writes;
        public Func<Task>? AfterRead { get; set; }

        public async Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!_values.TryAdd(key, buffer.ToArray()))
                throw new InvalidOperationException("A material object already exists.");
            Interlocked.Increment(ref Writes);
        }

        public async Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_values.TryGetValue(key, out var bytes))
                return null;
            var content = new MemoryStream(bytes, writable: false);
            if (AfterRead is not null)
                await AfterRead();
            return new(content, bytes.Length, content.Dispose);
        }

        public Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryRemove(key, out _));
    }

    private async Task VerifyNativeUsageAccountingAsync(
        RuntimeUsageSourceReceipt sourceReceipt, SecurityKey signingKey, string runToken,
        ProjectsConfigResourceServer projects, Dictionary<string, Func<HttpMessageHandler>> routes,
        ConcurrentQueue<string> failures, string eventsSchema,
        Func<HttpClient, RuntimeMaterialObjects, Task> verifyMaterial)
    {
        var binding = sourceReceipt.Registration.Binding;
        var sdkSource = sourceReceipt.Usage.SdkSource;
        Assert.NotNull(sdkSource);
        var byok = sdkSource.SourceMode == "byok";
        using var configuration = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["EventsAndSessions__RuntimeUsage__Enabled"] = "true",
            ["AgentHost__ModelBindingsRevision"] = byok ? "azure-byok-bindings-v1" : "native-model-bindings-v1",
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__ModelId"] = sdkSource.ModelId,
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__SourceMode"] =
                byok ? "Byok" : "HostedCopilot",
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__PromptCapacityTokens"] =
                byok ? "20000" : null,
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__Provider__Type"] = byok ? "azure" : null,
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__Provider__BaseUrl"] =
                byok ? "https://byok.test/" : null,
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__Provider__WireApi"] =
                byok ? "responses" : null,
            [$"AgentHost__ModelBindings__{sdkSource.ModelSelectionReference}__Provider__AzureApiVersion"] =
                byok ? "2025-04-01-preview" : null,
            ["EventsAndSessions__Cost__Copilot__ResourceId"] = "native-copilot-cost",
            ["EventsAndSessions__Cost__Copilot__ResourceGeneration"] = "1",
            ["EventsAndSessions__Cost__Copilot__OptionsRevision"] = "native-cost-v1",
            ["EventsAndSessions__Cost__Copilot__OptionsSchemaVersion"] = "1",
            ["EventsAndSessions__Cost__Copilot__RateCard__Id"] = "native-sdk-card",
            ["EventsAndSessions__Cost__Copilot__RateCard__Version"] = "1",
            ["EventsAndSessions__Cost__Copilot__RateCard__MeterSource"] = SdkMeterSources.CopilotNanoAiu,
            ["EventsAndSessions__Cost__Copilot__RateCard__Unit"] = "AIC",
            ["EventsAndSessions__Cost__Copilot__RateCard__NanoUnitsPerUnit"] = "1000000000",
            ["EventsAndSessions__Cost__Copilot__RateCard__ModelMultipliers__controlled-model"] = "2.5"
        });
        var objects = new RuntimeMaterialObjects();
        await using var factory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => new RuntimeServiceRouter(routes, failures),
            nativeUsageHandler: () => new RuntimeServiceRouter(routes, failures), sessionMaterialObjects: objects);
        using var client = factory.CreateDefaultClient(
            new Uri("https://events.test/"), new MaterialResponseTraceHandler(failures));
        var modelPin = factory.Services.GetRequiredService<RuntimeModelBindingsResolver>()
            .Pin(sdkSource.ModelSelectionReference, byok ? ModelSourceMode.Byok : ModelSourceMode.HostedCopilot);
        Assert.Equal(sdkSource.ModelId, modelPin.ModelId);
        var path = $"/internal/sessions/{binding.SessionId}/usage-receipts";
        using var forged = await SendJsonAsync(client, HttpMethod.Post, path, runToken,
            new { sourceReceipt.ReceiptId, ProviderUnits = 1, Price = 100 });
        await AssertStatusAsync(forged, HttpStatusCode.BadRequest);
        using var append = await SendJsonAsync(client, HttpMethod.Post, path, runToken,
            new RuntimeUsageReceiptReferenceRequest(sourceReceipt.ReceiptId));
        await AssertStatusAsync(append, HttpStatusCode.OK);
        Assert.True(append.Headers.CacheControl?.NoStore);
        var accepted = await append.Content.ReadFromJsonAsync<RuntimeUsageAccountingAcknowledgment>(
            CoordinationJsonOptions);
        Assert.NotNull(accepted);
        Assert.False(accepted.IsDuplicate);
        Assert.Equal(sourceReceipt.Usage.EventId, accepted.Accounting.EventId);
        Assert.Equal(sourceReceipt.Usage.Attribution, accepted.Accounting.Attribution);
        Assert.Equal(byok ? (decimal?)null : 0.00123456725m, accepted.Accounting.Amount);
        Assert.Equal(byok ? CostDisposition.Unpriced : CostDisposition.Estimate, accepted.Accounting.Disposition);
        await AssertNativeHistoryImmutabilityAsync(eventsSchema,
            [("usage_source_receipts", "source_receipt_id"), ("usage_run_cost_bindings", "meter_source")],
            "reject_usage_receipt_mutation");
        await AssertNativeAccountingCountsAsync(eventsSchema, 1, byok ? 0 : 1);
        using var replay = await SendJsonAsync(client, HttpMethod.Post, path, runToken,
            new RuntimeUsageReceiptReferenceRequest(sourceReceipt.ReceiptId));
        await AssertStatusAsync(replay, HttpStatusCode.OK);
        var repeated = await replay.Content.ReadFromJsonAsync<RuntimeUsageAccountingAcknowledgment>(
            CoordinationJsonOptions);
        Assert.NotNull(repeated);
        Assert.True(repeated.IsDuplicate);
        Assert.Equal(accepted.Accounting, repeated.Accounting);
        using var totalsResponse = await SendAsync(client, HttpMethod.Get,
            $"/internal/projects/{binding.ProjectId}/runs/{binding.RunId}/usage",
            runToken, [binding.TenantId]);
        await AssertStatusAsync(totalsResponse, HttpStatusCode.OK);
        var totals = await totalsResponse.Content.ReadFromJsonAsync<UsageRunTotals>(CoordinationJsonOptions);
        Assert.NotNull(totals);
        Assert.Equal(1, totals.Events);
        var agent = Assert.Single(totals.Agents);
        Assert.Equal(binding.AgentId, agent.AgentId);
        Assert.Equal(5, agent.CacheWriteTokens);
        Assert.Null(agent.RequestCount);
        if (byok)
        {
            Assert.Empty(totals.Amounts);
            Assert.False(totals.IsFullyPriced);
        }
        else
            Assert.Equal(0.00123456725m, Assert.Single(totals.Amounts).Amount);
        await using var source = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var check = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{eventsSchema}".usage_ledger),
                   (SELECT count(*) FROM "{eventsSchema}".usage_rate_cards),
                   (SELECT count(*) FROM "{eventsSchema}".usage_source_receipts),
                   (SELECT count(*) FROM "{eventsSchema}".usage_run_cost_bindings),
                   (SELECT count(*) FROM "{eventsSchema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events.native-sdk-usage.v1')
            """, connection);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 5), column =>
            Assert.Equal(byok && column == 1 ? 0L : 1L, reader.GetInt64(column)));
        routes.Add("events.test", () => factory.Server.CreateHandler());
        try
        {
            await verifyMaterial(client, objects);
        }
        finally
        {
            routes.Remove("events.test");
        }
    }

    private sealed class RuntimeServiceRouter(
        IReadOnlyDictionary<string, Func<HttpMessageHandler>> routes,
        ConcurrentQueue<string> failures,
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? afterResponse = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            if (request.RequestUri is not { Scheme: "https" } endpoint ||
                !routes.TryGetValue(endpoint.Host, out var handler))
                throw new InvalidOperationException("The exact test service route is not registered.");
            using var invoker = new HttpMessageInvoker(handler());
            TraceNativeStage(failures, $"HTTP begin {request.Method} {endpoint.Host}{endpoint.AbsolutePath}; " +
                $"cancellable={cancellationToken.CanBeCanceled}, cancelled={cancellationToken.IsCancellationRequested}.");
            var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            TraceNativeStage(failures, $"HTTP response {request.Method} {endpoint.Host}{endpoint.AbsolutePath}: " +
                $"{(int)response.StatusCode}, elapsed={elapsed.Elapsed}, cancelled={cancellationToken.IsCancellationRequested}, " +
                $"content-type={response.Content.Headers.ContentType}, content-length={response.Content.Headers.ContentLength}.");
            if (elapsed.Elapsed > TimeSpan.FromMilliseconds(500))
                failures.Enqueue($"{endpoint.Host}{endpoint.AbsolutePath}: took {elapsed.Elapsed}");
            if (!response.IsSuccessStatusCode)
                failures.Enqueue($"{endpoint.Host}{endpoint.AbsolutePath}: {(int)response.StatusCode} " +
                    await response.Content.ReadAsStringAsync(cancellationToken));
            if (afterResponse is not null)
                await afterResponse(request, response, cancellationToken);
            TraceNativeStage(failures, $"HTTP completed {request.Method} {endpoint.Host}{endpoint.AbsolutePath}.");
            return response;
        }
    }

    private async Task AssertNativeSourceCountsAsync(string schema, long sources, long observations)
    {
        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{schema}".runtime_sdk_sources),
                   (SELECT count(*) FROM "{schema}".runtime_usage_observations)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(sources, reader.GetInt64(0));
        Assert.Equal(observations, reader.GetInt64(1));
    }

    private async Task AssertNativeAccountingCountsAsync(string schema, long expected, long? rateCards = null)
    {
        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{schema}".usage_ledger),
                   (SELECT count(*) FROM "{schema}".usage_rate_cards),
                   (SELECT count(*) FROM "{schema}".usage_source_receipts),
                   (SELECT count(*) FROM "{schema}".usage_run_cost_bindings),
                   (SELECT count(*) FROM "{schema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events.native-sdk-usage.v1')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 5), column =>
            Assert.Equal(column == 1 ? rateCards ?? expected :
                column == 3 && rateCards is not null ? Math.Min(expected, 1) : expected, reader.GetInt64(column)));
    }

    private Task AssertNativeSourceImmutabilityAsync(string schema) =>
        AssertNativeHistoryImmutabilityAsync(schema,
            [("runtime_sdk_sources", "runtime_instance_id"), ("runtime_usage_observations", "receipt_id")],
            "reject_runtime_usage_mutation");

    private async Task AssertNativeHistoryImmutabilityAsync(
        string schema, (string Table, string Identity)[] tables, string rejectionFunction)
    {
        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        async Task<string> SnapshotAsync(string table, string identity)
        {
            await using var snapshot = new NpgsqlCommand($"""
                SELECT coalesce(jsonb_agg(to_jsonb(history) ORDER BY {identity}), '[]'::jsonb)::text
                FROM "{schema}".{table} AS history
                """, connection);
            return Assert.IsType<string>(await snapshot.ExecuteScalarAsync());
        }
        var before = new Dictionary<string, string>();
        foreach (var (table, identity) in tables)
        {
            before[table] = await SnapshotAsync(table, identity);
            Assert.NotEqual("[]", before[table]);
            await using var guard = new NpgsqlCommand("""
                SELECT count(*) FROM pg_trigger
                WHERE tgrelid = to_regclass(@table) AND tgname = @trigger
                  AND NOT tgisinternal AND tgenabled = 'O' AND tgtype = 34
                  AND tgfoid = to_regprocedure(@function)
                """, connection);
            guard.Parameters.AddWithValue("table", $"\"{schema}\".{table}");
            guard.Parameters.AddWithValue("trigger", $"{table}_no_truncate");
            guard.Parameters.AddWithValue("function", $"\"{schema}\".{rejectionFunction}()");
            Assert.Equal(1L, await guard.ExecuteScalarAsync());
            foreach (var sql in new[]
            {
                $"""UPDATE "{schema}".{table} SET {identity} = {identity}""",
                $"""DELETE FROM "{schema}".{table}""",
                $"""TRUNCATE "{schema}".{table} CASCADE"""
            })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                var rejected = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains("append-only", rejected.MessageText);
            }
        }
        var targets = string.Join(", ", tables.Select(table => $"\"{schema}\".{table.Table}"));
        foreach (var sql in new[] { $"TRUNCATE {targets}", $"TRUNCATE {targets} CASCADE" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", rejected.MessageText);
        }
        foreach (var (table, identity) in tables)
            Assert.Equal(before[table], await SnapshotAsync(table, identity));
    }

    private async Task VerifyNativeSourceAuthorityAfterWaitAsync(
        string loss, AuthorizedRuntimeSession session, RuntimeUsageSourceHttpClient sourceClient,
        RuntimeBrokerCredentialClient broker, RuntimeBootstrapReceiver receiver, RuntimePlacementTestServer environment,
        string schema, ProjectsConfigResourceServer projects, string runToken, ConcurrentQueue<string> failures,
        CancellationToken cancellationToken)
    {
        TraceNativeStage(failures, $"Source {loss} registration begin.");
        await sourceClient.RegisterAsync(session, cancellationToken);
        TraceNativeStage(failures, $"Source {loss} registration completed.");
        await using var lockConnection = await _nativeDataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await lockConnection.BeginTransactionAsync(cancellationToken);
        await using (var acquire = new NpgsqlCommand(loss == "head"
            ? "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.usage'), hashtext(@runtime))"
            : $"""LOCK TABLE "{schema}".runtime_usage_observations IN ACCESS EXCLUSIVE MODE""",
            lockConnection, transaction))
        {
            if (loss == "head")
                acquire.Parameters.AddWithValue("runtime", session.Registration.RuntimeInstanceId.ToString("D"));
            TraceNativeStage(failures, $"Source {loss} SQL lock acquisition begin.");
            await acquire.ExecuteNonQueryAsync(cancellationToken);
            TraceNativeStage(failures, $"Source {loss} SQL lock acquired.");
        }
        await using var observations = session.CommitUsageAsync(sourceClient, cancellationToken).GetAsyncEnumerator();
        var append = observations.MoveNextAsync().AsTask();
        TraceNativeStage(failures, $"Source {loss} append started; waiting for actual PostgreSQL contention.");
        try
        {
            await using var observer = await _nativeDataSource.OpenConnectionAsync(cancellationToken);
            var waiting = false;
            while (!waiting)
            {
                await using var wait = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                        WHERE datname = current_database() AND @locker = ANY(pg_blocking_pids(pid)))
                    """, observer);
                wait.Parameters.AddWithValue("locker", lockConnection.ProcessID);
                waiting = (bool)(await wait.ExecuteScalarAsync(cancellationToken))!;
                if (append.IsCompleted)
                    await append;
                if (!waiting)
                    await Task.Delay(20, cancellationToken);
            }
            TraceNativeStage(failures, $"Source {loss} actual PostgreSQL wait confirmed; revocation begin.");
            switch (loss)
            {
                case "grant":
                    Assert.Equal(RuntimeCredentialState.Revoked,
                        (await broker.RevokeAsync(session.Proof(), Guid.NewGuid(), cancellationToken)).State);
                    break;
                case "head":
                    await using (var source = NpgsqlDataSource.Create(_connectionString))
                    {
                        var owner = new RuntimeRegistrationStore(source, schema, TimeProvider.System);
                        var revoked = await owner.RevokeAsync(
                            session.Registration.RuntimeInstanceId, session.Registration.Revision, cancellationToken);
                        Assert.Equal(RuntimeRegistrationState.Revoked, revoked.State);
                        Assert.Equal(session.Registration.Revision + 1, revoked.Revision);
                    }
                    break;
                case "lease":
                    using (var response = await SendAsync(projects.Client, HttpMethod.Get,
                        "/api/authorization/context", runToken, [session.Registration.Binding.TenantId]))
                    {
                        await AssertStatusAsync(response, HttpStatusCode.OK);
                        var current = await ReadAuthorizationContextAsync(response);
                        await environment.RetireCurrentLeaseAsync(new(
                            current.Issuer, current.ActorId, current.MembershipRevision), cancellationToken);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unknown native source authority-loss case.");
            }
            TraceNativeStage(failures, $"Source {loss} revocation completed.");
        }
        finally
        {
            TraceNativeStage(failures, $"Source {loss} SQL rollback begin.");
            await transaction.RollbackAsync(CancellationToken.None);
            TraceNativeStage(failures, $"Source {loss} SQL rollback completed.");
        }
        TraceNativeStage(failures, $"Source {loss} denial await begin.");
        var denial = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => append);
        TraceNativeStage(failures, $"Source {loss} denial returned {denial.Code}.");
        Assert.Equal("runtime_owner_denied", denial.Code);
        await AssertNativeSourceCountsAsync(schema, 1, 0);
        TraceNativeStage(failures, $"Source {loss} receiver revocation cleanup begin.");
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => receiver.DisposeAsync().AsTask());
        TraceNativeStage(failures, $"Source {loss} receiver revocation cleanup completed.");
        Assert.Equal(RuntimeBootstrapReceiverState.Disposed, receiver.State);
        output.WriteLine($"Native source {loss} loss after actual PostgreSQL wait denies without observation or accounting.");
    }

    private const string RuntimeReceiverHost = "runtime-receiver.agentweaver.svc.cluster.local";
    private const string RuntimeReceiverConfigureEndpoint =
        "https://runtime-receiver.agentweaver.svc.cluster.local:8443/runtime/v1/configure";

    internal static ProviderCandidate ResolveRuntimePlacementSandboxCandidate(
        EffectiveProviderCandidate candidate, AgentSandboxOptions sandboxOptions)
    {
        var sandboxRegistration = AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions);
        var defaultProviderId = sandboxRegistration.Descriptor.Id;
        if (candidate.Seam != ProviderSeam.Sandbox ||
            !StringComparer.Ordinal.Equals(candidate.ProviderId, defaultProviderId))
            throw new InvalidOperationException(
                "The selected Sandbox provider does not match the runtime catalog default.");

        var sandboxCatalog = ProviderCatalog.Create(
            [sandboxRegistration],
            [new(ProviderSeam.Sandbox, defaultProviderId)],
            []);
        if (!sandboxCatalog.IsSuccess)
            throw new InvalidOperationException(
                sandboxCatalog.Error?.Message ?? "The controlled Sandbox provider catalog is invalid.");
        var sandboxResolution = new ProviderResolver(sandboxCatalog.Value!).Resolve(
            new(
                ProviderSeam.Sandbox,
                null,
                Version.Parse(candidate.AdapterVersion),
                candidate.OptionsSchemaVersion,
                candidate.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)));
        if (!sandboxResolution.IsSuccess || sandboxResolution.Value?.Candidate is not { } sandboxCandidate)
            throw new InvalidOperationException(
                sandboxResolution.Error?.Message ?? "The controlled Sandbox provider is unavailable.");
        return sandboxCandidate;
    }

    private sealed class RuntimePlacementTestServer(IHost host, NpgsqlDataSource dataSource,
        HttpClient sandboxHttpClient, SandboxLeaseSnapshot lease, RuntimePlacementReadTimeProvider readTime) : IAsyncDisposable
    {
        public SandboxLeaseSnapshot Lease { get; } = lease;
        public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

        public void ExpireCurrentLease() =>
            readTime.Current = Lease.LeaseExpiresAt!.Value.AddSeconds(1);

        public async Task RetireCurrentLeaseAsync(
            SandboxRetirementAuthorization authorization, CancellationToken cancellationToken)
        {
            var lease = await new EnvironmentSandboxLeaseStore(dataSource, TimeProvider.System).BeginRetirementAsync(
                Lease.Fence, Lease.ResourceGeneration, Lease.ProviderFencingGeneration,
                SandboxRetirementReason.AuthorizedAbandon, "source-current-fence-denial",
                authorization, terminalEvidence: null, cancellationToken);
            Assert.Equal(SandboxLeaseState.Releasing, lease.State);
            Assert.True(lease.CurrentFencingGeneration > Lease.CurrentFencingGeneration);
        }

        public static async Task<RuntimePlacementTestServer> StartAsync(
            string connectionString, SecurityKey signingKey, Func<HttpMessageHandler> projectsHandler,
            EnvironmentOwnerIdentity owner, EffectiveProviderCandidate candidate, JsonElement selection,
            Func<HttpMessageHandler> outgoing)
        {
            var dataSource = NpgsqlDataSource.Create(connectionString);
            // This cross-service integration can exceed the sandbox's short production lease.
            var leaseTimeProvider = new RuntimeLeaseTestTimeProvider();
            var readTimeProvider = new RuntimePlacementReadTimeProvider();
            HttpClient? sandboxHttpClient = null;
            IHost? host = null;
            try
            {
                var dbOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
                    .UseNpgsql(dataSource, options => options.MigrationsHistoryTable(
                        "__ef_migrations_history", EnvironmentDbContext.Schema)).Options;
                await EnvironmentMigrator.MigrateAsync(dataSource, dbOptions);
                var lifecycleStore = new EnvironmentLifecycleStore(dataSource, TimeProvider.System);
                var lifecycle = await new EnvironmentLifecycleProducer(lifecycleStore)
                    .RegisterAsync(owner, "runtime-register", default);
                var leaseStore = new EnvironmentSandboxLeaseStore(dataSource, leaseTimeProvider);
                var sandboxOptions = CreateRuntimePlacementSandboxOptions(candidate);
                var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                var runSelection = selection.Deserialize<EffectiveNetworkPolicySelection>(jsonOptions)
                    ?? throw new InvalidOperationException("The controlled run selection is empty.");
                var selectedNetworkCandidate = runSelection.Providers
                    .Single(provider => provider.Seam == ProviderSeam.NetworkPolicy)
                    .Candidates.Single(provider => provider.Layer == NetworkPolicyLayer.L3L4);
                var networkOptions = new CiliumEgressProviderOptions(
                    "agentweaver", Version.Parse(selectedNetworkCandidate.AdapterVersion),
                    selectedNetworkCandidate.OptionsSchemaVersion, selectedNetworkCandidate.OptionsRevision,
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
                var networkStore = new RuntimePlacementNetworkTransport();
                var cilium = new CiliumEgressPolicyAdapter(networkStore, networkOptions);
                var networkSelector = EnvironmentEgressSelector.Create(
                    owner.EnvironmentId, owner.TenantId, owner.ProjectId, owner.RunId, networkOptions.Namespace);
                var compiledEgress = EgressIntentCompiler.Compile(runSelection);
                if (!compiledEgress.IsSuccess)
                    throw new InvalidOperationException(
                        compiledEgress.Failure?.Message ?? "The controlled run egress selection is invalid.");
                const long networkPolicyGeneration = 1;
                var networkEffect = await lifecycleStore.ReserveNetworkEffectAsync(
                    lifecycle.Snapshot.Fence,
                    $"{networkSelector.Namespace}/{networkSelector.PolicyName}",
                    networkPolicyGeneration,
                    0,
                    EnvironmentNetworkEffectKind.Apply,
                    "runtime-profile-resolution",
                    default);
                _ = await cilium.ApplyAsync(
                    networkSelector, compiledEgress.Intent!, networkPolicyGeneration, 0, default);
                await lifecycleStore.CompleteNetworkEffectAsync(
                    networkEffect.OperationId, lifecycle.Snapshot.Fence, true, true, default);

                var sandboxCandidate =
                    ResolveRuntimePlacementSandboxCandidate(candidate, sandboxOptions);
                Assert.Equal(candidate.Seam, sandboxCandidate.Seam);
                Assert.Equal(candidate.ProviderId, sandboxCandidate.ProviderId);
                Assert.Equal(candidate.AdapterVersion, sandboxCandidate.AdapterVersion.ToString());
                Assert.Equal(candidate.OptionsSchemaVersion, sandboxCandidate.OptionsSchemaVersion);
                Assert.Equal(candidate.OptionsRevision, sandboxCandidate.OptionsRevision);
                Assert.Equal(
                    candidate.AdvertisedCapabilities.OrderBy(capability => capability, StringComparer.Ordinal),
                    sandboxCandidate.AdvertisedCapabilities.OrderBy(capability => capability, StringComparer.Ordinal));
                Assert.Equal(
                    candidate.RequiredCapabilities.OrderBy(capability => capability, StringComparer.Ordinal),
                    sandboxCandidate.RequiredCapabilities.OrderBy(capability => capability, StringComparer.Ordinal));
                var provisionRequest = new SandboxProvisionApiRequest(
                    "runtime-workspace", 1, 0, "/workspace/agentweaver/project",
                    false, networkPolicyGeneration, "runtime-provision");
                var optionsSnapshot = JsonSerializer.SerializeToElement(sandboxOptions, jsonOptions);
                var intent = new SandboxLeaseProvisionIntent(
                    candidate.ProviderId, candidate.AdapterVersion, candidate.OptionsSchemaVersion,
                    candidate.OptionsRevision, optionsSnapshot, selection,
                    JsonSerializer.SerializeToElement(provisionRequest, jsonOptions));
                var reserved = await leaseStore.ReserveProvisionAsync(
                    lifecycle.Snapshot.Fence, provisionRequest.IdempotencyKey, intent, default);
                var lease = reserved.Lease;
                var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
                    sandboxCandidate.ProviderId, sandboxCandidate.OptionsRevision, lease.Fence,
                    lease.ResourceGeneration, lease.ProviderFencingGeneration, lease.OperationId);
                var storageResource = new ProviderResourceRef(
                    ProviderSeam.Storage, sandboxOptions.WorkspaceStorageProviderId, "workspace-claim-uid", 1);
                var workspaceSpec = new WorkspaceVolumeSpec(
                    provisionRequest.VolumeId, owner.ProjectId,
                    new(WorkspaceVolumeOwnerKind.Run, owner.RunId), owner.EnvironmentId,
                    WorkspaceVolumeBindingMode.Environment, WorkspaceVolumeAccessMode.ReadWriteOnce, 8,
                    "azure-files", WorkspaceVolumeConsistency.Strict, WorkspaceVolumeReclaimPolicy.Delete,
                    WorkspaceVolumeOwnerDeletionPolicy.Retain, []);
                var workspaceReference = new WorkspaceVolumeReference(
                    owner.ProjectId, provisionRequest.VolumeId, provisionRequest.VolumeResourceGeneration);
                var workspaceMount = new WorkspaceVolumeMountDeclaration(
                    workspaceReference, provisionRequest.MountPath, provisionRequest.ReadOnly);
                var storageBinding = new WorkspaceVolumeProviderBindingSnapshot(
                    storageResource.ProviderId, "1.0.0", 1, "runtime-storage-options",
                    JsonSerializer.SerializeToElement(new { @namespace = "agentweaver" }, jsonOptions),
                    JsonSerializer.SerializeToElement(new
                    {
                        @namespace = "agentweaver",
                        claimName = "workspace-claim",
                        claimUid = "workspace-claim-uid"
                    }, jsonOptions));
                var workspaceStorage = new WorkspaceVolumeResource(
                    storageResource,
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        WorkspaceVolumeCapabilities.ReadWriteOnce,
                        WorkspaceVolumeCapabilities.ReadOnlyMount),
                    storageBinding);
                var sandboxAttachmentProfile = new WorkspaceSandboxAttachmentProfile(
                    plannedResource,
                    Enum.GetValues<WorkspaceVolumeAccessMode>().ToImmutableHashSet(),
                    ImmutableHashSet.Create(WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
                    ImmutableHashSet.Create(StringComparer.Ordinal, storageResource.ProviderId),
                    SupportsReadOnlyMounts: true);
                var workspaceNegotiation = WorkspaceVolumeAttachmentNegotiator.Negotiate(
                    owner.ProjectId,
                    owner.EnvironmentId,
                    owner.RunId,
                    workspaceSpec,
                    workspaceStorage,
                    new WorkspaceVolumeMountManifest([workspaceMount]),
                    workspaceMount,
                    plannedResource,
                    lifecycle.Snapshot.Fence,
                    provisionRequest.DataGeneration,
                    sandboxAttachmentProfile,
                    WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
                var workspaceAttachment = new SandboxWorkspaceAttachment(
                    workspaceNegotiation,
                    JsonSerializer.SerializeToElement(new AgentSandboxPersistentVolumeClaimAttachment(
                        1, storageResource.ProviderId, "agentweaver", "workspace-claim", "workspace-claim-uid"),
                        jsonOptions)).Validate();
                var sandboxProvisionRequest = new SandboxProvisionRequest(
                    lifecycle.Snapshot.Fence,
                    sandboxCandidate,
                    lease.ResourceGeneration,
                    lease.ProviderFencingGeneration,
                    lease.OperationId,
                    networkSelector.MatchLabels,
                    workspaceAttachment).Validate();
                var recoveryIntent = JsonSerializer.SerializeToElement(new
                {
                    contractVersion = 1,
                    request = provisionRequest,
                    workspace = workspaceAttachment,
                    egressSelectorLabels = networkSelector.MatchLabels,
                    workspaceAttachmentTransitionRevision = 2
                }, jsonOptions);
                lease = await leaseStore.SaveProviderRequestAsync(
                    lease.OperationId, lifecycle.Snapshot.Fence, recoveryIntent, default);
                var kubernetesTransport = new RuntimePlacementKubernetesTransport(
                    sandboxOptions, owner, provisionRequest);
                sandboxHttpClient = new HttpClient(kubernetesTransport)
                {
                    BaseAddress = new Uri("https://kubernetes.test/")
                };
                var sandboxProvider = new AgentSandboxProvider(
                    sandboxOptions, new KubernetesAgentSandboxClient(sandboxHttpClient));
                var provisioned = await sandboxProvider.ProvisionAsync(sandboxProvisionRequest);
                kubernetesTransport.AddReadySandbox(
                    provisioned,
                    lease.CreatedAt ?? throw new InvalidOperationException(
                        "The controlled Sandbox lease has no creation timestamp."));
                lease = await leaseStore.CompleteProvisionAsync(
                    lease.OperationId, lease.Fence, provisioned, true, default);
                var profile = new EnvironmentRuntimeBootstrapProfile(
                    "runtime-profile", new(RuntimeReceiverConfigureEndpoint),
                    new("https://orchestrator.test/internal/runtime/observations"));
                host = await new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(dataSource);
                        services.AddSingleton<TimeProvider>(readTimeProvider);
                        services.AddScoped<IEnvironmentLifecycleStore, EnvironmentLifecycleStore>();
                        services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
                        services.AddScoped<EnvironmentRuntimePlacementReader>();
                        services.AddScoped<EnvironmentSandboxManager>();
                        services.AddSingleton(sandboxOptions);
                        services.AddSingleton<ISandboxProvider>(sandboxProvider);
                        services.AddScoped<EnvironmentEgressManager>();
                        services.AddScoped<EnvironmentWorkspaceVolumeManager>();
                        services.AddSingleton(networkOptions);
                        services.AddSingleton<ICiliumPolicyResourceStore>(networkStore);
                        services.AddScoped<CiliumEgressPolicyAdapter>();
                        services.AddScoped<IEnvironmentRuntimeBootstrapProfileReplacementStore,
                            EnvironmentRuntimeBootstrapProfileReplacementStore>();
                        services.AddHttpClient<EnvironmentRuntimeEndpointVerifier>()
                            .ConfigurePrimaryHttpMessageHandler(() => new RuntimeEndpointHealthTransport());
                        services.AddSingleton(new EnvironmentRuntimeBootstrapProfileRegistry(
                            [new(owner, profile, provisioned.Resource)]));
                        services.AddSingleton(new EnvironmentRuntimeBootstrapDeliveryOptions(
                            new("https://orchestrator.test/"), new("https://broker.test/")));
                        services.AddHttpClient<EnvironmentRuntimeBootstrapDelivery>()
                            .ConfigurePrimaryHttpMessageHandler(outgoing);
                        services.AddHttpClient<EnvironmentRuntimeOwnerContextClient>()
                            .ConfigurePrimaryHttpMessageHandler(outgoing);
                        services.AddHttpClient<IProjectsConfigClient, ProjectsConfigHttpClient>(
                            client => client.BaseAddress = new Uri("https://projects.test/"))
                            .ConfigurePrimaryHttpMessageHandler(projectsHandler);
                        services.ConfigureHttpJsonOptions(options =>
                        {
                            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                        });
                        AddJwtBearer(services, signingKey);
                        services.AddAuthorization();
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapEnvironmentEndpoints();
                            endpoints.MapEnvironmentRuntimeBootstrap();
                        });
                    });
                }).StartAsync();
                return new(host, dataSource, sandboxHttpClient!, lease, readTimeProvider);
            }
            catch
            {
                host?.Dispose();
                sandboxHttpClient?.Dispose();
                await dataSource.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            sandboxHttpClient.Dispose();
            await dataSource.DisposeAsync();
        }
    }

    private sealed class RuntimePlacementReadTimeProvider : TimeProvider
    {
        public DateTimeOffset? Current { get; set; }
        public override DateTimeOffset GetUtcNow() => Current ?? TimeProvider.System.GetUtcNow();
    }

    private sealed class RuntimeExpiryTimeProvider : TimeProvider
    {
        private DateTimeOffset? _current;

        public void SetUtcNow(DateTimeOffset current) => _current = current;

        public override DateTimeOffset GetUtcNow() => _current ?? TimeProvider.System.GetUtcNow();
    }

    private sealed class RuntimeLeaseTestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            TimeProvider.System.GetUtcNow().AddMinutes(5);
    }

    private sealed class RuntimePlacementNetworkTransport : ICiliumPolicyResourceStore
    {
        private CiliumNetworkPolicyDocument? _policy;

        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token) =>
            Task.FromResult(_policy is { } policy &&
                policy.Metadata.Namespace == ns && policy.Metadata.Name == name ? policy : null);

        public Task<CiliumNetworkPolicyDocument> CreateAsync(
            CiliumNetworkPolicyDocument policy, CancellationToken token)
        {
            if (_policy is not null)
                throw new InvalidOperationException("The controlled runtime network policy already exists.");
            var created = policy with
            {
                Metadata = policy.Metadata with { ResourceVersion = "1", Generation = 1 }
            };
            _policy = created;
            return Task.FromResult(created);
        }

        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string version, CancellationToken token)
        {
            var current = _policy;
            if (current?.Metadata.ResourceVersion != version)
                throw new InvalidOperationException("The controlled runtime network policy version changed.");
            var updated = policy with
            {
                Metadata = policy.Metadata with
                {
                    ResourceVersion = "2",
                    Generation = (current.Metadata.Generation ?? 0) + 1
                }
            };
            _policy = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteAsync(string ns, string name, string version, CancellationToken token)
        {
            if (_policy is null || _policy.Metadata.Namespace != ns ||
                _policy.Metadata.Name != name || _policy.Metadata.ResourceVersion != version)
                throw new InvalidOperationException("The controlled runtime network policy changed.");
            _policy = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeEndpointHealthTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/health/live")
                throw new InvalidOperationException("The controlled runtime health transport received an unexpected request.");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"status\":\"live\"}", Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class RuntimePlacementKubernetesTransport : HttpMessageHandler
    {
        private readonly AgentSandboxOptions _options;
        private readonly Dictionary<string, JsonNode> _resources = new(StringComparer.Ordinal);
        private JsonNode? _pods;
        private JsonNode? _events;

        public RuntimePlacementKubernetesTransport(
            AgentSandboxOptions options, EnvironmentOwnerIdentity owner, SandboxProvisionApiRequest provisionRequest)
        {
            _options = options;
            _resources["api/v1/namespaces/agentweaver/persistentvolumeclaims/workspace-claim"] =
                JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "v1",
                    kind = "PersistentVolumeClaim",
                    metadata = new
                    {
                        name = "workspace-claim",
                        @namespace = "agentweaver",
                        uid = "workspace-claim-uid",
                        annotations = new Dictionary<string, string>
                        {
                            ["agentweaver.dev/project-id"] = owner.ProjectId,
                            ["agentweaver.dev/volume-id"] = provisionRequest.VolumeId,
                            ["agentweaver.dev/generation"] =
                                provisionRequest.VolumeResourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["agentweaver.dev/environment-id"] = owner.EnvironmentId
                        }
                    },
                    status = new { phase = "Bound" }
                })!;
            _resources["apis/node.k8s.io/v1/runtimeclasses/kata-test"] =
                JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "node.k8s.io/v1",
                    kind = "RuntimeClass",
                    metadata = new { name = "kata-test" },
                    handler = "kata-test"
                })!;
        }

        public void AddReadySandbox(SandboxProvisionedResource provisioned, DateTimeOffset leaseCreatedAt)
        {
            var descriptor = provisioned.ProviderBinding.ReleaseDescriptor;
            var kubernetesNamespace = descriptor.GetProperty("namespace").GetString()!;
            var claimName = descriptor.GetProperty("claimName").GetString()!;
            var templateName = descriptor.GetProperty("templateName").GetString()!;
            var claimPath = $"apis/extensions.agents.x-k8s.io/v1beta1/namespaces/{kubernetesNamespace}/sandboxclaims/{claimName}";
            var templatePath = $"apis/extensions.agents.x-k8s.io/v1beta1/namespaces/{kubernetesNamespace}/sandboxtemplates/{templateName}";
            var claim = _resources[claimPath];
            claim["status"] = new JsonObject
            {
                ["sandbox"] = new JsonObject { ["name"] = "sandbox-a" }
            };

            const string sandboxName = "sandbox-a";
            const string sandboxUid = "sandbox-uid";
            const string podName = "sandbox-pod";
            const string podUid = "pod-uid";
            const string serviceName = "sandbox-a";
            const string podAddress = "10.2.3.4";
            var claimUid = provisioned.Resource.ResourceId;
            var sandboxPath =
                $"apis/agents.x-k8s.io/v1beta1/namespaces/{kubernetesNamespace}/sandboxes/{sandboxName}";
            _resources[sandboxPath] = JsonSerializer.SerializeToNode(new
            {
                apiVersion = "agents.x-k8s.io/v1beta1",
                kind = "Sandbox",
                metadata = new
                {
                    name = sandboxName,
                    @namespace = kubernetesNamespace,
                    uid = sandboxUid,
                    ownerReferences = new[] { new { uid = claimUid, controller = true } }
                },
                status = new
                {
                    service = serviceName,
                    serviceFQDN = $"{serviceName}.{kubernetesNamespace}.svc.cluster.local"
                }
            })!;

            var template = _resources[templatePath];
            var podTemplate = template["spec"]!["podTemplate"]!;
            var labels = podTemplate["metadata"]!["labels"]!.DeepClone().AsObject();
            labels["agents.x-k8s.io/claim-uid"] = claimUid;
            var startedAt = leaseCreatedAt.AddSeconds(2);
            var pulledAt = leaseCreatedAt.AddSeconds(1);
            var pod = JsonSerializer.SerializeToNode(new
            {
                apiVersion = "v1",
                kind = "Pod",
                metadata = new
                {
                    name = podName,
                    @namespace = kubernetesNamespace,
                    uid = podUid,
                    labels,
                    ownerReferences = new[] { new { uid = sandboxUid, controller = true } }
                },
                spec = podTemplate["spec"]!.DeepClone(),
                status = new
                {
                    phase = "Running",
                    podIP = podAddress,
                    conditions = new[]
                    {
                        new
                        {
                            type = "PodScheduled",
                            status = "True",
                            lastTransitionTime = leaseCreatedAt.AddMilliseconds(500)
                        }
                    },
                    containerStatuses = new[]
                    {
                        new
                        {
                            name = "agenthost",
                            imageID = _options.ContainerImage,
                            ready = true,
                            state = new { running = new { startedAt } }
                        }
                    }
                }
            })!;
            _pods = new JsonObject
            {
                ["items"] = new JsonArray(pod)
            };
            _events = JsonSerializer.SerializeToNode(new
            {
                items = new[]
                {
                    new
                    {
                        reason = "Pulled",
                        message = $"Successfully pulled image {_options.ContainerImageDigest}",
                        involvedObject = new { uid = podUid },
                        eventTime = pulledAt
                    }
                }
            })!;

            _resources[$"api/v1/namespaces/{kubernetesNamespace}/services/{serviceName}"] =
                JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "v1",
                    kind = "Service",
                    metadata = new
                    {
                        name = serviceName,
                        @namespace = kubernetesNamespace,
                        uid = "service-uid",
                        ownerReferences = new[] { new { kind = "Sandbox", uid = sandboxUid, controller = true } }
                    },
                    spec = new
                    {
                        type = "ClusterIP",
                        selector = new Dictionary<string, string>
                        {
                            ["agentweaver.io/service"] = "agenthost"
                        },
                        ports = new[]
                        {
                            new { name = "https", port = 8443, targetPort = "https", protocol = "TCP" }
                        }
                    }
                })!;
            var configureEndpoint = RuntimeReceiverConfigureEndpoint;
            _resources[$"api/v1/namespaces/{kubernetesNamespace}/configmaps/{_options.AgentHost!.ConfigurationMapName}"] =
                JsonSerializer.SerializeToNode(new
                {
                    apiVersion = "v1",
                    kind = "ConfigMap",
                    metadata = new
                    {
                        name = _options.AgentHost.ConfigurationMapName,
                        @namespace = kubernetesNamespace
                    },
                    immutable = true,
                    data = new Dictionary<string, string>
                    {
                        ["appsettings.json"] = JsonSerializer.Serialize(new
                        {
                            AgentHost = new
                            {
                                ConfigureEndpoint = configureEndpoint,
                                Authentication = new { Audience = configureEndpoint }
                            }
                        })
                    }
                })!;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.TrimStart('/')
                ?? throw new InvalidOperationException("The controlled Kubernetes request has no path.");
            if (request.Method == HttpMethod.Get)
            {
                if (path == "api/v1/namespaces/agentweaver/pods")
                    return JsonResponse(_pods ?? new JsonObject { ["items"] = new JsonArray() });
                if (path == "api/v1/namespaces/agentweaver/events")
                    return JsonResponse(_events ?? new JsonObject { ["items"] = new JsonArray() });
                return _resources.TryGetValue(path, out var resource)
                    ? JsonResponse(resource)
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                    ?? throw new InvalidOperationException("The controlled Kubernetes create body is empty.");
                var metadata = body["metadata"]!.AsObject();
                var name = metadata["name"]!.GetValue<string>();
                var resourceType = path[(path.LastIndexOf('/') + 1)..];
                metadata["uid"] = resourceType switch
                {
                    "sandboxtemplates" => "sandbox-template-uid",
                    "sandboxwarmpools" => "sandbox-warm-pool-uid",
                    "sandboxclaims" => name,
                    _ => throw new InvalidOperationException(
                        $"Unexpected controlled Kubernetes create resource '{resourceType}'.")
                };
                metadata["generation"] = 1;
                metadata["resourceVersion"] = "1";
                _resources[$"{path}/{Uri.EscapeDataString(name)}"] = body;
                return JsonResponse(body, HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Patch)
                return new HttpResponseMessage(HttpStatusCode.OK);

            throw new InvalidOperationException(
                $"Unexpected controlled Kubernetes request '{request.Method} {path}'.");
        }

        private static HttpResponseMessage JsonResponse(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
    }
}
