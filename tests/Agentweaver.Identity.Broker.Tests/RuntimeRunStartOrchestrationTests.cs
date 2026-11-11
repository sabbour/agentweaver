extern alias OrchestratorHost;

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class RuntimeRunStartOrchestrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PostgresContainerFixture _postgres;

    public RuntimeRunStartOrchestrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

    [Fact]
    public void RegistrationMustKeepTheSelectedProfileAndAnEmptyFirstStartWorkspace()
    {
        var source = RuntimeCopilotSessionTests.Registration();
        var binding = source.Binding with
        {
            AgentId = RuntimeActionContract.CoordinatorRootStepId,
            EnvironmentProviderFencingGeneration = source.Binding.EnvironmentCurrentFencingGeneration
        };
        var registration = source with { Binding = binding };
        var owner = RuntimeRunStartContinuation.CreateOwnerContext(binding);
        var bootstrapOperationId = Guid.NewGuid();
        var placement = new RuntimeRunStartPlacementReceipt(
            1,
            binding.EnvironmentId,
            binding.ProfileId,
            "workspace",
            RuntimeRunStartKeys.SandboxIdempotencyKey(owner, bootstrapOperationId),
            1,
            0,
            1,
            Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "claim", 1),
            binding.EnvironmentLifecycleGeneration,
            binding.EnvironmentLeaseRevision,
            binding.EnvironmentCurrentFencingGeneration,
            binding.EnvironmentProviderFencingGeneration,
            DateTimeOffset.UtcNow.AddMinutes(2));

        RuntimeRunStartContinuation.RequireRegistration(owner, placement, registration);

        var changedProfile = registration with
        {
            Binding = registration.Binding with { ProfileId = "other-profile" }
        };
        var profileFailure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireRegistration(owner, placement, changedProfile));
        Assert.Equal("runtime_start_registration_stale", profileFailure.Code);

        var retainedContents = placement with { WorkspaceDataGeneration = 1 };
        var contentFailure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireRegistration(owner, retainedContents, registration));
        Assert.Equal("runtime_start_workspace_not_empty", contentFailure.Code);
        RuntimeRunStartContinuation.RequireReplacementRegistration(owner, retainedContents, registration);

        var changedFence = retainedContents with { CurrentFencingGeneration = retainedContents.CurrentFencingGeneration + 1,
            ProviderFencingGeneration = retainedContents.ProviderFencingGeneration + 1 };
        var replacementBinding = registration with
        {
            Binding = registration.Binding with
            {
                EnvironmentCurrentFencingGeneration = changedFence.CurrentFencingGeneration,
                EnvironmentProviderFencingGeneration = changedFence.ProviderFencingGeneration
            }
        };
        RuntimeRunStartContinuation.RequireReplacementRegistration(owner, changedFence, replacementBinding);
        var ordinaryStartFailure = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireRegistration(owner, changedFence, replacementBinding));
        Assert.Equal("runtime_start_workspace_not_empty", ordinaryStartFailure.Code);
    }

    [Fact]
    public void ReplacementPlacementRequiresANewPodAndTheExactRetainedWorkspace()
    {
        var source = RuntimeCopilotSessionTests.Registration();
        var binding = source.Binding with
        {
            AgentId = RuntimeActionContract.CoordinatorRootStepId,
            EnvironmentProviderFencingGeneration = source.Binding.EnvironmentCurrentFencingGeneration
        };
        var owner = new RuntimeOwnerContext(
            1, binding.ActorIssuer, binding.ActorId, binding.TenantId, binding.ProjectId,
            binding.RunId, binding.SessionId, binding.AgentId, binding.ModelSelectionReference!,
            binding.TurnId, binding.ProjectRevision, binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision, binding.ContextRevision, binding.AcceptedSelectionHash,
            binding.ExecutionFence, 0, 1, 1);
        var oldBootstrap = Guid.NewGuid();
        var previous = new RuntimeRunStartPlacementReceipt(
            1, RuntimeRunStartKeys.EnvironmentId(owner), "profile", "workspace",
            RuntimeRunStartKeys.SandboxIdempotencyKey(owner, oldBootstrap),
            4, 2, 1, Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "old", 4),
            3, 5, 6, 6, DateTimeOffset.UtcNow.AddMinutes(2))
        {
            PodUid = "old-pod"
        };
        var replacementId = Guid.NewGuid();
        var newBootstrap = RuntimeRunStartKeys.ReplacementOperationId(replacementId, "bootstrap");
        var current = previous with
        {
            SandboxIdempotencyKey = RuntimeRunStartKeys.SandboxIdempotencyKey(owner, newBootstrap),
            SandboxOperationId = Guid.NewGuid(),
            SandboxResource = new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "new", 5),
            LeaseRevision = previous.LeaseRevision,
            CurrentFencingGeneration = 7,
            ProviderFencingGeneration = 7,
            PodUid = "new-pod"
        };

        RuntimeRunStartContinuation.RequireReplacementPlacement(previous, current, newBootstrap, owner);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireReplacementPlacement(
                previous, current with { WorkspaceDataGeneration = 3 }, newBootstrap, owner));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireReplacementPlacement(
                previous, current with { SandboxOperationId = previous.SandboxOperationId }, newBootstrap, owner));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRunStartContinuation.RequireReplacementPlacement(
                previous, current with { PodUid = previous.PodUid }, newBootstrap, owner));
    }

    [Fact]
    public void OrdinaryReplacementCanReuseAnEnvironmentLeaseRevision()
    {
        var source = RuntimeCopilotSessionTests.Registration();
        var previous = source.Binding with
        {
            AgentId = RuntimeActionContract.CoordinatorRootStepId,
            PlacementUid = "previous-placement",
            PlacementGeneration = 2,
            EnvironmentLeaseRevision = 3,
            EnvironmentCurrentFencingGeneration = 5,
            EnvironmentProviderFencingGeneration = 5
        };
        var owner = new RuntimeOwnerContext(
            1, previous.ActorIssuer, previous.ActorId, previous.TenantId, previous.ProjectId,
            previous.RunId, previous.SessionId, previous.AgentId, previous.ModelSelectionReference!,
            previous.TurnId, previous.ProjectRevision, previous.ProjectConfigurationRevision,
            previous.PlatformRuntimeRevision, previous.ContextRevision, previous.AcceptedSelectionHash,
            previous.ExecutionFence, 0, 1, 1)
        {
            ModelCredentialReference = previous.ModelCredentialReference,
            ModelSourceMode = previous.ModelSourceMode,
            ReasoningEffort = previous.ReasoningEffort,
            ModelBindingPin = previous.ModelBindingPin,
            ModelConnectionId = previous.ModelConnectionId,
            ModelConnectionScope = previous.ModelConnectionScope,
            MaxPromptTokens = previous.MaxPromptTokens
        };
        var current = previous with
        {
            PlacementUid = "current-placement",
            PlacementGeneration = 3,
            EnvironmentCurrentFencingGeneration = 6,
            EnvironmentProviderFencingGeneration = 6
        };

        RuntimeRegistrationOwner.RequireOrdinaryReplacement(previous, owner, current);
    }

    [Fact]
    public async Task CleanupContinuationDoesNotUseAnExpiredInMemoryActor()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var credential = new SecretCredential(
            "expired-cancel-actor",
            clock.GetUtcNow().AddSeconds(1),
            clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var actor = new RuntimeActorAuthorization(credential, "tenant");
        var continuation = new RuntimeRunStartCleanupContinuation(
            null!,
            null!,
            null!,
            null!,
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            null!);

        try
        {
            await continuation.ContinueAsync(
                new RuntimeRunStartCleanupWork(
                    "project",
                    "run",
                    "tenant",
                    new CoordinationActor("https://issuer.test/", "actor"),
                    actor),
                CancellationToken.None);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task ContinuationDropsAnExpiredQueuedBearerBeforeReadingOrWritingIntent()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var credential = new SecretCredential(
            "expired-start-actor",
            clock.GetUtcNow().AddSeconds(1),
            clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        var continuation = new RuntimeRunStartContinuation(
            null!,
            null!,
            null!,
            null!,
            null!,
            clock);

        await continuation.ContinueAsync(
            null!,
            new RuntimeActorAuthorization(credential, "tenant"),
            CancellationToken.None);
    }

    [Theory]
    [InlineData("not-ready")]
    [InlineData("unavailable")]
    public async Task ReadyReplayRequiresCurrentHostReadiness(string hostState)
    {
        var (continuation, intent, actor, clients, credential) = CreateReadyReplay(hostState);
        using (clients)
        {
            try
            {
                var summary = await continuation.RevalidateReadyAsync(intent, actor, CancellationToken.None);

                Assert.Equal("pending", summary.State);
                Assert.Equal("readiness", summary.Step);
                Assert.Contains(clients.Requests, request =>
                    request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath == "/runtime/v1/readiness");
                Assert.Equal(2, clients.Requests.Count);
                Assert.All(clients.Requests, request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.NotNull(request.Headers.Authorization);
                    Assert.EndsWith("/readiness", request.RequestUri!.AbsolutePath);
                });
            }
            finally
            {
                credential.Invalidate();
            }
        }
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("wrong-runtime")]
    public async Task ReadyReplayRejectsRevokedOrMismatchedCurrentSourceEvidence(string sourceState)
    {
        var (continuation, intent, actor, clients, credential) = CreateReadyReplay(sourceState);
        using (clients)
        {
            try
            {
                var summary = await continuation.RevalidateReadyAsync(intent, actor, CancellationToken.None);

                Assert.Equal("blocked", summary.State);
                Assert.Equal("maf_execution_host_readiness_invalid", summary.FailureCode);
                Assert.Equal(2, clients.Requests.Count);
                Assert.All(clients.Requests, request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.NotNull(request.Headers.Authorization);
                    Assert.EndsWith("/readiness", request.RequestUri!.AbsolutePath);
                });
            }
            finally
            {
                credential.Invalidate();
            }
        }
    }

    [Fact]
    public void FullWorkQueueReturnsRetryableFailureAndInvalidatesItsBearer()
    {
        var credential = new SecretCredential("queue-test-actor", DateTimeOffset.UtcNow.AddMinutes(1));
        var actor = new RuntimeActorAuthorization(credential, "tenant");
        var work = new RuntimeRunStartContinueWork(null!, actor);
        var queue = new RuntimeRunStartWorkQueue();
        while (queue.TryEnqueue(work))
        {
        }

        var failure = Assert.Throws<CoordinationException>(() =>
            RuntimeRunStartDispatcher.EnqueueOrThrow(queue, work));

        Assert.Equal("runtime_start_queue_full", failure.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.False(credential.IsUsable());

        var cleanupCredential = new SecretCredential("cleanup-queue-test-actor", DateTimeOffset.UtcNow.AddMinutes(1));
        var cleanup = new RuntimeRunStartCleanupWork(
            "project", "run", "tenant", null!,
            new RuntimeActorAuthorization(cleanupCredential, "tenant"));
        var cleanupFailure = Assert.Throws<CoordinationException>(() =>
            RuntimeRunStartDispatcher.EnqueueOrThrow(queue, cleanup));

        Assert.Equal("runtime_start_queue_full", cleanupFailure.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, cleanupFailure.StatusCode);
        Assert.False(cleanupCredential.IsUsable());
    }

    [Fact]
    public async Task BackgroundWorkerAllowsDistinctStartIntentsToProgressConcurrently()
    {
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("parallel-test-actor", DateTimeOffset.UtcNow.AddMinutes(1)),
            "tenant");
        RuntimeOwnerContext Owner(string runId) => new(
            1,
            "https://issuer.test/",
            "actor",
            "tenant",
            "project",
            runId,
            $"root-{runId}",
            RuntimeActionContract.CoordinatorRootStepId,
            "model",
            "turn",
            1,
            1,
            1,
            "context",
            "selection",
            1,
            0,
            1,
            1);
        var firstWork = new RuntimeRunStartContinueWork(Owner("run-1"), actor);
        var secondWork = new RuntimeRunStartContinueWork(Owner("run-2"), actor);
        var observedRuns = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await RuntimeRunStartBackgroundService.ProcessAsync(
            TwoWorkItems(firstWork, secondWork),
            async (work, cancellationToken) =>
            {
                var start = Assert.IsType<RuntimeRunStartContinueWork>(work);
                observedRuns.Enqueue(start.Owner.RunId);
                if (start.Owner.RunId == "run-1")
                {
                    firstStarted.SetResult(true);
                    await secondStarted.Task.WaitAsync(cancellationToken);
                }
                else if (start.Owner.RunId == "run-2")
                {
                    secondStarted.SetResult(true);
                }
                else
                {
                    throw new Xunit.Sdk.XunitException($"Unexpected run '{start.Owner.RunId}'.");
                }
            },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(firstStarted.Task.IsCompletedSuccessfully);
        Assert.True(secondStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(["run-1", "run-2"], observedRuns.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FailedBootstrapRetiresItsExactAttemptBeforeStableRetryAndRejectsForeignReceipt()
    {
        var source = RuntimeCopilotSessionTests.Registration();
        var binding = source.Binding with
        {
            AgentId = RuntimeActionContract.CoordinatorRootStepId,
            EnvironmentProviderFencingGeneration = source.Binding.EnvironmentCurrentFencingGeneration,
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            PlacementProviderId = "sandbox"
        };
        var owner = RuntimeRunStartContinuation.CreateOwnerContext(binding);
        var environmentId = RuntimeRunStartKeys.EnvironmentId(owner);
        var connectionString = await _postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        const string schema = "runtime_run_start_test";
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, schema);
        var registrationStore = new RuntimeRegistrationStore(dataSource, schema, TimeProvider.System);
        var registration = await registrationStore.RegisterAsync(
            binding with { EnvironmentId = environmentId }, DateTimeOffset.UtcNow.AddMinutes(10), default);
        var bootstrapOperationId = Guid.NewGuid();
        var placement = new RuntimeRunStartPlacementReceipt(
            1,
            environmentId,
            registration.Binding.ProfileId,
            RuntimeRunStartKeys.WorkspaceVolumeId(owner),
            RuntimeRunStartKeys.SandboxIdempotencyKey(owner, bootstrapOperationId),
            1,
            0,
            1,
            Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "claim", 1),
            registration.Binding.EnvironmentLifecycleGeneration,
            registration.Binding.EnvironmentLeaseRevision,
            registration.Binding.EnvironmentCurrentFencingGeneration,
            registration.Binding.EnvironmentProviderFencingGeneration,
            DateTimeOffset.UtcNow.AddMinutes(2));
        placement.Validate();
        var intent = new RuntimeRunStartIntent(
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.SessionId,
            owner.AcceptedSelectionHash,
            owner.ExecutionFence,
            Guid.NewGuid(),
            bootstrapOperationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "pending",
            "retirement",
            "runtime_delivery_failed",
            environmentId,
            registration.RuntimeInstanceId,
            JsonSerializer.Serialize(new { placement, registration }, JsonOptions),
            false,
            1);
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await using var run = new NpgsqlCommand($"""
                INSERT INTO {schema}.accepted_runs
                    (project_id, run_id, accepted_selection, accepted_selection_hash,
                     accepted_by_issuer, accepted_by_subject)
                VALUES (@project, @run, @accepted_selection, @selection, @issuer, @actor)
                """, connection);
            run.Parameters.AddWithValue("project", intent.ProjectId);
            run.Parameters.AddWithValue("run", intent.RunId);
            run.Parameters.AddWithValue("accepted_selection", NpgsqlTypes.NpgsqlDbType.Jsonb, "{}");
            run.Parameters.AddWithValue("selection", intent.AcceptedSelectionHash);
            run.Parameters.AddWithValue("issuer", binding.ActorIssuer);
            run.Parameters.AddWithValue("actor", binding.ActorId);
            await run.ExecuteNonQueryAsync();

            await using var rootSession = new NpgsqlCommand($"""
                INSERT INTO {schema}.coordination_sessions
                    (project_id, run_id, session_id, root_session_id, node_kind,
                     writer_issuer, writer_subject, execution_fence)
                VALUES (@project, @run, @root, @root, 'coordinator', @issuer, @actor, @fence)
                """, connection);
            rootSession.Parameters.AddWithValue("project", intent.ProjectId);
            rootSession.Parameters.AddWithValue("run", intent.RunId);
            rootSession.Parameters.AddWithValue("root", intent.RootSessionId);
            rootSession.Parameters.AddWithValue("issuer", binding.ActorIssuer);
            rootSession.Parameters.AddWithValue("actor", binding.ActorId);
            rootSession.Parameters.AddWithValue("fence", intent.ExecutionFence);
            await rootSession.ExecuteNonQueryAsync();

            await using var savedIntent = new NpgsqlCommand($"""
                INSERT INTO {schema}.runtime_run_start_intents
                    (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                     execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                     exchange_operation_id, state, current_step, failure_code, environment_id,
                     runtime_instance_id, receipt_links, row_version)
                VALUES (@tenant, @project, @run, @root, @selection, @fence, @intent,
                        @bootstrap, @consume, @exchange, 'pending', 'retirement',
                        'runtime_delivery_failed', @environment, @runtime, @receipts, @version)
                """, connection);
            savedIntent.Parameters.AddWithValue("tenant", intent.TenantId);
            savedIntent.Parameters.AddWithValue("project", intent.ProjectId);
            savedIntent.Parameters.AddWithValue("run", intent.RunId);
            savedIntent.Parameters.AddWithValue("root", intent.RootSessionId);
            savedIntent.Parameters.AddWithValue("selection", intent.AcceptedSelectionHash);
            savedIntent.Parameters.AddWithValue("fence", intent.ExecutionFence);
            savedIntent.Parameters.AddWithValue("intent", intent.IntentId);
            savedIntent.Parameters.AddWithValue("bootstrap", intent.BootstrapOperationId);
            savedIntent.Parameters.AddWithValue("consume", intent.ConsumeOperationId);
            savedIntent.Parameters.AddWithValue("exchange", intent.ExchangeOperationId);
            savedIntent.Parameters.AddWithValue("environment", intent.EnvironmentId!);
            savedIntent.Parameters.AddWithValue("runtime", intent.RuntimeInstanceId!.Value);
            savedIntent.Parameters.AddWithValue(
                "receipts", NpgsqlTypes.NpgsqlDbType.Jsonb, intent.ReceiptLinks);
            savedIntent.Parameters.AddWithValue("version", intent.RowVersion);
            await savedIntent.ExecuteNonQueryAsync();
        }
        var store = new CoordinationRuntimeRunStartStore(dataSource, schema);
        RuntimeRunStartRetirementReceipt Retirement(string state, Guid? receiver = null) => new(
            1,
            bootstrapOperationId,
            receiver ?? registration.RuntimeInstanceId,
            registration.Revision,
            placement.SandboxOperationId,
            placement.SandboxResource.Generation,
            placement.ProviderFencingGeneration,
            placement.SandboxIdempotencyKey,
            state);

        var pendingFailure = await Assert.ThrowsAsync<CoordinationException>(() =>
            store.ReplaceFailedBootstrapAttemptAsync(
                intent, intent.RowVersion, Retirement("pending"), default));
        Assert.Equal("runtime_start_retirement_receipt_invalid", pendingFailure.Code);
        var foreignReceipt = Retirement("released", Guid.NewGuid());
        var foreignFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            store.ReplaceFailedBootstrapAttemptAsync(intent, intent.RowVersion, foreignReceipt, default));
        Assert.Equal("runtime_start_retirement_receipt_invalid", foreignFailure.Code);
        var unchanged = await store.ReadAsync(intent, default);
        Assert.NotNull(unchanged);
        Assert.Equal(intent.BootstrapOperationId, unchanged.BootstrapOperationId);
        Assert.Equal(intent.ConsumeOperationId, unchanged.ConsumeOperationId);
        Assert.Equal(intent.ExchangeOperationId, unchanged.ExchangeOperationId);
        Assert.Equal(intent.RowVersion, unchanged.RowVersion);

        using var ownerClients = new RuntimeRunStartRecoveryClientFactory();
        var continuation = new RuntimeRunStartContinuation(
            store,
            new RuntimeRegistrationOwner(
                null!, null!, null!, null!, null!, null!, registrationStore, null!, TimeProvider.System),
            null!,
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            ownerClients,
            TimeProvider.System);
        var credential = new SecretCredential("recovery-test-actor", DateTimeOffset.UtcNow.AddMinutes(1));
        var actor = new RuntimeActorAuthorization(credential, owner.TenantId);
        try
        {
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                continuation.ContinueAsync(owner, actor, CancellationToken.None));
            var firstRecoveryState = await store.ReadAsync(intent, CancellationToken.None);
            Assert.NotNull(firstRecoveryState);
            Assert.True(
                intent.BootstrapOperationId != firstRecoveryState.BootstrapOperationId,
                $"Retirements={ownerClients.RetirementRequests.Count}; " +
                $"placements={ownerClients.PlacementRequests.Count}; " +
                $"state={firstRecoveryState.State}; step={firstRecoveryState.CurrentStep}; " +
                $"failure={firstRecoveryState.FailureCode}.");
            Assert.NotEqual(intent.ConsumeOperationId, firstRecoveryState.ConsumeOperationId);
            Assert.NotEqual(intent.ExchangeOperationId, firstRecoveryState.ExchangeOperationId);
            Assert.Equal("pending", firstRecoveryState.State);
            Assert.Equal("placement", firstRecoveryState.CurrentStep);
            Assert.Equal("runtime_delivery_pending", firstRecoveryState.FailureCode);

            var savedRetirement = Assert.Single(ownerClients.RetirementRequests);
            Assert.Equal(intent.BootstrapOperationId, savedRetirement.BootstrapOperationId);
            Assert.Equal(registration.RuntimeInstanceId, savedRetirement.RuntimeInstanceId);
            Assert.Equal(registration.Revision, savedRetirement.RegistrationRevision);
            var firstPlacementAttempt = Assert.Single(ownerClients.PlacementRequests);
            Assert.Equal(firstRecoveryState.BootstrapOperationId, firstPlacementAttempt.BootstrapOperationId);
            // The released attempt's registration is revoked before the attempt is replaced.
            Assert.Equal(RuntimeRegistrationState.Revoked,
                (await registrationStore.ReadAsync(registration.RuntimeInstanceId, default))!.State);

            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                continuation.ContinueAsync(owner, actor, CancellationToken.None));
            var replayed = await store.ReadAsync(intent, CancellationToken.None);
            Assert.NotNull(replayed);
            Assert.Equal(firstRecoveryState.BootstrapOperationId, replayed.BootstrapOperationId);
            Assert.Equal(firstRecoveryState.ConsumeOperationId, replayed.ConsumeOperationId);
            Assert.Equal(firstRecoveryState.ExchangeOperationId, replayed.ExchangeOperationId);
            Assert.Single(ownerClients.RetirementRequests);
            Assert.Collection(
                ownerClients.PlacementRequests,
                request => Assert.Equal(firstRecoveryState.BootstrapOperationId, request.BootstrapOperationId),
                request => Assert.Equal(firstRecoveryState.BootstrapOperationId, request.BootstrapOperationId));
        }
        finally
        {
            credential.Invalidate();
        }

        var recoveredIntent = await store.ReadAsync(intent, CancellationToken.None);
        Assert.NotNull(recoveredIntent);
        Assert.NotEqual(intent.BootstrapOperationId, recoveredIntent.BootstrapOperationId);
        Assert.NotEqual(intent.ConsumeOperationId, recoveredIntent.ConsumeOperationId);
        Assert.NotEqual(intent.ExchangeOperationId, recoveredIntent.ExchangeOperationId);
        Assert.Equal("pending", recoveredIntent.State);
        Assert.Equal("placement", recoveredIntent.CurrentStep);
        Assert.Equal("runtime_delivery_pending", recoveredIntent.FailureCode);
        using var retiredLinks = JsonDocument.Parse(recoveredIntent.ReceiptLinks);
        Assert.False(retiredLinks.RootElement.TryGetProperty("placement", out _));
        Assert.False(retiredLinks.RootElement.TryGetProperty("registration", out _));
        var retired = retiredLinks.RootElement.GetProperty("retiredBootstrap");
        Assert.Equal(intent.BootstrapOperationId, retired.GetProperty("bootstrapOperationId").GetGuid());
        Assert.Equal(registration.RuntimeInstanceId,
            retired.GetProperty("registration").GetProperty("runtimeInstanceId").GetGuid());
        Assert.Equal("released", retired.GetProperty("retirement").GetProperty("state").GetString());
    }

    [Fact]
    public async Task ChildBlockedAfterBootstrapDeliveryFailureKeepsLinksAndIsNotRetried()
    {
        const string issuer = "https://identity.example/";
        const string actorId = "child-owner";
        const string tenant = "tenant-1";
        const string project = "project-1";
        const string run = "run-1";
        const string root = "root";
        const string child = "child-1";
        var selectionHash = new string('a', 64);
        var connectionString = await _postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        const string schema = "runtime_child_retirement_test";
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, schema);
        var binding = new RuntimeChildStartBinding(
            root, child, "plan-1", "association-1", null, "step-1", "worker", "model-ref", issuer, actorId,
            "context-1", 1, 2, Guid.NewGuid(), Guid.NewGuid(), new string('b', 64));
        var links = JsonSerializer.Serialize(new
        {
            childStart = binding,
            placement = new { marker = "placement" },
            registration = new { marker = "registration" }
        }, JsonOptions);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO {schema}.accepted_runs
                (project_id, run_id, tenant_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject)
            VALUES (@project, @run, @tenant, '{"{}"}'::jsonb, @selection, @issuer, @actor);
            INSERT INTO {schema}.coordination_sessions
                (project_id, run_id, session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence)
            VALUES (@project, @run, @root, @root, 'coordinator', @issuer, @actor, 1);
            INSERT INTO {schema}.coordination_sessions
                (project_id, run_id, session_id, parent_session_id, root_session_id, node_kind,
                 writer_issuer, writer_subject, execution_fence, logical_turn_ordinal, turn_state, state_version)
            VALUES (@project, @run, @child, @root, @root, 'child_work', @issuer, @actor, 1, 1, 'active', 2);
            INSERT INTO {schema}.coordinator_decisions
                (project_id, run_id, session_id, request_id, decision_id, actor_issuer, actor_subject,
                 execution_fence, state_version, action_kind, idempotency_key, command_hash,
                 decision_state, decision)
            VALUES (@project, @run, @root, 'request-1', @decision, @issuer, @actor, 1, 1, 'spawn',
                    'decision-1', repeat('c', 64), 'accepted', '{"{}"}'::jsonb);
            INSERT INTO {schema}.runtime_run_start_intents
                (tenant_id, project_id, run_id, root_session_id, accepted_selection_hash,
                 execution_fence, intent_id, bootstrap_operation_id, consume_operation_id,
                 exchange_operation_id, state, current_step, failure_code, receipt_links)
            VALUES (@tenant, @project, @run, @child, @selection, 1, @intent, @bootstrap, @consume,
                    @exchange, 'blocked', 'retirement', 'runtime_delivery_failed', @links);
            """, connection))
        {
            seed.Parameters.AddWithValue("project", project);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("tenant", tenant);
            seed.Parameters.AddWithValue("selection", selectionHash);
            seed.Parameters.AddWithValue("issuer", issuer);
            seed.Parameters.AddWithValue("actor", actorId);
            seed.Parameters.AddWithValue("root", root);
            seed.Parameters.AddWithValue("child", child);
            seed.Parameters.AddWithValue("decision", Guid.NewGuid());
            seed.Parameters.AddWithValue("intent", Guid.NewGuid());
            seed.Parameters.AddWithValue("bootstrap", Guid.NewGuid());
            seed.Parameters.AddWithValue("consume", Guid.NewGuid());
            seed.Parameters.AddWithValue("exchange", Guid.NewGuid());
            seed.Parameters.AddWithValue("links", NpgsqlTypes.NpgsqlDbType.Jsonb, links);
            await seed.ExecuteNonQueryAsync();
        }
        var owner = new RuntimeOwnerContext(
            1, issuer, actorId, tenant, project, run, child, "worker", "model-ref", "turn-1", 1, 1, 1,
            "context-1", selectionHash, 1, 1, 2, 1)
        {
            WorkflowStepId = "step-1"
        };
        var store = new CoordinationRuntimeRunStartStore(dataSource, schema);
        var before = await store.EnsureChildIntentAsync(owner, CancellationToken.None);
        using var clients = new RuntimeRunStartRecoveryClientFactory();
        var continuation = new RuntimeRunStartContinuation(
            store, null!, null!,
            new RuntimeRunStartOptions(new Uri("https://environment.test/"), new Uri("https://broker.test/")),
            clients, TimeProvider.System);
        var credential = new SecretCredential("child-retirement-actor", DateTimeOffset.UtcNow.AddMinutes(1));
        try
        {
            await continuation.ContinueChildAsync(
                owner, new RuntimeActorAuthorization(credential, tenant), CancellationToken.None);
        }
        finally
        {
            credential.Invalidate();
        }

        // The used bootstrap operation is not retried; the links stay for terminal cleanup.
        Assert.Empty(clients.PlacementRequests);
        Assert.Empty(clients.RetirementRequests);
        var after = await store.ReadAsync(before, CancellationToken.None);
        Assert.Equal(before, after);
        Assert.Equal(("blocked", "retirement"), (after!.State, after.CurrentStep));
        using var saved = JsonDocument.Parse(after.ReceiptLinks);
        Assert.True(saved.RootElement.TryGetProperty("placement", out _));
        Assert.True(saved.RootElement.TryGetProperty("registration", out _));
    }

    private static (
        RuntimeRunStartContinuation Continuation,
        RuntimeRunStartIntent Intent,
        RuntimeActorAuthorization Actor,
        ReadyReplayClientFactory Clients,
        SecretCredential Credential) CreateReadyReplay(string hostState)
    {
        var clients = new ReadyReplayClientFactory(hostState);
        var source = RuntimeCopilotSessionTests.Registration();
        var image = new SandboxImageIdentity($"sha256:{new string('b', 64)}", "linux/amd64", 1024);
        var binding = source.Binding with
        {
            ConfigureEndpoint = new Uri("https://runtime.test/configure"),
            EnvironmentProviderFencingGeneration = source.Binding.EnvironmentCurrentFencingGeneration,
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            Image = image
        };
        var registration = source with { Binding = binding };
        var placement = new RuntimeRunStartPlacementReceipt(
            1,
            binding.EnvironmentId,
            binding.ProfileId,
            "workspace",
            "sandbox-key",
            1,
            0,
            1,
            Guid.NewGuid(),
            new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "claim", 1),
            binding.EnvironmentLifecycleGeneration,
            binding.EnvironmentLeaseRevision,
            binding.EnvironmentCurrentFencingGeneration,
            binding.EnvironmentProviderFencingGeneration,
            DateTimeOffset.UtcNow.AddMinutes(2));
        var links = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
        {
            ["placement"] = JsonSerializer.SerializeToElement(placement, JsonOptions),
            ["registration"] = JsonSerializer.SerializeToElement(registration, JsonOptions)
        }, JsonOptions);
        var intent = new RuntimeRunStartIntent(
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ready",
            "readiness",
            null,
            binding.EnvironmentId,
            registration.RuntimeInstanceId,
            links,
            false,
            1);
        var credential = new SecretCredential("ready-replay-token", DateTimeOffset.UtcNow.AddMinutes(1));
        var actor = new RuntimeActorAuthorization(credential, binding.TenantId);
        var environment = new RuntimeEnvironmentContextClient(
            clients.CreateClient("runtime-run-start-environment"),
            new RuntimeRegistrationOwnerOptions(new Uri("https://environment.test/")));
        var continuation = new RuntimeRunStartContinuation(
            null!,
            null!,
            environment,
            new RuntimeRunStartOptions(
                new Uri("https://environment.test/"),
                new Uri("https://broker.test/")),
            clients,
            TimeProvider.System);
        clients.Readiness = new RuntimeHostReadinessReceipt(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            binding.ExecutionFence,
            image,
            ImmutableArray.CreateRange(Enum.GetValues<SandboxStartupPhase>().Select(phase =>
                new SandboxStartupPhaseObservation(
                    phase,
                    1,
                    DateTimeOffset.UtcNow,
                    phase == SandboxStartupPhase.ImageReady ? image.Digest : null,
                    phase == SandboxStartupPhase.ImageReady ? image.CompressedPullBytes : null))),
            new RuntimeGrantReceipt(
                Guid.NewGuid(),
                hostState == "wrong-runtime" ? Guid.NewGuid() : registration.RuntimeInstanceId,
                registration.Revision,
                1,
                "https://runtime.test/",
                RuntimeCredentialPurpose.Observe,
                binding.ObservationEndpoint,
                hostState == "revoked" ? RuntimeCredentialState.Revoked : RuntimeCredentialState.Active,
                "configuration-hash",
                DateTimeOffset.UtcNow.AddMinutes(2),
                DateTimeOffset.UtcNow));
        return (continuation, intent, actor, clients, credential);
    }

    private static async IAsyncEnumerable<RuntimeRunStartWork> TwoWorkItems(
        RuntimeRunStartWork first,
        RuntimeRunStartWork second)
    {
        yield return first;
        await Task.Yield();
        yield return second;
    }

    private sealed class RuntimeRunStartRecoveryClientFactory : IHttpClientFactory, IDisposable
    {
        private static readonly JsonSerializerOptions OwnerJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        private readonly HttpClient _client;

        public List<RuntimeRunStartRetirementRequest> RetirementRequests { get; } = [];
        public List<RuntimeRunStartPlacementRequest> PlacementRequests { get; } = [];

        public RuntimeRunStartRecoveryClientFactory() =>
            _client = new HttpClient(new Handler(this), disposeHandler: true);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();

        private sealed class Handler(RuntimeRunStartRecoveryClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath == "/internal/runtime/run-start/retire-failed")
                {
                    var input = await request.Content!.ReadFromJsonAsync<RuntimeRunStartRetirementRequest>(
                        OwnerJsonOptions, cancellationToken);
                    Assert.NotNull(input);
                    owner.RetirementRequests.Add(input);
                    var placement = input.Placement;
                    return JsonResponse(new RuntimeRunStartRetirementReceipt(
                        1,
                        input.BootstrapOperationId,
                        input.RuntimeInstanceId,
                        input.RegistrationRevision,
                        placement.SandboxOperationId,
                        placement.SandboxResource.Generation,
                        placement.ProviderFencingGeneration,
                        placement.SandboxIdempotencyKey,
                        "released"), request);
                }

                if (request.RequestUri?.AbsolutePath == "/internal/runtime/run-start/placement")
                {
                    var input = await request.Content!.ReadFromJsonAsync<RuntimeRunStartPlacementRequest>(
                        OwnerJsonOptions, cancellationToken);
                    Assert.NotNull(input);
                    owner.PlacementRequests.Add(input);
                    return JsonResponse(
                        new { code = "runtime_delivery_pending" },
                        request,
                        HttpStatusCode.ServiceUnavailable);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }

            private static HttpResponseMessage JsonResponse<T>(
                T value,
                HttpRequestMessage request,
                HttpStatusCode status = HttpStatusCode.OK)
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(value, options: JsonOptions),
                    RequestMessage = request
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }
        }
    }

    private sealed class ReadyReplayClientFactory : IHttpClientFactory, IDisposable
    {
        private static readonly JsonSerializerOptions RuntimeJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        private readonly HttpClient _client;

        public ReadyReplayClientFactory(string hostState)
        {
            HostState = hostState;
            _client = new HttpClient(new Handler(this), disposeHandler: true);
        }

        public string HostState { get; }
        public List<HttpRequestMessage> Requests { get; } = [];
        public RuntimeHostReadinessReceipt Readiness { get; set; } = null!;

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();

        private sealed class Handler(ReadyReplayClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner.Requests.Add(request);
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/runtime/v1/readiness")
                {
                    if (owner.HostState == "unavailable")
                        throw new HttpRequestException("Host readiness is unavailable.");
                    if (owner.HostState == "not-ready")
                        return Task.FromResult(JsonResponse(
                            new { code = "runtime_host_not_ready" },
                            request,
                            HttpStatusCode.Forbidden));
                    return Task.FromResult(JsonResponse(owner.Readiness, request));
                }

                if (path.EndsWith("/runtime-bootstrap/profiles/profile/readiness", StringComparison.Ordinal))
                    return Task.FromResult(JsonResponse(
                        new EnvironmentRuntimeReadinessContext(1, null!, DateTimeOffset.UtcNow, null!, null!, "/workspace"),
                        request));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }

            private static HttpResponseMessage JsonResponse<T>(
                T value,
                HttpRequestMessage request,
                HttpStatusCode status = HttpStatusCode.OK)
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(value, options: RuntimeJsonOptions),
                    RequestMessage = request
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
