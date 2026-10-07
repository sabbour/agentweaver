extern alias EnvironmentService;
extern alias OrchestratorHost;
extern alias ProjectsConfig;
extern alias EventsHost;

using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using EnvironmentService::Agentweaver.Environment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
        ICoordinatorSandboxResourceProvider sandboxProvider, bool revokeSourceBeforeSdk, string? sourceLoss)
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
        using var selectionDocument = JsonDocument.Parse(selectionBytes);
        var environmentOwner = new EnvironmentOwnerIdentity(
            owner.TenantId, owner.ProjectId, owner.RunId, "runtime-environment");
        var routes = new Dictionary<string, Func<HttpMessageHandler>>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        await using var environment = await RuntimePlacementTestServer.StartAsync(
            _connectionString, signingKey, projects.CreateHandler, environmentOwner,
            candidate, selectionDocument.RootElement.Clone(), () => new RuntimeServiceRouter(routes, failures));
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
            environmentHandler: () => new RuntimeServiceRouter(routes, failures),
            runtimeUsageHandler: () => new RuntimeServiceRouter(routes, failures));
        using var client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://orchestrator.test/"), AllowAutoRedirect = false
        });
        routes.Add("orchestrator.test", () => factory.Server.CreateHandler());
        var enrollmentPath = $"/internal/projects/{owner.ProjectId}/runs/{owner.RunId}" +
            $"/coordination/sessions/{owner.SessionId}/runtime-registrations";
        using var response = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath,
            runToken, new RegisterRuntimeRequest(environmentOwner.EnvironmentId, "runtime-profile"));
        await AssertStatusAsync(response, HttpStatusCode.OK);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var registration = await response.Content.ReadFromJsonAsync<RuntimeRegistration>(CoordinationJsonOptions);
        Assert.NotNull(registration);
        Assert.Equal(owner.ActorId, registration.Binding.ActorId);
        Assert.Equal(owner.TurnId, registration.Binding.TurnId);
        Assert.Equal(owner.ModelSelectionReference, registration.Binding.ModelSelectionReference);
        Assert.Equal(owner.AcceptedSelectionHash, registration.Binding.AcceptedSelectionHash);
        Assert.Equal(environment.Lease.LeaseRevision, registration.Binding.EnvironmentLeaseRevision);
        Assert.Equal(environment.Lease.ProvisionedResource!.Resource.ProviderId,
            registration.Binding.PlacementProviderId);
        Assert.Equal(environment.Lease.ProvisionedResource.Resource.ResourceId, registration.Binding.PlacementUid);
        using var current = await SendAsync(client, HttpMethod.Get,
            $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}", runToken, [owner.TenantId]);
        await AssertStatusAsync(current, HttpStatusCode.OK);
        Assert.Equal(registration, await current.Content.ReadFromJsonAsync<RuntimeRegistration>(CoordinationJsonOptions));
        await using var source = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{ownerSchema}\".runtime_registration_heads", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        using var forged = await SendJsonAsync(client, HttpMethod.Post, enrollmentPath, runToken,
            new { environmentOwner.EnvironmentId, ProfileId = "runtime-profile", DesiredModel = "foreign" });
        await AssertStatusAsync(forged, HttpStatusCode.BadRequest);
        await VerifyCurrentRuntimeDeliveryAndNativeSessionAsync(
            registration, signingKey, runToken, routes, failures, projects, events.Schema, ownerSchema,
            revokeSourceBeforeSdk, sourceLoss, environment);
    }

    private async Task VerifyCurrentRuntimeDeliveryAndNativeSessionAsync(
        RuntimeRegistration registration, SecurityKey signingKey, string runToken,
        Dictionary<string, Func<HttpMessageHandler>> routes, ConcurrentQueue<string> failures,
        ProjectsConfigResourceServer projects, string eventsSchema, string ownerSchema, bool revokeSourceBeforeSdk,
        string? sourceLoss, RuntimePlacementTestServer environment)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
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
                    sourceLoss == "sdk-preparation-expiry" ? "00:00:10" : "00:02:00";
            },
            configureServices: services =>
            {
                services.AddHttpClient(nameof(RuntimeRegistrationHttpClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new RuntimeServiceRouter(routes, failures));
                services.AddHttpClient(nameof(BrokerRuntimeBootstrapDeliveryClient))
                    .ConfigurePrimaryHttpMessageHandler(() => new RuntimeServiceRouter(routes, failures));
            });
        using var broker = brokerFactory.CreateClient(new()
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://broker.test/")
        });
        routes.Add("broker.test", () => brokerFactory.Server.CreateHandler());
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? inspectOwnerResponse = null;
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, Task>? inspectRuntimeResponse = null;
        using var owners = new HttpClient(new RuntimeServiceRouter(routes, failures,
            (request, response, token) => inspectOwnerResponse?.Invoke(request, response, token) ?? Task.CompletedTask));
        var currentOwner = new RuntimeRegistrationHttpClient(owners, new("https://orchestrator.test/"));
        await using var sdk = new ControlledCopilotRuntime();
        using var runtimeHttp = new HttpClient(new RuntimeServiceRouter(routes, failures,
            (request, response, token) => inspectRuntimeResponse?.Invoke(request, response, token) ?? Task.CompletedTask))
        {
            BaseAddress = broker.BaseAddress
        };
        await using var receiver = new RuntimeBootstrapReceiver(
            registration, currentOwner, broker, broker.BaseAddress!, TimeProvider.System);
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
        routes.Add("runtime.test", () => host.GetTestServer().CreateHandler());
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
            using var deliveryResponse = await SendJsonAsync(
                broker, HttpMethod.Post, "/internal/runtime/bootstrap/request", runToken, input);
            Assert.True(deliveryResponse.StatusCode == HttpStatusCode.OK,
                await deliveryResponse.Content.ReadAsStringAsync() + "\n" + string.Join('\n', failures));
            var delivered = await deliveryResponse.Content.ReadFromJsonAsync<RuntimeBootstrapDeliveryReceipt>(
                CoordinationJsonOptions);
            Assert.NotNull(delivered);
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            failures.Enqueue($"After delivery: {elapsed.Elapsed}");
            output.WriteLine(failures.Last());
            Assert.Equal(registration.RuntimeInstanceId, delivered.RuntimeInstanceId);
            var replay = await RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapDeliveryReceipt>(
                broker, broker.BaseAddress!, "/internal/runtime/bootstrap/request", actor, input, default);
            Assert.Equal(delivered, replay);
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            var factory = new RuntimeCopilotSessionFactory(
                sdk.Connection, Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
                new Dictionary<string, string> { [registration.Binding.ModelSelectionReference!] = sdk.ModelId });
            var runtimeBroker = new RuntimeBrokerCredentialClient(
                runtimeHttp, broker.BaseAddress!, IdentityBrokerWebApplicationFactory.Issuer, actor, TimeProvider.System);
            var bootstrap = new RuntimeSessionBootstrap(currentOwner, runtimeBroker, factory, actor, TimeProvider.System);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => receiver.ConfigureAsync(
                bootstrap, "forged"u8.ToArray(), Guid.NewGuid(), Guid.NewGuid(),
                RuntimeCopilotSessionTests.SdkCredential(), default));
            Assert.Equal(RuntimeBootstrapReceiverState.Pending, receiver.State);
            Assert.Empty(sdk.Requests);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
                    exchanged = new(grant.GrantId, grant.RuntimeInstanceId, grant.Revision, grant.Purpose,
                        grant.Audience, grant.ConfigurationHash,
                        new SecretCredential(exchange.CredentialValue, grant.ExpiresAt));
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
                    RuntimeCopilotSessionTests.SdkCredential(), timeout.Token);
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
                            var remaining = exchanged.Credential.ExpiresAt - DateTimeOffset.UtcNow;
                            if (remaining > TimeSpan.Zero)
                                await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), timeout.Token);
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
                    Assert.True(revision.ExpiresAt <= DateTimeOffset.UtcNow);
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
            var session = await receiver.ConfigureAsync(
                bootstrap, configuration, Guid.NewGuid(), Guid.NewGuid(),
                RuntimeCopilotSessionTests.SdkCredential(), timeout.Token);
            Assert.Equal(RuntimeBootstrapReceiverState.Ready, receiver.State);
            failures.Enqueue($"After SDK configure: {elapsed.Elapsed}");
            output.WriteLine(failures.Last());
            Assert.Equal(registration, session.Registration);
            Assert.Equal(registration.Binding.ModelSelectionReference, session.Facts.ModelSelectionReference);
            Assert.Equal(registration.Binding.AcceptedSelectionHash, session.Facts.AcceptedSelectionHash);
            var sourceClient = new RuntimeUsageSourceHttpClient(
                runtimeHttp, new("https://orchestrator.test/"), actor);
            if (sourceLoss is not null)
            {
                await VerifyNativeSourceAuthorityAfterWaitAsync(
                    sourceLoss, session, sourceClient, runtimeBroker, receiver, environment, ownerSchema,
                    projects, runToken, timeout.Token);
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
            Assert.Equal(1234567.25m, accepted.Usage.Measurement.ProviderUnits);
            Assert.Equal(12.5m, accepted.Usage.Measurement.DurationMilliseconds);
            Assert.Null(accepted.Usage.Measurement.RequestCount);
            Assert.Equal(2.5m, session.Facts.ModelMultiplier);
            await sdk.EmitUsageAsync(sdk.UsageData());
            Assert.True(await usage.MoveNextAsync());
            Assert.Equal(accepted, usage.Current);
            var observation = new SdkUsageObservation(
                accepted.Usage.EventId, accepted.Usage.SdkEventId!, session.Facts.SdkSessionId,
                accepted.Usage.OccurredAt, session.Facts.ModelId, 17, 11, 7, 5, 3, 1234567.25m, 12.5m);
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
            await receiver.DisposeAsync();
            Assert.Equal(RuntimeBootstrapReceiverState.Disposed, receiver.State);
            await VerifyNativeUsageAccountingAsync(
                accepted, signingKey, runToken, projects, routes, failures, eventsSchema);
            output.WriteLine($"After actual Events priced ACK: {elapsed.Elapsed}");
        });
        var receiverCleanupFailure = await Record.ExceptionAsync(() => receiver.DisposeAsync().AsTask());
        bearer.Invalidate();
        var hostCleanupFailure = await Record.ExceptionAsync(() => host.StopAsync());
        var lifecycleFailures = new[] { primaryFailure, receiverCleanupFailure, hostCleanupFailure }
            .OfType<Exception>().ToArray();
        if (lifecycleFailures.Length > 0)
            Assert.Fail(new AggregateException("Native runtime proof and cleanup failed.", lifecycleFailures)
                + "\n" + string.Join('\n', failures));
    }

    private async Task VerifyNativeUsageAccountingAsync(
        RuntimeUsageSourceReceipt sourceReceipt, SecurityKey signingKey, string runToken,
        ProjectsConfigResourceServer projects, Dictionary<string, Func<HttpMessageHandler>> routes,
        ConcurrentQueue<string> failures, string eventsSchema)
    {
        var binding = sourceReceipt.Registration.Binding;
        using var configuration = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["EventsAndSessions__RuntimeUsage__Enabled"] = "true",
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
        await using var factory = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => new RuntimeServiceRouter(routes, failures),
            nativeUsageHandler: () => new RuntimeServiceRouter(routes, failures));
        using var client = factory.CreateClient(new()
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://events.test/")
        });
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
        Assert.Equal(0.00123456725m, accepted.Accounting.Amount);
        Assert.Equal(CostDisposition.Estimate, accepted.Accounting.Disposition);
        await AssertNativeHistoryImmutabilityAsync(eventsSchema,
            [("usage_source_receipts", "source_receipt_id"), ("usage_run_cost_bindings", "meter_source")],
            "reject_usage_receipt_mutation");
        await AssertNativeAccountingCountsAsync(eventsSchema, 1);
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
        Assert.All(Enumerable.Range(0, 5), column => Assert.Equal(1L, reader.GetInt64(column)));
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
            var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (elapsed.Elapsed > TimeSpan.FromMilliseconds(500))
                failures.Enqueue($"{endpoint.Host}{endpoint.AbsolutePath}: took {elapsed.Elapsed}");
            if (!response.IsSuccessStatusCode)
                failures.Enqueue($"{endpoint.Host}{endpoint.AbsolutePath}: {(int)response.StatusCode} " +
                    await response.Content.ReadAsStringAsync(cancellationToken));
            if (afterResponse is not null)
                await afterResponse(request, response, cancellationToken);
            return response;
        }
    }

    private async Task AssertNativeSourceCountsAsync(string schema, long sources, long observations)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{schema}".runtime_sdk_sources),
                   (SELECT count(*) FROM "{schema}".runtime_usage_observations)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(sources, reader.GetInt64(0));
        Assert.Equal(observations, reader.GetInt64(1));
    }

    private async Task AssertNativeAccountingCountsAsync(string schema, long expected)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
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
        Assert.All(Enumerable.Range(0, 5), column => Assert.Equal(expected, reader.GetInt64(column)));
    }

    private Task AssertNativeSourceImmutabilityAsync(string schema) =>
        AssertNativeHistoryImmutabilityAsync(schema,
            [("runtime_sdk_sources", "runtime_instance_id"), ("runtime_usage_observations", "receipt_id")],
            "reject_runtime_usage_mutation");

    private async Task AssertNativeHistoryImmutabilityAsync(
        string schema, (string Table, string Identity)[] tables, string rejectionFunction)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
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
        string schema, ProjectsConfigResourceServer projects, string runToken, CancellationToken cancellationToken)
    {
        await sourceClient.RegisterAsync(session, cancellationToken);
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync(cancellationToken);
        await using var transaction = await lockConnection.BeginTransactionAsync(cancellationToken);
        await using (var acquire = new NpgsqlCommand(loss == "head"
            ? "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.usage'), hashtext(@runtime))"
            : $"""LOCK TABLE "{schema}".runtime_usage_observations IN ACCESS EXCLUSIVE MODE""",
            lockConnection, transaction))
        {
            if (loss == "head")
                acquire.Parameters.AddWithValue("runtime", session.Registration.RuntimeInstanceId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var observations = session.CommitUsageAsync(sourceClient, cancellationToken).GetAsyncEnumerator();
        var append = observations.MoveNextAsync().AsTask();
        try
        {
            await using var observer = new NpgsqlConnection(_connectionString);
            await observer.OpenAsync(cancellationToken);
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
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        var denial = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => append);
        Assert.Equal("runtime_owner_denied", denial.Code);
        await AssertNativeSourceCountsAsync(schema, 1, 0);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => receiver.DisposeAsync().AsTask());
        Assert.Equal(RuntimeBootstrapReceiverState.Disposed, receiver.State);
        output.WriteLine($"Native source {loss} loss after actual PostgreSQL wait denies without observation or accounting.");
    }

    private sealed class RuntimePlacementTestServer(IHost host, NpgsqlDataSource dataSource,
        SandboxLeaseSnapshot lease) : IAsyncDisposable
    {
        public SandboxLeaseSnapshot Lease { get; } = lease;
        public HttpMessageHandler CreateHandler() => host.GetTestServer().CreateHandler();

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
            IHost? host = null;
            try
            {
                var dbOptions = new DbContextOptionsBuilder<EnvironmentDbContext>()
                    .UseNpgsql(dataSource, options => options.MigrationsHistoryTable(
                        "__ef_migrations_history", EnvironmentDbContext.Schema)).Options;
                await EnvironmentMigrator.MigrateAsync(dataSource, dbOptions);
                var lifecycle = await new EnvironmentLifecycleProducer(
                    new EnvironmentLifecycleStore(dataSource, TimeProvider.System))
                    .RegisterAsync(owner, "runtime-register", default);
                var leaseStore = new EnvironmentSandboxLeaseStore(dataSource, TimeProvider.System);
                var optionsSnapshot = JsonSerializer.SerializeToElement(new { namespaceName = "agentweaver" });
                var intent = new SandboxLeaseProvisionIntent(
                    candidate.ProviderId, candidate.AdapterVersion, candidate.OptionsSchemaVersion,
                    candidate.OptionsRevision, optionsSnapshot, selection,
                    JsonSerializer.SerializeToElement(new { transport = "controlled-external-placement" }));
                var reserved = await leaseStore.ReserveProvisionAsync(lifecycle.Snapshot.Fence, "runtime-provision", intent, default);
                var lease = reserved.Lease;
                var resource = SandboxResourceIdentity.CreatePlannedReference(
                    candidate.ProviderId, candidate.OptionsRevision, lease.Fence,
                    lease.ResourceGeneration, lease.ProviderFencingGeneration, lease.OperationId);
                var provisioned = new SandboxProvisionedResource(
                    resource, new(Guid.NewGuid()), new("controlled-placement"),
                    candidate.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal), [],
                    new(candidate.ProviderId, candidate.AdapterVersion, candidate.OptionsSchemaVersion,
                        candidate.OptionsRevision, optionsSnapshot,
                        JsonSerializer.SerializeToElement(new { claim = resource.ResourceId })));
                lease = await leaseStore.CompleteProvisionAsync(lease.OperationId, lease.Fence, provisioned, true, default);
                var profile = new EnvironmentRuntimeBootstrapProfile(
                    "runtime-profile", new("https://runtime.test/configure"),
                    new("https://orchestrator.test/internal/runtime/observations"));
                var networkOptions = new CiliumEgressProviderOptions(
                    "agentweaver", new Version(1, 0, 0), 1, "unused-network-options",
                    ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty);
                host = await new HostBuilder().ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(dataSource);
                        services.AddSingleton(TimeProvider.System);
                        services.AddScoped<IEnvironmentLifecycleStore, EnvironmentLifecycleStore>();
                        services.AddScoped<ISandboxLeaseStore, EnvironmentSandboxLeaseStore>();
                        services.AddScoped<EnvironmentRuntimePlacementReader>();
                        services.AddScoped<EnvironmentSandboxManager>();
                        services.AddSingleton(new AgentSandboxOptions(
                            1, "unused-sandbox-options", "agentweaver", "azure-files-csi",
                            "runtime-test:local", "kata-test", "kata-test", "100m", "128Mi", 20, 100));
                        services.AddHttpClient<KubernetesAgentSandboxClient>(client =>
                            client.BaseAddress = new Uri("https://kubernetes.test/"))
                            .ConfigurePrimaryHttpMessageHandler(() => new UnusedRuntimePlacementTransport());
                        services.AddScoped<ISandboxProvider, AgentSandboxProvider>();
                        services.AddScoped<EnvironmentEgressManager>();
                        services.AddScoped<EnvironmentWorkspaceVolumeManager>();
                        services.AddSingleton(networkOptions);
                        services.AddSingleton<ICiliumPolicyResourceStore, UnusedRuntimeNetworkTransport>();
                        services.AddScoped<CiliumEgressPolicyAdapter>();
                        services.AddSingleton(new EnvironmentRuntimeBootstrapProfileRegistry(
                            [new(owner, profile, resource)]));
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
                return new(host, dataSource, lease);
            }
            catch
            {
                host?.Dispose();
                await dataSource.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            await dataSource.DisposeAsync();
        }
    }

    private sealed class UnusedRuntimeNetworkTransport : ICiliumPolicyResourceStore
    {
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(
            CiliumNetworkPolicyDocument policy, string version, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
        public Task DeleteAsync(string ns, string name, string version, CancellationToken token) =>
            throw new InvalidOperationException("The placement getter must not use Kubernetes.");
    }

    private sealed class UnusedRuntimePlacementTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A current placement read must not dispatch a provider effect.");
    }
}
