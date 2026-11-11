extern alias OrchestratorHost;
extern alias ProjectsConfig;

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
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
    private async Task VerifyProductionChildStartupAsync(
        string projectId,
        string runToken,
        long version,
        WorkPlan plan,
        ProjectRunSelectionResponse selection,
        JsonElement selectionJson,
        SecurityKey signingKey,
        ProjectsConfigResourceServer projects,
        ControlledCopilotConnection connection,
        ConcurrentDictionary<string, Func<HttpMessageHandler>> routes,
        ConcurrentQueue<string> failures,
        ConcurrentDictionary<string, RuntimePlacementTestServer> environments,
        ConcurrentDictionary<Guid, string> runtimeEnvironments,
        WebApplicationFactory<OrchestratorHost::Program> orchestrator,
        HttpClient client,
        NpgsqlDataSource dataSource,
        string ownerSchema,
        RuntimeRunStartOwnerProbe starts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = timeout.Token;
        var receiverReady = new TaskCompletionSource<HttpMessageHandler>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessFault = new ChildReadinessFault();
        var bootstrapFault = new LostBootstrapReply();
        var endpoint = new Uri($"https://child-{Guid.NewGuid():N}.test:8443/runtime/v1/configure");
        routes[endpoint.Host] = () => readinessFault.CreateHandler(
            new RuntimeRunStartDeferredHandler(receiverReady.Task));
        RuntimePlacementTestServer? childEnvironment = null;
        RuntimeAgentHost? host = null;
        IHost? receiver = null;
        HttpClient? ownerHttp = null;
        HttpClient? hostHttp = null;
        await using var sdk = new ControlledCopilotRuntime(TimeSpan.FromMinutes(6))
        {
            EmitUsageAfterCreate = false,
            EmitUsageWithTurn = true,
            EmitNativeCompletionReceipt = true,
            PersistNativeSessionState = true,
            ExpectedAvailableToolsCount = 7,
            ExpectedPrompt = Assert.Single(plan.Items).Task,
            ModelId = "gpt-6.1-sol",
            SdkCredential = connection.AccessToken,
            BeforeTurnResponse = token => releaseTurn.Task.WaitAsync(token)
        };
        await using var brokerFactory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: settings =>
            {
                for (var index = 0; index < ProjectScopes.Length; index++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{index + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        ProjectScopes[index];
                settings["IdentityBroker__RuntimeBootstrap__OrchestratorOwnerAddress"] = "https://orchestrator.test/";
                settings["IdentityBroker__RuntimeBootstrap__EnvironmentOwnerAddress"] = "https://environment.test/";
                settings["IdentityBroker__RuntimeBootstrap__BootstrapLifetime"] = "00:01:00";
                settings["IdentityBroker__RuntimeBootstrap__SourceLifetime"] = "00:10:00";
                ConfigureRunBindingSettings(settings);
                connection.ConfigureSettings(settings);
            },
            configureServices: services =>
            {
                services.AddSingleton<TimeProvider>(TimeProvider.System);
                services.RemoveAll<Microsoft.Extensions.Logging.ILoggerProvider>();
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
        routes["broker.test"] = () => bootstrapFault.CreateHandler(brokerFactory.Server.CreateHandler());
        starts.DropNextChildPlacementReply();
        starts.SetProvisioner(async (owner, request, incoming, token) =>
        {
            var environmentId = RuntimeRunStartKeys.EnvironmentId(owner);
            if (childEnvironment is null)
            {
                childEnvironment = await RuntimePlacementTestServer.StartAsync(
                    _connectionString, signingKey, projects.CreateHandler,
                    new(owner.TenantId, owner.ProjectId, owner.RunId, environmentId),
                    Assert.Single(Assert.Single(selection.Providers, p => p.Seam == ProviderSeam.Sandbox).Candidates),
                    selectionJson, () => new RuntimeServiceRouter(routes, failures),
                    configureEndpoint: endpoint.ToString(),
                    leaseClockStart: DateTimeOffset.UtcNow,
                    mountPath: "/workspace",
                    volumeId: RuntimeRunStartKeys.WorkspaceVolumeId(owner),
                    startupBudgets: new AgentSandboxStartupBudgets(120, 120, 120, 120, 120, 600),
                    firstStartWorkspace: true,
                    provisionFirstStartOnDemand: true);
                Assert.True(environments.TryAdd(environmentId, childEnvironment));
            }
            using var environmentClient = new HttpClient(childEnvironment.CreateHandler())
            {
                BaseAddress = new Uri("https://environment.test/")
            };
            using var placementRequest = new HttpRequestMessage(
                HttpMethod.Post, "/internal/runtime/run-start/child-placement")
            {
                Content = JsonContent.Create(request, options: OrchestratorJsonOptions)
            };
            AddBearerAndTenant(placementRequest, incoming.Headers.Authorization!.Parameter!, owner.TenantId);
            using var placementResponse = await environmentClient.SendAsync(placementRequest, token);
            await AssertStatusAsync(placementResponse, HttpStatusCode.OK, string.Join('\n', failures));
            return await ReadJsonAsync<RuntimeRunStartPlacementReceipt>(placementResponse);
        }, replayToOwner: true);
        var rootPath = $"/api/projects/{projectId}/runs/{RunId}/coordination/sessions/root";
        var loseNextDispatchReply = 0;
        var lostDispatchReplyCount = 0;
        async Task<HttpResponseMessage> DispatchAsync(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, rootPath + "/actions/dispatch")
            {
                Content = JsonContent.Create(
                    new MafExecutionDispatchRequest(version, plan.Id), options: CoordinationJsonOptions)
            };
            AddBearerAndTenant(request, runToken, TenantId);
            var response = await client.SendAsync(request, token);
            if (Interlocked.Exchange(ref loseNextDispatchReply, 0) == 1 && response.IsSuccessStatusCode)
            {
                response.Dispose();
                Interlocked.Increment(ref lostDispatchReplyCount);
                throw new HttpRequestException("The child completion reply was lost.");
            }
            return response;
        }
        async Task<long> ChildRegistrationCountAsync()
        {
            await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"""
                SELECT count(*) FROM "{ownerSchema}".runtime_registration_heads h
                JOIN "{ownerSchema}".runtime_registration_revisions r
                  ON r.runtime_instance_id = h.runtime_instance_id AND r.revision = h.current_revision
                WHERE r.binding_json::jsonb ->> 'ProjectId' = @project
                  AND r.binding_json::jsonb ->> 'RunId' = @run
                  AND r.binding_json::jsonb ->> 'SessionId' <> 'root'
                """, db);
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("run", RunId);
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        try
        {
            Assert.Equal(0, await ChildRegistrationCountAsync());
            using var firstDispatch = await DispatchAsync(cancellationToken);
            await AssertStatusAsync(firstDispatch, HttpStatusCode.OK, string.Join('\n', failures));
            var firstResult = await ReadJsonAsync<MafExecutionDispatchResponse>(firstDispatch);
            Assert.Equal("pending", Assert.Single(firstResult.ChildRuntimeStartup).Value.State);
            Assert.Empty(firstResult.CompletedResults);
            _ = await starts.WaitForPlacementAsync();
            var firstPlacement = Assert.Single(starts.PlacementAttempts);
            var owner = Assert.IsType<RuntimeOwnerContext>(firstPlacement.Owner);
            var store = new CoordinationRuntimeRunStartStore(dataSource, ownerSchema);
            RuntimeRunStartIntent? intent;
            do
            {
                intent = await store.ReadIntentForStatusAsync(
                    projectId, RunId, owner.SessionId, cancellationToken);
                if (intent?.FailureCode is null)
                    await Task.Delay(50, cancellationToken);
            }
            while (intent?.FailureCode is null);
            Assert.Equal("placement", intent.CurrentStep);
            Assert.Equal(0, await ChildRegistrationCountAsync());
            Assert.False(sdk.TurnReceived.Task.IsCompleted);
            foreach (var staleOwner in new[]
            {
                owner with { ActorId = "foreign-owner" },
                owner with { AgentId = "foreign-agent" },
                owner with { ModelSelectionReference = "foreign-model" },
                owner with { AcceptedSelectionHash = new string('b', 64) },
                owner with { ExecutionFence = owner.ExecutionFence + 1 },
                owner with { OwnerStateVersion = owner.OwnerStateVersion + 1 }
            })
                await Assert.ThrowsAsync<CoordinationException>(() =>
                    store.EnsureChildIntentAsync(staleOwner, cancellationToken));
            Assert.Equal(1, childEnvironment!.SandboxClaimCreateCount);
            Assert.False(sdk.TurnReceived.Task.IsCompleted);

            // Dispatch must not wait while another owner holds the startup lease.
            await using (var held = await store.AcquireAsync(intent, cancellationToken))
            {
                using var busyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                busyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var busy = await DispatchAsync(busyTimeout.Token);
                await AssertStatusAsync(busy, HttpStatusCode.OK, string.Join('\n', failures));
                var busyResult = await ReadJsonAsync<MafExecutionDispatchResponse>(busy);
                Assert.Equal("pending", Assert.Single(busyResult.ChildRuntimeStartup).Value.State);
                Assert.Single(starts.PlacementAttempts);
            }

            using var replay = await DispatchAsync(cancellationToken);
            await AssertStatusAsync(replay, HttpStatusCode.OK, string.Join('\n', failures));
            RuntimeRegistration? registration = null;
            var registrationPath = $"/internal/projects/{projectId}/runs/{RunId}" +
                $"/coordination/sessions/{owner.SessionId}/runtime-registrations";
            while (registration is null)
            {
                using var response = await SendAsync(
                    client, HttpMethod.Get, registrationPath, runToken, [TenantId],
                    cancellationToken: cancellationToken);
                if (response.IsSuccessStatusCode)
                    registration = await ReadJsonAsync<RuntimeRegistration>(response);
                else
                    await Task.Delay(50, cancellationToken);
            }
            Assert.Equal(owner.SessionId, registration.Binding.SessionId);
            Assert.Equal(owner.AgentId, registration.Binding.AgentId);
            Assert.Equal(owner.ModelSelectionReference, registration.Binding.ModelSelectionReference);
            Assert.Equal(owner.AcceptedSelectionHash, registration.Binding.AcceptedSelectionHash);
            Assert.Equal(owner.ExecutionFence, registration.Binding.ExecutionFence);
            Assert.True(runtimeEnvironments.TryAdd(registration.RuntimeInstanceId, registration.Binding.EnvironmentId));
            Assert.Equal(1, await ChildRegistrationCountAsync());

            var workspace = Path.GetFullPath(Path.Combine(
                ".scratch", "production-child-workspace", registration.RuntimeInstanceId.ToString("N")));
            Directory.CreateDirectory(workspace);
            var modelBindings = new RuntimeModelBindingsResolver(
                "native-model-bindings-v1", new Dictionary<string, RuntimeModelBinding>
                {
                    [registration.Binding.ModelSelectionReference!] = new(sdk.ModelId, ModelSourceMode.HostedCopilot)
                });
            var sessions = new RuntimeCopilotSessionFactory(
                sdk.Connection, workspace, modelBindings, workingDirectory: workspace);
            ownerHttp = new HttpClient(new RuntimeServiceRouter(routes, failures))
            {
                BaseAddress = new Uri("https://orchestrator.test/")
            };
            var currentOwner = new RuntimeRegistrationHttpClient(ownerHttp, new("https://orchestrator.test/"));
            hostHttp = new HttpClient(new RuntimeServiceRouter(routes, failures,
                async (request, response, token) =>
                {
                    if (!response.IsSuccessStatusCode ||
                        request.RequestUri is not { Host: "environment.test", AbsolutePath: var path } ||
                        !(path.EndsWith("/readiness", StringComparison.Ordinal) ||
                          path.EndsWith("/pod-observation", StringComparison.Ordinal)))
                        return;
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                    if (path.EndsWith("/readiness", StringComparison.Ordinal))
                        body["workspaceMountPath"] = workspace;
                    else
                        body["workspaceAttachment"]!["mountPath"] = workspace;
                    response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }));
            host = new RuntimeAgentHost(
                new RuntimeAgentHostOptions(endpoint, broker.BaseAddress!, new("https://orchestrator.test/"),
                    new("https://environment.test/"), new("https://events.test/"),
                    new("https://projects.test/"), registration.Binding.Image!, 2),
                hostHttp, currentOwner, sessions, TimeProvider.System);
            receiver = await new HostBuilder().ConfigureWebHost(web =>
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
                        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapRuntimeAgentHost(host, TimeProvider.System));
                });
            }).StartAsync(cancellationToken);
            receiverReady.SetResult(receiver.GetTestServer().CreateHandler());
            await bootstrapFault.Dropped.Task.WaitAsync(cancellationToken);
            do
            {
                intent = await store.ReadIntentForStatusAsync(
                    projectId, RunId, owner.SessionId, cancellationToken);
                if (intent?.CurrentStep != "bootstrap" || intent.FailureCode is null)
                    await Task.Delay(50, cancellationToken);
            }
            while (intent?.CurrentStep != "bootstrap" || intent.FailureCode is null);
            Assert.False(sdk.TurnReceived.Task.IsCompleted);
            using var bootstrapReplay = await DispatchAsync(cancellationToken);
            await AssertStatusAsync(bootstrapReplay, HttpStatusCode.OK, string.Join('\n', failures));
            do
            {
                intent = await store.ReadIntentForStatusAsync(
                    projectId, RunId, owner.SessionId, cancellationToken);
                if (intent?.State != "ready")
                    await Task.Delay(100, cancellationToken);
            }
            while (intent?.State != "ready");
            Assert.Equal(firstPlacement.BootstrapOperationId, intent.BootstrapOperationId);
            Assert.Equal(1, starts.UniquePlacementCount);
            Assert.Equal(1, childEnvironment!.SandboxClaimCreateCount);
            Assert.True(starts.PlacementAttempts.Count >= 2);
            Assert.All(starts.PlacementAttempts,
                attempt => Assert.Equal(firstPlacement.BootstrapOperationId, attempt.BootstrapOperationId));
            Assert.True(bootstrapFault.OperationIds.Count >= 2);
            Assert.All(bootstrapFault.OperationIds,
                operationId => Assert.Equal(firstPlacement.BootstrapOperationId, operationId));
            using var links = JsonDocument.Parse(intent.ReceiptLinks);
            foreach (var name in new[] { "placement", "registration", "bootstrap", "activation", "readiness" })
                Assert.True(links.RootElement.TryGetProperty(name, out _), $"Missing {name} receipt.");

            readinessFault.FailNextReadiness();
            using var notReady = await DispatchAsync(cancellationToken);
            await AssertStatusAsync(notReady, HttpStatusCode.OK, string.Join('\n', failures));
            var notReadyResult = await ReadJsonAsync<MafExecutionDispatchResponse>(notReady);
            Assert.NotEqual("ready", Assert.Single(notReadyResult.ChildRuntimeStartup).Value.State);
            Assert.False(sdk.TurnReceived.Task.IsCompleted);

            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref loseNextDispatchReply, 1);
            var send = DispatchAsync(sendCancellation.Token);
            if (await Task.WhenAny(send, sdk.TurnReceived.Task) == send)
            {
                using var early = await send;
                Assert.Fail($"Dispatch returned before the SDK turn: {early.StatusCode}, " +
                    $"{await early.Content.ReadAsStringAsync(cancellationToken)}. " +
                    $"Recent routes: {string.Join(" | ", failures.TakeLast(8))}.");
            }
            await sdk.TurnReceived.Task.WaitAsync(cancellationToken);
            Assert.Single(sdk.Requests, request => request.Method == "session.create");
            Assert.Single(sdk.Requests, request => request.Method == "session.send");
            Assert.Equal(1, await ChildRegistrationCountAsync());
            releaseTurn.TrySetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => send.WaitAsync(cancellationToken));
            Assert.Equal(1, lostDispatchReplyCount);
            using var completedDispatch = await DispatchAsync(cancellationToken);
            await AssertStatusAsync(completedDispatch, HttpStatusCode.OK, string.Join('\n', failures));
            var dispatchResult = await ReadJsonAsync<MafExecutionDispatchResponse>(completedDispatch);
            Assert.False(dispatchResult.IsComplete);
            var completionEvidence = Assert.Single(dispatchResult.CompletionEvidence);
            var checkpointBinding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
                new(projectId, RunId, "root"),
                new CoordinationActor(owner.ActorIssuer, owner.ActorId),
                registration.Binding.ExecutionFence,
                MafExecutionCheckpointStore.CurrentSdkVersion,
                registration.Binding.ModelSelectionReference!);
            var checkpointStore = new MafExecutionCheckpointStore(
                orchestrator.Services.GetRequiredService<PostgresMafCheckpointStore>(),
                checkpointBinding);
            var completedCheckpoint = await checkpointStore.ReadLatestAsync(cancellationToken);
            Assert.NotNull(completedCheckpoint);
            Assert.Single(completedCheckpoint.State.Results);
            Assert.Empty(completedCheckpoint.State.PendingDispatches);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(
                    Assert.Single(completedCheckpoint.State.ChildTurnReceipts.Values), CoordinationJsonOptions),
                JsonSerializer.SerializeToElement(completionEvidence, CoordinationJsonOptions)));
            Assert.Single(sdk.Requests, request => request.Method == "session.send");
            Assert.Single(completedCheckpoint.State.ReviewIntents);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"Child startup timed out. Owner calls: {string.Join(" | ", starts.ObservedPaths.TakeLast(30))}. " +
                $"Failures: {string.Join(" | ", starts.ProvisionerFailures.Concat(starts.HandlerFailures).TakeLast(10))}. " +
                $"Logs: {string.Join(" | ", starts.LoggerMessages.TakeLast(10))}. " +
                $"Routes: {string.Join(" | ", failures.TakeLast(15))}.");
        }
        finally
        {
            releaseTurn.TrySetResult();
            if (host is not null)
            {
                try
                {
                    await host.DisposeAsync();
                }
                catch (RuntimeAuthorizationException error) when (error.Code == "runtime_owner_denied")
                {
                    // The owner may revoke the child source grant before local Host teardown.
                }
            }
            receiver?.Dispose();
            hostHttp?.Dispose();
            ownerHttp?.Dispose();
            if (childEnvironment is not null)
                await childEnvironment.DisposeAsync();
        }
    }

    private sealed class LostBootstrapReply
    {
        private readonly ConcurrentQueue<Guid> _operationIds = new();
        private int _drop = 1;

        public TaskCompletionSource Dropped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<Guid> OperationIds => _operationIds.ToArray();
        public HttpMessageHandler CreateHandler(HttpMessageHandler inner) => new Handler(inner, this);

        private sealed class Handler(HttpMessageHandler inner, LostBootstrapReply fault) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath != "/internal/runtime/bootstrap/request")
                    return await base.SendAsync(request, cancellationToken);
                var input = await request.Content!.ReadFromJsonAsync<RuntimeBootstrapRequest>(
                    OrchestratorJsonOptions, cancellationToken);
                fault._operationIds.Enqueue(Assert.IsType<RuntimeBootstrapRequest>(input).OperationId);
                var response = await base.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode && Interlocked.Exchange(ref fault._drop, 0) == 1)
                {
                    response.Dispose();
                    fault.Dropped.TrySetResult();
                    throw new HttpRequestException("The bootstrap reply was lost.");
                }
                return response;
            }
        }
    }

    private sealed class ChildReadinessFault
    {
        private int _failNext;

        public void FailNextReadiness() => Interlocked.Exchange(ref _failNext, 1);
        public HttpMessageHandler CreateHandler(HttpMessageHandler inner) => new Handler(inner, this);

        private sealed class Handler(HttpMessageHandler inner, ChildReadinessFault fault) : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath == "/runtime/v1/readiness" &&
                    Interlocked.Exchange(ref fault._failNext, 0) == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = JsonContent.Create(new { code = "runtime_host_not_ready" }),
                        RequestMessage = request
                    };
                    response.Headers.CacheControl = new() { NoStore = true };
                    return Task.FromResult(response);
                }
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
